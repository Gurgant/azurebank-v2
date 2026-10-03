using AzureBank.Infrastructure.Data;
using AzureBank.Seeder.Extensions;
using AzureBank.Seeder.Pool;
using AzureBank.Shared.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzureBank.Seeder.Commands;

/// <summary>
/// What the demo flag means to each command, and the line a pool run ends with (ADR-0062).
/// </summary>
/// <remarks>
/// <para>
/// ON, the pool's two commands run and <c>seed</c> and <c>reset</c> are refused. OFF, the default,
/// the other way round. <c>migrate</c> does not read it.
/// </para>
/// <para>
/// THE FLAG IS READ AFTER THE SETTINGS PASSED THEIR VALIDATOR (<c>PinPepperIsUsable</c> runs it): a
/// demo setting out of range is then a refusal with a sentence, and never an exception thrown by
/// the read.
/// </para>
/// </remarks>
internal static class DemoMode
{
    /// <summary>
    /// What <c>seed-pool</c> and <c>recycle</c> ask before they open anything: a connection string
    /// they can read, which may name an Azure SQL server; usable settings; and the demo on. False
    /// once the refusal has been logged, which is exit <see cref="ExitCodes.Refused"/>.
    /// </summary>
    public static bool PoolCommandMayRun(IServiceProvider services, ILogger logger, string command)
    {
        if (ConnectionTarget.ReadOrRefuse(services, logger, command, refusedOnAzureSql: null) is null
            || !services.PinPepperIsUsable(logger, command))
        {
            return false;
        }

        if (IsOn(services))
        {
            return true;
        }

        logger.LogError(
            "{Command} refused: Demo:Enabled is not true. The pool's commands build and delete demo copies, and run "
            + "only where the demo is on (Demo__Enabled=true). Nothing was opened.",
            command);
        return false;
    }

    /// <summary>
    /// What <c>seed</c> and <c>reset</c> ask after their own checks and before they open anything:
    /// the demo off. False once the refusal has been logged, which is exit
    /// <see cref="ExitCodes.Refused"/>.
    /// </summary>
    /// <param name="services">The tool's provider; the settings have passed their validator.</param>
    /// <param name="logger">Where the refusal goes.</param>
    /// <param name="command">The command's name, as typed.</param>
    /// <param name="reason">Why this command must not run in demo mode, as a sentence.</param>
    public static bool IsOffFor(IServiceProvider services, ILogger logger, string command, string reason)
    {
        if (!IsOn(services))
        {
            return true;
        }

        logger.LogError("{Command} refused: Demo:Enabled is true. {Reason} Nothing was opened.", command, reason);
        return false;
    }

    /// <summary>
    /// Whether the database holds a row of the demo pool, the record of a deleted copy included:
    /// then it is the demo's, whatever the flag says. True once the refusal has been logged, which
    /// is exit <see cref="ExitCodes.Refused"/>.
    /// </summary>
    /// <remarks>
    /// One count, the first statement the command sends, before it writes anything. A record counts:
    /// a demo database whose every copy was deleted still holds the records, and is still the demo's.
    /// </remarks>
    /// <param name="scoped">The command's scope.</param>
    /// <param name="logger">Where the refusal goes.</param>
    /// <param name="command">The command's name, as typed.</param>
    /// <param name="reason">Why this command must not run on the demo's database, as a sentence.</param>
    /// <param name="cancellationToken">The run's token.</param>
    public static async Task<bool> HoldsThePoolAsync(
        IServiceProvider scoped, ILogger logger, string command, string reason, CancellationToken cancellationToken)
    {
        var rows = await scoped.GetRequiredService<AzureBankDbContext>().DemoCopies.CountAsync(cancellationToken);
        if (rows == 0)
        {
            return false;
        }

        logger.LogError(
            "{Command} refused: the database holds the demo pool's rows ({Rows}), so it is the demo's. {Reason} "
            + "Nothing was written.",
            command,
            rows,
            reason);
        return true;
    }

    /// <summary>The one line a pool run ends with: at Information when its code is 0, at Warning when it is a signal.</summary>
    /// <remarks>
    /// The line, not a sentence about it: its shape is the summary's own (<see cref="PoolRunSummary.ToLine"/>),
    /// so that it can be searched for and compared from one run to the next.
    /// </remarks>
    public static void Report(ILogger logger, PoolRunSummary summary) =>
        logger.Log(
            summary.ExitCode == PoolExitCodes.PoolOk ? LogLevel.Information : LogLevel.Warning,
            "{PoolLine}",
            summary.ToLine());

    private static bool IsOn(IServiceProvider services) =>
        services.GetRequiredService<IOptions<DemoOptions>>().Value.Enabled;
}
