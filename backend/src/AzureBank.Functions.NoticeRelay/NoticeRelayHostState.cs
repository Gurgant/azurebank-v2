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
    private bool _saidItStandsAside;

    /// <summary>The name this host claims under, for as long as the process lives.</summary>
    public string RunnerName { get; } = NoticeClaim.RunnerNameFor(
        NoticeClaim.FunctionKind, Environment.MachineName, Environment.ProcessId, Guid.NewGuid());

    /// <summary>
    /// True the FIRST time this host declines to deliver, false ever after.
    /// </summary>
    /// <remarks>
    /// The API says "this process delivers nothing" once, because its loop reads the flag once and
    /// returns from <c>ExecuteAsync</c>. A Function is re-entered every tick, so the identical
    /// sentence would arrive on a schedule forever — a standing configuration fact logged as if it
    /// were an event. The lease warning repeats DELIBERATELY, because that is a fault which can be
    /// fixed while the host runs; this one cannot change without a restart. Kept separate from
    /// <see cref="ObserveTick"/> on purpose: that is reached only on the delivering path, and moving
    /// it above the flag check would start measuring tick intervals on a host that delivers nothing.
    /// </remarks>
    public bool AnnounceStandingAside()
    {
        lock (this)
        {
            if (_saidItStandsAside)
            {
                return false;
            }

            _saidItStandsAside = true;
            return true;
        }
    }

    /// <summary>
    /// Records this tick and returns the gap since the previous one, or <c>null</c> on the first
    /// tick of the process, when there is nothing to measure against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MEASURED HERE BECAUSE THE OBVIOUS SOURCE IS EMPTY BY THIS PROJECT'S OWN CHOICE.
    /// <c>TimerInfo.ScheduleStatus</c> reports <c>Last</c> and <c>Next</c> and would give the
    /// interval for free — but it is populated only when the ScheduleMonitor is attached, and the
    /// trigger pins <c>UseMonitor = false</c> so that a restart cannot sweep a past-due tick
    /// immediately (see <c>DeliverOwedNotices</c>). Observed null across six consecutive live ticks
    /// on 2026-09-08, every one with a lease short enough that a warning was due and none produced.
    /// ⚠️ That run could not tell the two causes apart: its 20-second cadence fires three times a
    /// minute, which clears <c>UseMonitor</c> on its own. The attribute is what settles it now. A
    /// guard that has never refused is a wish, so the interval is taken from this host's own clock
    /// rather than from a field this host has decided not to have filled.
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
