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
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        // DbContext registration
        services.AddDbContext<AzureBankDbContext>(options =>
        {
            options.UseSqlServer(
                configuration.GetConnectionString("DefaultConnection"),
                sqlOptions =>
                {
                    // Retry on transient failures (network issues, deadlocks)
                    sqlOptions.EnableRetryOnFailure(
                        maxRetryCount: 3,
                        maxRetryDelay: TimeSpan.FromSeconds(30),
                        errorNumbersToAdd: null);

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
