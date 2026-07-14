using AzureBank.Api.Extensions;
using AzureBank.Api.Middleware;
using AzureBank.Infrastructure.Extensions;
using Scalar.AspNetCore;
using Serilog;

// ═══════════════════════════════════════════════════════════════════════════
// BOOTSTRAP LOGGER (captures startup errors before config is loaded)
// ═══════════════════════════════════════════════════════════════════════════

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting AzureBank API...");

    var builder = WebApplication.CreateBuilder(args);

    // ═══════════════════════════════════════════════════════════════════════════
    // SERILOG CONFIGURATION (reads from appsettings.json)
    // ═══════════════════════════════════════════════════════════════════════════

    // preserveStaticLogger: true keeps the static bootstrap logger (console) untouched
    // instead of freezing it into the host logger. Freezing is a one-shot operation on
    // a process-wide static: with multiple in-process hosts (WebApplicationFactory in
    // integration tests) the second host build throws "The logger is already frozen".
    // The DI-registered logger below still gets the full appsettings configuration.
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services),
        preserveStaticLogger: true);

    // ═══════════════════════════════════════════════════════════════════════════
    // SERVICE REGISTRATION (using extension methods for clean organization)
    // ═══════════════════════════════════════════════════════════════════════════

    builder.Services
        .AddInfrastructure(builder.Configuration, builder.Environment)
        .AddIdentityServices()
        .AddJwtAuthentication(builder.Configuration)
        .AddValidation()
        .AddExceptionHandlers()
        .AddApplicationServices(builder.Configuration)
        .AddApiControllers()
        .AddApiDocumentation()
        .AddCorsPolicies(builder.Configuration);

    // ═══════════════════════════════════════════════════════════════════════════
    // BUILD APPLICATION
    // ═══════════════════════════════════════════════════════════════════════════

    var app = builder.Build();

    // ═══════════════════════════════════════════════════════════════════════════
    // DEVELOPMENT CONFIGURATION
    // ═══════════════════════════════════════════════════════════════════════════

    if (app.Environment.IsDevelopment())
    {
        // Scalar API Documentation (NOT Swagger)
        app.MapOpenApi();
        app.MapScalarApiReference(options =>
        {
            options.WithTitle("AzureBank API")
                   //.WithTheme(ScalarTheme.BluePlanet)
                   .WithDefaultHttpClient(ScalarTarget.CSharp, ScalarClient.HttpClient);
        });
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // MIDDLEWARE PIPELINE
    // ═══════════════════════════════════════════════════════════════════════════

    // Exception handling (must be first to catch all errors)
    app.UseExceptionHandler();

    // Status code pages (converts empty error responses to ProblemDetails)
    // Defense-in-depth for any auth/error responses not handled by OnChallenge/OnForbidden
    app.UseStatusCodePages();

    // Invalid request handling (catches BadHttpRequestException from invalid UTF-8, etc.)
    // Must be early in pipeline, before routing processes the request
    app.UseInvalidRequestHandling();

    // Correlation ID for request tracing
    app.UseCorrelationId();

    // Serilog request logging (replaces default ASP.NET Core logging)
    app.UseSerilogRequestLogging(options =>
    {
        options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000}ms";
    });

    app.UseHttpsRedirection();

    // CORS (must be before Auth)
    app.UseCors(app.Environment.IsDevelopment() ? "Development" : "AllowFrontend");

    // Authentication & Authorization (order matters!)
    app.UseAuthentication();
    app.UseAuthorization();

    // Idempotency for monetary endpoints (ADR-0009).
    // AFTER auth (401/403 must short-circuit before any record is created),
    // BEFORE the endpoints it guards.
    app.UseIdempotency();

    app.MapControllers();

    // ═══════════════════════════════════════════════════════════════════════════
    // DATABASE SEEDING (Development only)
    // ═══════════════════════════════════════════════════════════════════════════

    //await app.SeedDatabaseAsync();

    // ═══════════════════════════════════════════════════════════════════════════
    // RUN APPLICATION
    // ═══════════════════════════════════════════════════════════════════════════

    Log.Information("AzureBank API started successfully");
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

// ═══════════════════════════════════════════════════════════════════════════
// PARTIAL CLASS FOR INTEGRATION TESTS
// Enables WebApplicationFactory to access the Program class for testing
// ═══════════════════════════════════════════════════════════════════════════

public partial class Program { }
