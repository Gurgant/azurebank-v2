using AzureBank.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AzureBank.Api.Extensions;

/// <summary>
/// Extension methods for WebApplication configuration.
///
/// Note: Database seeding has been moved to the AzureBank.Seeder tool.
/// Use: dotnet run --project tools/AzureBank.Seeder -- seed
/// </summary>
public static class WebApplicationExtensions
{
    /// <summary>
    /// Logs one line, once the host has started, with the database limits this process runs with
    /// (ADR-0058): the connect timeout, SqlClient's connect retries, the pool size and its blocking
    /// period, and EF's retry count and cap.
    /// </summary>
    /// <remarks>
    /// From <c>ApplicationStarted</c>, like the success line in <c>Program.cs</c> and for its reason:
    /// the options are validated by then, and a host that refused to start has no limits to report.
    /// </remarks>
    public static WebApplication LogDatabaseLimitsOnceStarted(this WebApplication app)
    {
        app.Lifetime.ApplicationStarted.Register(() => LogDatabaseLimits(app.Services));
        return app;
    }

    /// <summary>
    /// The line itself, read back from the context the host builds rather than from configuration:
    /// the connection string a connection is opened with (the Database section's defaults, or the
    /// string's own values where it sets them) and the execution strategy EF retries with. So the
    /// line is what a request gets, whichever of the two set it. No connection is opened.
    /// </summary>
    internal static void LogDatabaseLimits(IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("AzureBank.Api.Database");
        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>().Database;

        // A test host on the in-memory provider has no connection string and no connection.
        if (!database.IsRelational())
        {
            return;
        }

        var opened = new SqlConnectionStringBuilder(database.GetConnectionString());

        // SQL Server's non-retrying strategy is not an ExecutionStrategy: no retries.
        var strategy = database.CreateExecutionStrategy() as ExecutionStrategy;
        logger.LogInformation(
            "Database limits: connect timeout {ConnectTimeoutSeconds} s, connect retries {ConnectRetryCount}, "
            + "pool {MaxPoolSize}, pool blocking {PoolBlockingPeriod}; "
            + "EF retries {MaxRetryCount}, back-off capped at {MaxRetryDelay}",
            opened.ConnectTimeout,
            opened.ConnectRetryCount,
            opened.MaxPoolSize,
            opened.PoolBlockingPeriod,
            strategy?.MaxRetryCount ?? 0,
            strategy?.MaxRetryDelay ?? TimeSpan.Zero);
    }
}
