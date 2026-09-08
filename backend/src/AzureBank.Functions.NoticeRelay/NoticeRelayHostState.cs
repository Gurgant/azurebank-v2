using AzureBank.Infrastructure.Notices;

namespace AzureBank.Functions.NoticeRelay;

/// <summary>
/// The two things this host knows that one invocation cannot: the name it claims under, and when it
/// last ticked (ADR-0051 D5, D8).
/// </summary>
/// <remarks>
/// <para>
/// A TYPE RATHER THAN TWO FIELDS ON THE FUNCTION, because a Function class in the isolated worker is
/// constructed PER INVOCATION. The API's relay needs none of this: it is a singleton, so its name is
/// a property and its schedule is its own <c>PeriodTimer</c>. Both members here exist for the same
/// reason and are kept together to say so.
/// </para>
/// <para>
/// THE NAME MUST NOT BE PER INVOCATION. A row whose delivery failed stays owed and HELD under the
/// name that claimed it; the next sweep renews and retries it by reading <c>LeasedBy</c> back. A
/// runner that renamed itself every invocation would never recognise its own held rows: it would
/// leave each failure stranded until the lease lapsed, then claim it as a stranger — which is the
/// duplicate the lease exists to prevent, arrived at by a different route.
/// </para>
/// </remarks>
public sealed class NoticeRelayHostState
{
    private DateTime? _lastTick;

    /// <summary>The name this host claims under, for as long as the process lives.</summary>
    public string RunnerName { get; } = NoticeClaim.RunnerNameFor(
        NoticeClaim.FunctionKind, Environment.MachineName, Environment.ProcessId, Guid.NewGuid());

    /// <summary>
    /// Records this tick and returns the gap since the previous one, or <c>null</c> on the first
    /// tick of the process, when there is nothing to measure against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MEASURED, BECAUSE THE OBVIOUS SOURCE IS EMPTY. <c>TimerInfo.ScheduleStatus</c> reports
    /// <c>Last</c> and <c>Next</c> and would give the interval for free — but it arrives null in the
    /// isolated worker here. Measured 2026-09-08: six consecutive ticks across two runs, one with
    /// <c>timers.useMonitor</c> unset and one with it <c>true</c>, all with a lease short enough that
    /// a warning was due, and not one warning appeared. A guard that has never refused is a wish, so
    /// the interval is taken from this host's own clock instead of from a field the host may not
    /// fill.
    /// </para>
    /// <para>
    /// The gap between two ticks is not the SCHEDULE — a slow sweep, a paused debugger or a machine
    /// asleep all widen it, and the schedule is what the operator configured. It is the right thing
    /// to compare a lease against all the same: the lease has to cover the gap that actually
    /// happens, and a rule checked against reality catches a schedule that behaves differently from
    /// what it says.
    /// </para>
    /// </remarks>
    public TimeSpan? ObserveTick(DateTime nowUtc)
    {
        lock (this)
        {
            var previous = _lastTick;
            _lastTick = nowUtc;
            return previous is { } last && nowUtc > last ? nowUtc - last : null;
        }
    }
}
