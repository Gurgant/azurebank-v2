using System.CommandLine;
using System.CommandLine.Invocation;
using AzureBank.Seeder.Pool;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AzureBank.Seeder.Commands;

/// <summary>
/// CLI command that tops the demo pool up, deletes the copies whose time is over, and sweeps what
/// has expired: the job a deployment runs on a schedule (ADR-0062).
/// Usage: azurebank-seeder recycle
/// </summary>
/// <remarks>
/// <para>
/// SAFE AT ANY INTERVAL, and beside the API: everything it does is decided from the rows as they
/// are, a copy in use is never deleted, and a run that was stopped is completed by the next one.
/// </para>
/// <para>
/// IN DEMO MODE ONLY. With the flag off it refuses before it opens anything (exit 2): it deletes
/// users. The recycler refuses too, in its own code.
/// </para>
/// <para>
/// IT RUNS ON AN AZURE SQL NAME, where <c>seed</c> and <c>reset</c> are refused: the demo's database
/// is one (ADR-0060, decision 5, has the note). What makes that safe: every delete is keyed on a
/// copy, so a user that belongs to no copy matches none of them; on a database that holds users and
/// not one pool row it writes nothing at all, the sweeps included, and exits 13; it drops nothing,
/// creates no database and migrates nothing; and the copies its top-up builds have no password, as
/// <c>seed-pool</c>'s. Every statement it sends reads or writes rows, so it needs no right to change
/// the schema.
/// </para>
/// <para>
/// IT ENDS WITH THE RUN'S OWN CODE (<see cref="PoolExitCodes.From"/>: 0, or a signal from 10 to 15)
/// and one line that carries every count. The code is set on the invocation, which is what the
/// process returns: a job runner acts on that number and on nothing else.
/// </para>
/// </remarks>
public static class RecycleCommand
{
    /// <param name="services">The tool's provider.</param>
    /// <param name="stopping">Cancelled when the process is asked to stop (SIGTERM; Program.cs).</param>
    public static Command Create(IServiceProvider services, CancellationToken stopping = default)
    {
        var command = new Command(
            "recycle", "Top the demo pool up, delete the copies whose time is over, sweep what expired (demo mode only)");

        // Asking for the invocation's token is what makes Ctrl+C cancel the run; a SIGTERM cancels
        // the other one. The exit code goes on the invocation, which is what the process returns.
        command.SetHandler(async (InvocationContext invocation) =>
        {
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(
                invocation.GetCancellationToken(), stopping);
            invocation.ExitCode = await RunAsync(services, cancelled.Token);
        });

        return command;
    }

    /// <summary>
    /// Runs the command on <paramref name="services"/> and returns its exit code. Nothing it meets
    /// is thrown: a failure is one Error line and <see cref="ExitCodes.Failed"/>.
    /// </summary>
    /// <param name="services">The tool's provider (<c>AddSeederServices</c>).</param>
    /// <param name="cancellationToken">Cancels the run; the exit code is then 1.</param>
    public static async Task<int> RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("AzureBank.Seeder.Recycle");

        try
        {
            if (!DemoMode.PoolCommandMayRun(services, logger, "recycle"))
            {
                return ExitCodes.Refused;
            }

            using var scope = services.CreateScope();
            var summary = await scope.ServiceProvider.GetRequiredService<DemoCopyRecycler>().RunAsync(cancellationToken);

            DemoMode.Report(logger, summary);
            return summary.ExitCode;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // The token, not the exception's type: SqlClient reports a command cancelled in flight
            // as a SqlException (MigrateCommand has the measurement).
            logger.LogWarning(
                "recycle was cancelled. Each copy is built or deleted whole or not at all, and the next run "
                + "starts again from the rows as they are; run it again.");
            return ExitCodes.Failed;
        }
        catch (Exception e)
        {
            // A failure that is no copy's: a count, the roles the top-up creates first, a sweep. A
            // copy's own failure is counted in the summary, and does not come here.
            logger.LogError(e, "recycle failed: {Reason}", MigrateCommand.FirstLine(e));
            return ExitCodes.Failed;
        }
    }
}
