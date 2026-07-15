using System.Diagnostics;
using System.Globalization;
using System.Threading.RateLimiting;
using AzureBank.Bff;
using AzureBank.Bff.Middleware;
using AzureBank.Bff.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using AzureBank.Bff.Services;
using AzureBank.Bff.Services.Implementations;
using AzureBank.Bff.Services.Interfaces;
using AzureBank.Bff.Transforms;
using AzureBank.Shared.Constants;
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
