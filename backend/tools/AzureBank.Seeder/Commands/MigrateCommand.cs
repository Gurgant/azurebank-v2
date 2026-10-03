using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using AzureBank.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AzureBank.Seeder.Commands;

/// <summary>
/// CLI command that brings the database to the latest migration and changes nothing else.
/// Usage: azurebank-seeder migrate [--wait-seconds N]
/// </summary>
/// <remarks>
/// <para>
/// THE ONE-SHOT A DEPLOYMENT RUNS BEFORE THE APP MOVES (ADR-0060). It opens the database through
/// <c>AddInfrastructure</c>, so it has the connection limits and the retry budget every host has
/// (ADR-0058), and it logs them before it opens anything. It needs the connection string and no
/// other secret: it never reads the PIN pepper and never resolves a seeder.
/// </para>
/// <para>
/// IT WAITS FOR THE DATABASE FIRST (<see cref="DatabaseGate"/>), then checks the history table
/// BEFORE and AFTER <c>MigrateAsync</c>. A database that holds a migration this build does not
/// know was migrated by a newer build, and EF calls that "already up to date" and exits 0
/// (measured 2026-10-01 with one row added: 17 applied, 16 known). A deployment of an older
/// build would then move the app onto a schema it does not know, so it is refused, before
/// anything is applied: a database with an unknown migration may also lack a known one, and
/// applying onto a schema that diverged is the worse outcome.
/// </para>
/// <para>
/// IT CLAIMS NO "APPLIED N". The pending list is read before EF takes its lock, so two runs at
/// once both see the same list and only one applies it. EF's own <c>Applying migration '…'.</c>
/// lines are the record of what this run applied, and the tool's settings let them through.
/// </para>
/// <para>
/// <c>MigrateAsync</c> is always called, even with nothing pending: it is also the check that the
/// model has no change without a migration, which EF runs before it touches the database.
/// </para>
/// </remarks>
public static class MigrateCommand
{
    /// <summary>The longest wait the option accepts: ten minutes.</summary>
    public const int LongestWaitSeconds = 600;

    private const int DefaultWaitSeconds = 60;

    /// <param name="services">The tool's provider.</param>
    /// <param name="stopping">Cancelled when the process is asked to stop (SIGTERM; Program.cs).</param>
    public static Command Create(IServiceProvider services, CancellationToken stopping = default)
    {
        var command = new Command("migrate", "Apply every pending migration; change nothing when there is none");

        var waitOption = new Option<int>(
            name: "--wait-seconds",
            getDefaultValue: () => DefaultWaitSeconds,
            description: $"How long to wait for the database to accept a connection (0 to {LongestWaitSeconds})");
        waitOption.AddValidator(result =>
        {
            var seconds = result.GetValueOrDefault<int>();
            if (seconds is < 0 or > LongestWaitSeconds)
            {
                result.ErrorMessage = $"--wait-seconds must be between 0 and {LongestWaitSeconds}.";
            }
        });

        command.AddOption(waitOption);

        // The handler asks for the invocation's token, which is what makes Ctrl+C cancel the run,
        // and links it to the token a SIGTERM cancels. It sets the exit code on the context, which
        // is what the process returns.
        command.SetHandler(async (InvocationContext invocation) =>
        {
            var wait = TimeSpan.FromSeconds(invocation.ParseResult.GetValueForOption(waitOption));
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(
                invocation.GetCancellationToken(), stopping);
            invocation.ExitCode = await RunAsync(services, wait, cancelled.Token);
        });

        return command;
    }

    /// <summary>
    /// Runs the command on <paramref name="services"/> and returns its exit code. Nothing it meets
    /// is thrown: a failure is one Error line and <see cref="ExitCodes.Failed"/>.
    /// </summary>
    /// <param name="services">The tool's provider (<c>AddSeederServices</c>).</param>
    /// <param name="wait">How long to wait for the database to accept a connection.</param>
    /// <param name="cancellationToken">Cancels the run; the exit code is then 1.</param>
    /// <param name="gate">
    /// The wait itself. Null is the real one (<see cref="DatabaseGate"/>, on the string this run
    /// opens with); a test hands in a verdict no local server can be made to give.
    /// </param>
    public static async Task<int> RunAsync(
        IServiceProvider services,
        TimeSpan wait,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<GateResult>>? gate = null)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("AzureBank.Seeder.Migrate");
        var watch = Stopwatch.StartNew();

