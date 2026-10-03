using AzureBank.Seeder.Extensions;
using AzureBank.Seeder.Pool;
using AzureBank.Shared.Options;
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
