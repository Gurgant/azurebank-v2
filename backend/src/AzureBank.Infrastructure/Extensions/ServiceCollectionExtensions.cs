using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Options;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
        // The retry budget and the connection limits below read the Database section: 4 retries
        // with EF's back-off capped at 10 s, 10 s to connect, no SqlClient retry, a pool of 12 unless
        // set (ADR-0058; the budget was 3 and 30 s before). Bound here for every host that calls
        // this; only the API validates them at start (AddDatabaseOptions).
        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName));

        // DbContext registration
        services.AddDbContext<AzureBankDbContext>((serviceProvider, options) =>
        {
            var database = serviceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            options.UseSqlServer(
                SqlConnectionDefaults.Apply(configuration.GetConnectionString("DefaultConnection"), database),
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
                    // to succeed. ADR-0034 keeps the measured list above. The refresh-reuse path
                    // it weighed this against is gone since ADR-0057: a renewal only reads, the
                    // tripwire writes one audit row and revokes nothing, only code inside the
                    // replica can reach either, and /api/auth/revoke answers 503 so the BFF
                    // retries it. Add -2 to errorNumbersToAdd only after weighing the paths that
                    // remain.
                    if (retryOnTransientFailures)
                    {
                        sqlOptions.EnableRetryOnFailure(
                            maxRetryCount: database.MaxRetryCount,
                            maxRetryDelay: database.MaxRetryDelay,
                            errorNumbersToAdd: null);
                    }

                    sqlOptions.CommandTimeout(30);

                    // Migrations are in Infrastructure assembly
                    sqlOptions.MigrationsAssembly(typeof(AzureBankDbContext).Assembly.FullName);
                });

            // Suppress false-positive warning for query filter on required navigation
            // Transaction.Account is required, but Account has a query filter for soft-delete.
            // Transactions are immutable and never access soft-deleted Accounts via navigation.
            //
            // And a retry is logged at Warning (ADR-0058). EF raises ExecutionStrategyRetrying at
            // Information, while the hosts hold Microsoft.EntityFrameworkCore at Warning, so every
            // retry of an outage was dropped: a request could spend its whole budget retrying and
            // leave no line saying so. A retry is the first sign of an outage.
            options.ConfigureWarnings(warnings => warnings
                .Ignore(CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning)
                .Log((CoreEventId.ExecutionStrategyRetrying, LogLevel.Warning)));

            // Every interceptor the host registers. The API registers its commit gate (ADR-0058),
            // which lets a request's commit start only before the request deadline fires; the other
            // hosts register none. Read from the provider this context's options are built from,
            // which for the API's requests is the request's scope.
            options.AddInterceptors(serviceProvider.GetServices<IInterceptor>());

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

/// <summary>
/// The connection limits every host opens the database with (ADR-0058), written into a connection
/// string only where the string leaves them unset: <c>Connect Timeout</c>,
/// <c>ConnectRetryCount</c> and <c>Max Pool Size</c> from <see cref="DatabaseOptions"/>, and
/// <c>Pool Blocking Period=NeverBlock</c>.
/// </summary>
/// <remarks>
/// <para>
/// Before, the raw string went to <c>UseSqlServer</c> and SqlClient's own defaults applied: 15 s to
/// connect, one connect retry under each of EF's attempts, a pool of 100, and pool blocking on.
/// </para>
/// <para>
/// WHY NEVERBLOCK. After a failed open, a blocking pool hands the cached error to every caller for
/// 5 s, doubling up to 60 s, without trying the server. SqlClient turns that off for
/// <c>*.database.windows.net</c> (<c>Auto</c> blocks only off Azure), so on Azure an outage ends when
/// the database is back, and locally or in CI it went on failing afterwards, while EF's retries
/// during it were replays that never reached the server. NeverBlock is what Azure already gets, now
/// everywhere, so a local outage behaves like one on Azure.
/// </para>
/// <para>
/// A DEFAULT, NEVER AN OVERRIDE. A keyword counts as set under any of its names ("Connection
/// Timeout" is "Connect Timeout"), and a value the string sets wins, so a deployment can still tune
/// one limit in its own string. An empty or unparseable string is returned unchanged: the API
/// refuses it at startup with its own message (<c>AddDatabaseOptions</c>), which the parser's
/// exception must not replace here.
/// </para>
/// </remarks>
public static class SqlConnectionDefaults
{
    /// <summary>
    /// <paramref name="connectionString"/> with each limit it leaves unset filled in from
    /// <paramref name="options"/>.
    /// </summary>
    public static string? Apply(string? connectionString, DatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString;
        }

        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(connectionString);
        }
        catch (Exception e) when (e is ArgumentException or FormatException or OverflowException)
        {
            return connectionString;
        }

        // ShouldSerialize resolves a synonym to its keyword, so each check below sees a limit the
        // string sets under any of its names.
        if (!builder.ShouldSerialize("Connect Timeout"))
        {
            builder.ConnectTimeout = options.ConnectTimeoutSeconds;
        }

        if (!builder.ShouldSerialize("Connect Retry Count"))
        {
            builder.ConnectRetryCount = options.ConnectRetryCount;
        }

        // Never below the string's own Min Pool Size: SqlClient refuses a minimum above the maximum
        // when a connection is created, so a string that kept 20 warm under SqlClient's pool of 100
        // would otherwise stop opening at all.
        if (!builder.ShouldSerialize("Max Pool Size"))
        {
            builder.MaxPoolSize = Math.Max(options.MaxPoolSize, builder.MinPoolSize);
        }

        if (!builder.ShouldSerialize("Pool Blocking Period"))
        {
            builder.PoolBlockingPeriod = PoolBlockingPeriod.NeverBlock;
        }

        return builder.ConnectionString;
    }
}
