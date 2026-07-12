using System.Threading.RateLimiting;
using AzureBank.Bff.Middleware;
using AzureBank.Bff.Options;
using Microsoft.AspNetCore.RateLimiting;
using AzureBank.Bff.Services;
using AzureBank.Bff.Services.Implementations;
using AzureBank.Bff.Services.Interfaces;
using AzureBank.Bff.Transforms;
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

    // Rate limiting
    builder.Services.AddRateLimiter(options =>
    {
        options.AddFixedWindowLimiter("fixed", config =>
        {
            config.PermitLimit = 100;
            config.Window = TimeSpan.FromMinutes(1);
            config.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
            config.QueueLimit = 10;
        });
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
                .AllowAnyMethod();
        });

        // Development: More permissive
        options.AddPolicy("Development", policy =>
        {
            policy.SetIsOriginAllowed(_ => true)
                .AllowCredentials()
                .AllowAnyHeader()
                .AllowAnyMethod();
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
