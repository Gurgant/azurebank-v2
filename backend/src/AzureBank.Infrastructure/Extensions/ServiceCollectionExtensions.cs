using AzureBank.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AzureBank.Infrastructure.Extensions;

/// <summary>
/// Extension methods for registering Infrastructure services.
/// Called from Api/Bff/Tests/Seeder to add database access.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds Infrastructure layer services including:
    /// - DbContext with SQL Server
    /// Note: DatabaseSeeder has moved to AzureBank.Seeder tool.
    /// </summary>
    /// <param name="services">The service collection to register the DbContext into.</param>
    /// <param name="configuration">Supplies <c>ConnectionStrings:DefaultConnection</c>.</param>
    /// <param name="environment">Gates sensitive-data logging to Development.</param>
    /// <param name="retryOnTransientFailures">
    /// Leave true for anything that WRITES. Pass false only for a read-only consumer that walks a
    /// large resultset, and read why before you do: a retrying execution strategy forces EF to
    /// PRE-BUFFER every query this context compiles, because a stream cannot be replayed from the
    /// middle. <c>QueryCompilationContext.IsBuffering</c> is set from
    /// <c>ExecutionStrategy.RetriesOnFailure</c>, so <c>AsAsyncEnumerable()</c> silently stops
    /// streaming. Measured on 40,006 audit rows: 3 MB of managed heap with retry off, 34 MB with it
    /// on — and the 34 MB appears before the first row is examined, which is what a pre-buffer is.
    /// </param>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        bool retryOnTransientFailures = true)
    {
        // DbContext registration
        services.AddDbContext<AzureBankDbContext>(options =>
        {
            options.UseSqlServer(
                configuration.GetConnectionString("DefaultConnection"),
                sqlOptions =>
                {
                    // Retry transient failures: connection loss, resource limits, and DEADLOCKS
                    // (1205) — but NOT command timeouts, which this comment used to imply.
                    //
                    // Measured against the shipped EF 10.0.1 detector rather than assumed, because
                    // ADR-0034 turns on it: 1205 retries, 40613/10928/233 retry, and a bare
                    // TimeoutException retries — but SqlClient does not throw one for a command
                    // timeout. It throws SqlException with Number == -2, which is not in the list,
                    // so a query that sat blocked for the CommandTimeout below is NOT retried.
                    //
                    // Left that way deliberately. A timeout means the work was blocked, so retrying
                    // it holds a connection for another CommandTimeout without making it likelier
                    // to succeed; ADR-0034 works through where that matters (the refresh-reuse
                    // path, which an attacker can trigger on demand). Add -2 to errorNumbersToAdd
                    // only with that argument answered.
                    if (retryOnTransientFailures)
                    {
                        sqlOptions.EnableRetryOnFailure(
                            maxRetryCount: 3,
                            maxRetryDelay: TimeSpan.FromSeconds(30),
                            errorNumbersToAdd: null);
                    }

                    sqlOptions.CommandTimeout(30);

                    // Migrations are in Infrastructure assembly
                    sqlOptions.MigrationsAssembly(typeof(AzureBankDbContext).Assembly.FullName);
                });

            // Suppress false-positive warning for query filter on required navigation
            // Transaction.Account is required, but Account has a query filter for soft-delete.
            // Transactions are immutable and never access soft-deleted Accounts via navigation.
            // See: project-docs/21-ef-core-warnings-resolution.md
            options.ConfigureWarnings(warnings =>
                warnings.Ignore(CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning));

            // Development: Enable detailed logging
            if (environment.IsDevelopment())
            {
                options.EnableSensitiveDataLogging();
                options.EnableDetailedErrors();
            }
        });

        return services;
    }
}