        try
        {
            var target = ConnectionTarget.ReadOrRefuse(services, logger, "migrate", refusedOnAzureSql: null);
            if (target is null)
            {
                return ExitCodes.Refused;
            }

            using var scope = services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
            var database = context.Database;

            // Read back from the context this run opens with, as the API's start line is: the
            // string a connection is opened with and the strategy EF retries with. Nothing is
            // opened by reading them.
            var effective = database.GetConnectionString()!;
            var opened = new SqlConnectionStringBuilder(effective);

            // SqlClient reads a connect timeout of 0 as "no limit". The wait is read between two
            // opens, so one open that never ends is a run that never ends, and EF's opens after it
            // are no different. Measured 2026-10-01 against a listener that accepts and never
            // answers, --wait-seconds 3: exit 1 after 11.2 s with the default of 10; with 0, from
            // the string or from Database:ConnectTimeoutSeconds, still running at 25 s with the
            // limits line as its only output. Only the API validates that setting at start.
            if (opened.ConnectTimeout == 0)
            {
                logger.LogError(
                    "migrate refused: the connect timeout is 0, which means no limit, so nothing would bound "
                    + "the wait for the database. Set Connect Timeout in {Variable}, or "
                    + "Database__ConnectTimeoutSeconds, to 1 or more. Nothing was opened.",
                    ConnectionTarget.Variable);
                return ExitCodes.Refused;
            }

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

            gate ??= token => DatabaseGate.WaitAsync(
                DatabaseGate.Open(effective),
                DatabaseGate.AskMaster(effective),
                target.IsAzureSql,
                wait,
                TimeProvider.System,
                logger,
                token);

            var waited = await gate(cancellationToken);
            if (waited.Verdict != GateVerdict.Proceed)
            {
                LogVerdict(logger, waited, wait);
                return ExitCodes.Failed;
            }

            var known = database.GetMigrations().ToList();
            var applied = (await database.GetAppliedMigrationsAsync(cancellationToken)).ToList();
            var unknown = applied.Except(known, StringComparer.Ordinal).ToList();
            if (unknown.Count > 0)
            {
                logger.LogError(
                    "The database holds {Count} migration(s) this build does not know: {Migrations}. "
                    + "A newer build migrated it. Nothing was changed.",
                    unknown.Count,
                    string.Join(", ", unknown));
                return ExitCodes.Failed;
            }

            var pending = known.Except(applied, StringComparer.Ordinal).ToList();
            if (pending.Count > 0)
            {
                logger.LogInformation(
                    "Pending migrations: {Count}, from {First} to {Last}", pending.Count, pending[0], pending[^1]);
            }
            else
            {
                logger.LogInformation("Pending migrations: 0");
            }

            await database.MigrateAsync(cancellationToken);

            var after = (await database.GetAppliedMigrationsAsync(cancellationToken)).ToList();
            var missing = known.Except(after, StringComparer.Ordinal).ToList();
            var extra = after.Except(known, StringComparer.Ordinal).ToList();
            if (missing.Count > 0 || extra.Count > 0)
            {
                logger.LogError(
                    "After migrating, the history table does not match this build: "
                    + "{MissingCount} of its migrations are not applied ({Missing}), "
                    + "and {ExtraCount} applied ones are not its own ({Extra}).",
                    missing.Count,
                    string.Join(", ", missing),
                    extra.Count,
                    string.Join(", ", extra));
                return ExitCodes.Failed;
            }

            logger.LogInformation(
                "The database is at {Last}: {Applied} of {Known} migrations. migrate took {Seconds} s",
                known.Count > 0 ? known[^1] : "no migration",
                after.Count,
                known.Count,
                watch.Elapsed.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
            return ExitCodes.Done;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // The filter is the token, not the exception's type: SqlClient reports a command that
            // was cancelled in flight as a SqlException ("A severe error occurred on the current
            // command", measured 2026-10-01 on a run stopped while it waited for EF's lock).
            logger.LogWarning(
                "migrate was cancelled before it finished. Run it again: only migrations the history "
                + "table does not list are applied.");
            return ExitCodes.Failed;
        }
        catch (Exception e)
        {
            logger.LogError(e, "migrate failed: {Reason}", FirstLine(e));
            return ExitCodes.Failed;
        }
    }

    private static void LogVerdict(ILogger logger, GateResult waited, TimeSpan wait)
    {
        var lastAnswer = waited.LastAnswer ?? "none";
        switch (waited.Verdict)
        {
            case GateVerdict.TimedOut:
                logger.LogError(
                    "migrate failed: the database did not accept a connection within {Seconds} s; "
                    + "the last answer was {LastAnswer}.",
                    (int)wait.TotalSeconds,
                    lastAnswer);
                break;

            case GateVerdict.LoginRefused:
                logger.LogError(
                    "migrate failed: the login was refused three times; check what {Variable} signs in "
                    + "with: a user and its password, or a managed identity, which needs a user of its own "
                    + "in this database. The last answer was {LastAnswer}.",
                    ConnectionTarget.Variable,
                    lastAnswer);
                break;

            case GateVerdict.CannotOpenDatabase:
                logger.LogError(
                    "migrate failed: the server answers, and this login cannot open the database. "
                    + "The last answer was {LastAnswer}.",
                    lastAnswer);
                break;

            case GateVerdict.NoSuchDatabase:
                logger.LogError(
                    "migrate failed: the database does not exist on this Azure SQL server. Create it with "
                    + "the infrastructure: migrate does not create databases on Azure SQL. "
                    + "The last answer was {LastAnswer}.",
                    lastAnswer);
                break;
        }
    }

    /// <summary>The first line of an exception's message: the rest is in the stack the logger prints.</summary>
    internal static string FirstLine(Exception e)
    {
        var end = e.Message.IndexOfAny(['\r', '\n']);
        return end >= 0 ? e.Message[..end] : e.Message;
    }
}
