using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using AzureBank.Bff;
using AzureBank.Bff.Extensions;
using AzureBank.Bff.Middleware;
using AzureBank.Bff.Options;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using AzureBank.Bff.Services;
using AzureBank.Bff.Services.Implementations;
using AzureBank.Bff.Services.Interfaces;
using AzureBank.Bff.Transforms;
using AzureBank.Shared.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Sinks.OpenTelemetry;

// ═══════════════════════════════════════════════════════════════════════════
// BOOTSTRAP LOGGER (captures startup errors before config is loaded)
// ═══════════════════════════════════════════════════════════════════════════

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting AzureBank BFF Gateway...");

    var builder = WebApplication.CreateBuilder(args);

    // ═══════════════════════════════════════════════════════════════════════════
    // CONFIGURATION BINDING
    // ═══════════════════════════════════════════════════════════════════════════

    builder.Services.AddOptions<BffSessionOptions>()
        .Bind(builder.Configuration.GetSection(BffSessionOptions.SectionName))
        .ValidateOnStart();
    builder.Services.AddSingleton<IValidateOptions<BffSessionOptions>, BffSessionOptionsValidator>();
    builder.Services.Configure<SecurityOptions>(
        builder.Configuration.GetSection(SecurityOptions.SectionName));

    // Outside Development the session cookie carries the __Host- prefix (RFC 6265bis):
    // the browser refuses to store it unless it arrives Secure, with Path=/ and no
    // Domain — unforgeable from subdomains or insecure origins. Applied at runtime so
    // config keeps one canonical name and every reader (controller, middleware, YARP
    // transform, rate-limit partitioner) picks it up through IOptions. Development stays
    // unprefixed: the dev loop runs on http://localhost, where prefixed cookies cannot
    // be set at all (and Safari refuses Secure cookies there even unprefixed).
    if (!builder.Environment.IsDevelopment())
    {
        builder.Services.PostConfigure<BffSessionOptions>(options =>
        {
            // A null/empty name (config explicitly nulling it) falls through untouched:
            // PostConfigure runs BEFORE validation, so the BffSessionOptionsValidator
            // then fails startup with a clear message instead of an NRE here.
            if (!string.IsNullOrEmpty(options.CookieName)
                && !options.CookieName.StartsWith("__Host-", StringComparison.Ordinal))
            {
                options.CookieName = "__Host-" + options.CookieName.TrimStart('.');
            }
        });
    }

    // Security-relevant config is validated at STARTUP (ADR-0013): a non-positive rate-limit
    // value or a typo'd proxy IP must stop the app rather than silently disable the control
    // at request time — the limiter builds its windows lazily inside the partition factory,
    // and an unparseable proxy IP is otherwise just skipped, leaving XFF untrusted.
    builder.Services.AddOptions<RateLimitingOptions>()
        .Bind(builder.Configuration.GetSection(RateLimitingOptions.SectionName))
        .ValidateOnStart();
    builder.Services.AddSingleton<IValidateOptions<RateLimitingOptions>, RateLimitingOptionsValidator>();

    builder.Services.AddOptions<ProxyOptions>()
        .Bind(builder.Configuration.GetSection(ProxyOptions.SectionName))
        .ValidateOnStart();
    builder.Services.AddSingleton<IValidateOptions<ProxyOptions>, ProxyOptionsValidator>();

    // ═══════════════════════════════════════════════════════════════════════════
    // SERILOG CONFIGURATION (reads from appsettings.json)
    // ═══════════════════════════════════════════════════════════════════════════

    // preserveStaticLogger: true keeps the static bootstrap logger untouched instead of freezing
    // it into the host logger. Freezing is a one-shot on a process-wide static: with multiple
    // in-process hosts (WebApplicationFactory in tests) the second host build throws "The logger
    // is already frozen", which the top-level catch below turns into a silent failed startup
    // ("entry point exited without ever building an IHost"). The DI-registered logger still gets
    // the full appsettings configuration.
    builder.Host.UseSerilog((context, services, configuration) =>
    {
        configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services);

        // Export logs over OTLP (Loki) with automatic trace_id/span_id correlation. Gated on the
        // same env var as the SDK exporter so tests stay quiet; the sink resolves the /v1/logs
        // path from OTEL_EXPORTER_OTLP_ENDPOINT — do NOT set Endpoint here (a programmatic
        // endpoint skips the path append and the bare URL 404s silently). See the API for detail.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")))
        {
            configuration.WriteTo.OpenTelemetry(o =>
            {
                o.Protocol = OtlpProtocol.HttpProtobuf;
                // Mirror the SDK resource identity so Grafana joins logs to traces/metrics per-instance.
                o.ResourceAttributes = new Dictionary<string, object>
                {
                    ["service.name"] = AzureBank.Bff.Extensions.ObservabilityServiceCollectionExtensions.ServiceName,
                    ["service.version"] = typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0",
                    ["service.namespace"] = "azurebank",
                    ["service.instance.id"] = Environment.GetEnvironmentVariable("HOSTNAME") ?? Environment.MachineName,
                    ["deployment.environment.name"] = context.HostingEnvironment.EnvironmentName,
                };
                o.IncludedData = IncludedData.TraceIdField | IncludedData.SpanIdField
                               | IncludedData.MessageTemplateTextAttribute;
            });
        }
    },
        preserveStaticLogger: true);

    // ═══════════════════════════════════════════════════════════════════════════
    // SERVICE REGISTRATION
    // ═══════════════════════════════════════════════════════════════════════════

    // Controllers
    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();

    // Session services (singleton - shared across requests)
    builder.Services.AddSingleton<ITokenStoreService, InMemoryTokenStore>();
    builder.Services.AddSingleton<ISessionService, SessionService>();

    // Background services
    builder.Services.AddHostedService<SessionCleanupService>();

    // HTTP client for backend API
    builder.Services.AddHttpClient("BackendApi", client =>
    {
        client.BaseAddress = new Uri(builder.Configuration["BackendApi:BaseUrl"]!);
        client.DefaultRequestHeaders.Add("Accept", "application/json");
    })
    .ConfigurePrimaryHttpMessageHandler(() =>
    {
        // Accept self-signed certs in development
        var handler = new HttpClientHandler();
        if (builder.Environment.IsDevelopment())
        {
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }
        return handler;
    });

    // YARP Reverse Proxy with Bearer token transform
    builder.Services.AddReverseProxy()
        .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
        .AddTransforms<BearerTokenTransformProvider>();

    // Observability (OpenTelemetry traces + metrics, health probes). Captures the YARP
    // forwarder span so a proxied request is one trace spanning BFF -> API. Export is opt-in
    // via OTEL_EXPORTER_OTLP_ENDPOINT (see the extension).
    builder.Services.AddObservability(builder.Environment);

    // Forwarded headers (ADR-0013): the rate limiter partitions on the connection IP, which
    // behind a proxy is the PROXY's IP unless we rewrite it from X-Forwarded-For. Trusting
    // X-Forwarded-For from ANY source lets an attacker spoof/rotate IPs and defeat the per-IP
    // limiter (worse than proxy-collapse), so we honour it ONLY when the real proxy IPs are
    // configured (ForwardedHeaders:KnownProxies). Default — none configured, e.g. local/dev
    // where the BFF is the edge — do NOT process X-Forwarded-For; partition on the direct
    // connection IP (fail-safe). Deployments behind a proxy MUST set KnownProxies.
    var proxyConfig = builder.Configuration
        .GetSection(ProxyOptions.SectionName).Get<ProxyOptions>() ?? new ProxyOptions();
    var trustForwardedFor = proxyConfig.KnownProxies.Length > 0;
    if (trustForwardedFor)
    {
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
            options.ForwardLimit = proxyConfig.ForwardLimit;
            // Drop the loopback-only defaults; trust ONLY the configured proxies.
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var proxy in proxyConfig.KnownProxies)
            {
                // ProxyOptionsValidator already failed startup on an unparseable entry, so
                // nothing is silently dropped here.
                if (IPAddress.TryParse(proxy, out var ip))
                {
                    options.KnownProxies.Add(ip);
                }
            }
        });
    }

    // Rate limiting (ADR-0013): per-client-IP. A generous GlobalLimiter is the anti-DoS
    // baseline applied to ALL traffic — including the YARP-proxied /api/* bypass — while a
    // tight "auth" policy guards login/register against brute-force + enumeration at scale.
    // Rejections return 429 + Retry-After + a ProblemDetails matching the API's error shape.
    var rateLimiting = builder.Configuration
        .GetSection(RateLimitingOptions.SectionName).Get<RateLimitingOptions>() ?? new RateLimitingOptions();

    // Partition key. IPv6 end sites are handed a whole /64 (often more), so keying on the
    // full address would let an attacker rotate addresses inside their OWN allocation — no
    // spoofing required — and the per-IP limit would evaporate. Key IPv6 on its /64 prefix;
    // IPv4 keys on the full address.
    static string ClientIp(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress;
        if (ip is null)
        {
            return "unknown";
        }
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }
        if (ip.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return ip.ToString();
        }

        var bytes = ip.GetAddressBytes();
        Array.Clear(bytes, 8, 8); // zero the interface identifier -> the /64 prefix
        return new IPAddress(bytes) + "/64";
    }

    // Recipient lookup is limited per AUTHENTICATED USER, not per IP: registration is open,
    // so an attacker's cost unit is the throwaway account, not the address (ADR-0014).
    // Resolve the session cookie -> user id; fall back to the client IP for calls without a
    // session (which the API 401s regardless).
    static string LookupPartitionKey(HttpContext context)
    {
        var cookieName = context.RequestServices
            .GetRequiredService<IOptions<BffSessionOptions>>().Value.CookieName;
        if (context.Request.Cookies.TryGetValue(cookieName, out var sessionId)
            && !string.IsNullOrEmpty(sessionId))
        {
            var session = context.RequestServices.GetRequiredService<ISessionService>().GetSession(sessionId);
            if (session is not null)
            {
                return "user:" + session.UserId;
            }
        }
        return "ip:" + ClientIp(context);
    }

    builder.Services.AddRateLimiter(options =>
    {
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            RateLimitPartition.GetFixedWindowLimiter(ClientIp(context), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = rateLimiting.GlobalPermitLimit,
                Window = TimeSpan.FromSeconds(rateLimiting.GlobalWindowSeconds),
                QueueLimit = 0,
            }));

        // SLIDING window for the credential endpoints. A fixed window lets a client spend the
        // whole quota at the end of one window and again at the start of the next — ~2x the
        // limit within a couple of seconds, which is precisely the burst an anti-brute-force
        // control must not gift. (The global baseline above stays a fixed window: it is a
        // coarse anti-DoS backstop where the boundary burst is irrelevant.) One policy => ONE
        // bucket shared by bff/auth/{login,register} and the YARP /api/auth/* routes, which
        // is deliberate: that shared budget IS the per-IP enumeration/brute-force allowance.
        options.AddPolicy(RateLimitPolicies.Auth, context =>
            RateLimitPartition.GetSlidingWindowLimiter(ClientIp(context), _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = rateLimiting.AuthPermitLimit,
                Window = TimeSpan.FromSeconds(rateLimiting.AuthWindowSeconds),
                SegmentsPerWindow = rateLimiting.AuthSegmentsPerWindow,
                QueueLimit = 0,
            }));

        // Recipient lookup (/api/users/*): tight, per-user, sliding (ADR-0014).
        options.AddPolicy(RateLimitPolicies.Lookup, context =>
            RateLimitPartition.GetSlidingWindowLimiter(LookupPartitionKey(context), _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = rateLimiting.LookupPermitLimit,
                Window = TimeSpan.FromSeconds(rateLimiting.LookupWindowSeconds),
                SegmentsPerWindow = rateLimiting.AuthSegmentsPerWindow,
                QueueLimit = 0,
            }));

        options.OnRejected = async (context, cancellationToken) =>
        {
            var response = context.HttpContext.Response;
            response.StatusCode = StatusCodes.Status429TooManyRequests;
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            {
                response.Headers.RetryAfter =
                    ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                // Not every limiter advertises a retry time — the sliding window used for the
                // auth policy does not, unlike the fixed-window global. Fall back to that
                // window: conservative, and never tells a client to retry sooner than a permit
                // can actually free (RFC 9110 §10.2.3). A 429 without Retry-After is a poor
                // contract, and the API's lockout responses already always carry one.
                response.Headers.RetryAfter =
                    rateLimiting.AuthWindowSeconds.ToString(CultureInfo.InvariantCulture);
            }
            response.Headers.CacheControl = "no-store"; // RFC 6585 §4: a 429 must not be cached

            // This limiter IS the anti-brute-force control (ADR-0013), so a burst that trips
            // it is exactly the signal an operator must be able to alert on. Rejecting
            // silently would ship the control with no telemetry.
            Log.Warning(
                "SecurityEvent {SecurityEvent}: {Method} {Path} rejected for partition {Partition}",
                "RateLimitExceeded",
                context.HttpContext.Request.Method,
                context.HttpContext.Request.Path.Value,
                ClientIp(context.HttpContext));

            var problem = new ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too Many Requests",
                Detail = "Too many requests. Please retry later.",
                Type = "https://httpstatuses.com/429",
                Instance = context.HttpContext.Request.Path,
            };
            problem.Extensions["errorCode"] = ErrorCodes.RateLimitExceeded;
            // Bare 32-hex trace id — pastes straight into Tempo/Grafana search.
            problem.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier;

            await response.WriteAsJsonAsync(problem, cancellationToken);
        };
    });

    // NO CORS — deliberately (ADR-0018). The browser only ever talks to the BFF from its
    // own origin: dev = Vite server.proxy forwards /api and /bff, prod = the BFF serves
    // the SPA itself. The former "AllowFrontend" policy credential-whitelisted
    // http://localhost:5173 IN PRODUCTION — a live cross-site hole, not dead config.
    // Same-origin also makes WithExposedHeaders(Idempotency-Replayed) unnecessary:
    // the browser exposes all response headers on same-origin requests.

    // ═══════════════════════════════════════════════════════════════════════════
    // BUILD APPLICATION
    // ═══════════════════════════════════════════════════════════════════════════

    var app = builder.Build();

    // ═══════════════════════════════════════════════════════════════════════════
    // MIDDLEWARE PIPELINE (ORDER MATTERS!)
    // ═══════════════════════════════════════════════════════════════════════════

    // 0. Rewrite RemoteIpAddress from X-Forwarded-For BEFORE anything reads it (logging,
    // the rate limiter). Only runs when trusted proxies are configured (see above).
    if (trustForwardedFor)
    {
        app.UseForwardedHeaders();
    }

    // 1. Serilog request logging
    app.UseSerilogRequestLogging(options =>
    {
        options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000}ms";
    });

    // 2. Security headers (OWASP)
    app.UseSecurityHeaders();

    // 3. Fetch-Metadata isolation: a cross-site state-changing request stops here —
    // before it can refresh session activity, consume rate-limit budget, or reach the
    // controllers/proxy.
    app.UseFetchMetadata();

    // 4. Session activity tracking (updates LastActivity on every request except the
    // session-status probe)
    app.UseSessionActivity();

    // 5. Rate limiting
    app.UseRateLimiter();

    // 6. Auth level enforcement for sensitive routes (step-up authentication)
    app.UseAuthLevelEnforcement();

    // 7. Map controllers (BffAuthController)
    app.MapControllers();

    // 8. Map YARP reverse proxy (for /api/* routes)
    app.MapReverseProxy();

    // 9. Health probes (observability): liveness = process up; readiness = backend API reachable.
    // Excluded from the rate limiter — an orchestrator's probes must never be throttled.
    app.MapHealthChecks("/health/live",
        new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = _ => false })
        .DisableRateLimiting();
    app.MapHealthChecks("/health/ready",
        new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = h => h.Tags.Contains("ready") })
        .DisableRateLimiting();

    // ═══════════════════════════════════════════════════════════════════════════
    // RUN APPLICATION
    // ═══════════════════════════════════════════════════════════════════════════

    Log.Information("AzureBank BFF Gateway started successfully");
    app.Run();
}
// HostAbortedException is how host-building tooling stops the app on purpose — it is not a
// failure, so it must not set a failing exit code.
catch (Exception ex) when (ex.GetType().Name is not "HostAbortedException")
{
    // Log AND fail. Without a non-zero exit code a failed startup — including the
    // ValidateOnStart security-config checks — looks like a clean shutdown to an
    // orchestrator, which is exactly the silent failure those checks exist to prevent.
    Log.Fatal(ex, "BFF Gateway terminated unexpectedly");
    Environment.ExitCode = 1;
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>Exposed so the WebApplicationFactory in AzureBank.Bff.Tests can host the app.</summary>
public partial class Program { }
