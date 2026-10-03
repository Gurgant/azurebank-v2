using System.CommandLine;
using System.CommandLine.Invocation;
using AzureBank.Seeder.Pool;
using AzureBank.Shared.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AzureBank.Seeder.Commands;

/// <summary>
/// CLI command that tops the demo pool up to N fresh free copies (ADR-0062).
/// Usage: azurebank-seeder seed-pool [copies]
/// </summary>
/// <remarks>
/// <para>
/// THE DEMO'S FIRST FILL, and a refill by hand. Up to N, never by N: a pool that holds N fresh free
/// copies already is left as it is, so the command can be run again at any time. Without N it fills
/// to <c>Demo:Pool:TargetFree</c>. It deletes nothing and sweeps nothing: that is <c>recycle</c>.
/// </para>
/// <para>
/// IN DEMO MODE ONLY. With the flag off it refuses before it opens anything (exit 2): a copy is
/// three users, and a database that is not the demo's must not get them. The builder refuses too,
/// in its own code.
/// </para>
/// <para>
/// IT RUNS ON AN AZURE SQL NAME, where <c>seed</c> and <c>reset</c> are refused: the demo's database
/// is one (ADR-0060, decision 5, has the note). What makes that safe is what it writes and what it
/// never does. A copy's users have no password, so nothing it writes can be signed in to with a
/// value written down anywhere; <c>seed</c>'s four have a public one. It drops nothing, creates no
/// database and migrates nothing; <c>reset</c> drops the database. And on a database that holds
/// users and not one pool row it writes nothing at all and exits 13. Every statement it sends reads
/// or writes rows, so it needs no right to change the schema.
/// </para>
/// <para>
/// IT ENDS WITH THE RUN'S OWN CODE (<see cref="PoolExitCodes.ForSeedPool"/>: 0, 12 or 13) and one
/// line that carries every count. The code is set on the invocation, which is what the process
/// returns.
/// </para>
/// </remarks>
public static class SeedPoolCommand
{
    /// <param name="services">The tool's provider.</param>
    /// <param name="stopping">Cancelled when the process is asked to stop (SIGTERM; Program.cs).</param>
    public static Command Create(IServiceProvider services, CancellationToken stopping = default)
    {
        var command = new Command("seed-pool", "Top the demo pool up to N free copies (demo mode only)");

        var copiesArgument = new Argument<int?>(
            name: "copies",
            getDefaultValue: () => null,
            description: $"Free copies to top up to, 1 to {DemoPoolOptions.TargetFreeCeiling}; Demo:Pool:TargetFree when left out");
        copiesArgument.AddValidator(result =>
        {
            // The range Demo:Pool:TargetFree has: a number past it is a mistake, not a bigger pool.
            var copies = result.GetValueOrDefault<int?>();
            if (copies is < 1 or > DemoPoolOptions.TargetFreeCeiling)
            {
                result.ErrorMessage =
                    $"seed-pool takes a number of copies from 1 to {DemoPoolOptions.TargetFreeCeiling}, and was given {copies}.";
            }
        });

        command.AddArgument(copiesArgument);

        // Asking for the invocation's token is what makes Ctrl+C cancel the run; a SIGTERM cancels
        // the other one. The exit code goes on the invocation, which is what the process returns.
        command.SetHandler(async (InvocationContext invocation) =>
        {
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(
                invocation.GetCancellationToken(), stopping);
            invocation.ExitCode = await RunAsync(
                services,
                invocation.ParseResult.GetValueForArgument(copiesArgument),
                cancelled.Token);
        });

        return command;
    }

    /// <summary>
    /// Runs the command on <paramref name="services"/> and returns its exit code. Nothing it meets
    /// is thrown: a failure is one Error line and <see cref="ExitCodes.Failed"/>.
    /// </summary>
    /// <param name="services">The tool's provider (<c>AddSeederServices</c>).</param>
    /// <param name="copies">The fresh free copies to top up to; null for <c>Demo:Pool:TargetFree</c>.</param>
    /// <param name="cancellationToken">Cancels the run; the exit code is then 1.</param>
    public static async Task<int> RunAsync(IServiceProvider services, int? copies, CancellationToken cancellationToken)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("AzureBank.Seeder.SeedPool");

        try
        {
            if (!DemoMode.PoolCommandMayRun(services, logger, "seed-pool"))
            {
                return ExitCodes.Refused;
            }

            using var scope = services.CreateScope();
            var summary = await scope.ServiceProvider.GetRequiredService<DemoCopyBuilder>()
                .SeedPoolAsync(copies, cancellationToken);

            DemoMode.Report(logger, summary);
            return summary.ExitCode;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // The token, not the exception's type: SqlClient reports a command cancelled in flight
            // as a SqlException (MigrateCommand has the measurement).
            logger.LogWarning(
                "seed-pool was cancelled. A copy is written whole or not at all, so none is half built; "
                + "run it again to fill the pool.");
            return ExitCodes.Failed;
        }
        catch (Exception e)
        {
            logger.LogError(e, "seed-pool failed: {Reason}", MigrateCommand.FirstLine(e));
            return ExitCodes.Failed;
        }
    }
}
