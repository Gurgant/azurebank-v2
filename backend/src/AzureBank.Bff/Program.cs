using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using AzureBank.Bff;
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
using Microsoft.Extensions.Options;
using Serilog;

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

    builder.Services.Configure<BffSessionOptions>(
        builder.Configuration.GetSection(BffSessionOptions.SectionName));
    builder.Services.Configure<SecurityOptions>(
        builder.Configuration.GetSection(SecurityOptions.SectionName));

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

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services));

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

    static string ClientIp(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    builder.Services.AddRateLimiter(options =>
    {
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            RateLimitPartition.GetFixedWindowLimiter(ClientIp(context), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = rateLimiting.GlobalPermitLimit,
                Window = TimeSpan.FromSeconds(rateLimiting.GlobalWindowSeconds),
                QueueLimit = 0,
            }));

        options.AddPolicy(RateLimitPolicies.Auth, context =>
            RateLimitPartition.GetFixedWindowLimiter(ClientIp(context), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = rateLimiting.AuthPermitLimit,
                Window = TimeSpan.FromSeconds(rateLimiting.AuthWindowSeconds),
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

            var problem = new ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too Many Requests",
                Detail = "Too many requests. Please retry later.",
                Type = "https://httpstatuses.com/429",
                Instance = context.HttpContext.Request.Path,
            };
            problem.Extensions["errorCode"] = ErrorCodes.RateLimitExceeded;
            problem.Extensions["traceId"] = Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;

            await response.WriteAsJsonAsync(problem, cancellationToken);
        };
    });

    // CORS for frontend
    builder.Services.AddCors(options =>
    {
        options.AddPolicy("AllowFrontend", policy =>
        {
            policy.WithOrigins(
                    "http://localhost:5173",  // Vite dev server
                    "http://localhost:3000",  // Alternative frontend port
                    "https://localhost:5173")
                .AllowCredentials()
                .AllowAnyHeader()
                .AllowAnyMethod()
                // Proxied API responses may carry the idempotency replay
                // marker; browsers hide response headers unless exposed.
                .WithExposedHeaders(AzureBank.Shared.Constants.IdempotencyConstants.ReplayedHeaderName);
        });

        // Development: allow any LOOPBACK origin (any localhost port) but never
        // an external origin. Reflecting an arbitrary origin together with
        // AllowCredentials is a cross-site footgun even in development.
        options.AddPolicy("Development", policy =>
        {
            policy.SetIsOriginAllowed(origin =>
                    Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.IsLoopback)
                .AllowCredentials()
                .AllowAnyHeader()
                .AllowAnyMethod()
                .WithExposedHeaders(AzureBank.Shared.Constants.IdempotencyConstants.ReplayedHeaderName);
        });
    });

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

    // 3. Session activity tracking (updates LastActivity on every request)
    app.UseSessionActivity();

    // 4. CORS (must be before authentication)
    app.UseCors(app.Environment.IsDevelopment() ? "Development" : "AllowFrontend");

    // 5. Rate limiting
    app.UseRateLimiter();

    // 6. Auth level enforcement for sensitive routes (step-up authentication)
    app.UseAuthLevelEnforcement();

    // 7. Map controllers (BffAuthController)
    app.MapControllers();

    // 8. Map YARP reverse proxy (for /api/* routes)
    app.MapReverseProxy();

    // ═══════════════════════════════════════════════════════════════════════════
    // RUN APPLICATION
    // ═══════════════════════════════════════════════════════════════════════════

    Log.Information("AzureBank BFF Gateway started successfully");
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "BFF Gateway terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>Exposed so the WebApplicationFactory in AzureBank.Bff.Tests can host the app.</summary>
public partial class Program { }
