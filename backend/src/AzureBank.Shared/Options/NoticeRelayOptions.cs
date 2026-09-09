using System.ComponentModel.DataAnnotations;

namespace AzureBank.Shared.Options;

/// <summary>Which process, if any, delivers owed notices (ADR-0048).</summary>
public enum NoticeRunner
{
    /// <summary>Nothing runs. Notices stay owed until an operator runs the tool's <c>notify</c> verb.</summary>
    None,

    /// <summary>A hosted service inside the API process claims and delivers them.</summary>
    Api,

    /// <summary>
    /// A runner outside this process is live and the API must not also send: the Azure Function in
    /// <c>AzureBank.Functions.NoticeRelay</c>, which runs the same <c>NoticeSweep</c> on a timer
    /// trigger (ADR-0051). Until 2026-09-08 this said "Reserved: nothing in this repository
    /// implements it yet", and the API logged a Warning to match.
    /// </summary>
    Function,
}

/// <summary>
/// The <c>Notices</c> section: whether the API relays owed notices, where the last hop delivers, the
/// repudiation contact every notice must carry, and the two clocks of the claim protocol.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Runner"/> is <see cref="NoticeRunner.None"/> unless configured, on purpose: the pickup
/// directory is a spool of addresses at rest and must sit outside any git tree, so no default path
/// can ship. Set <c>Notices__Runner</c>, <c>Notices__PickupDirectory</c> and <c>Notices__Contact</c>
/// together; the host the flag NAMES refuses to start with a partial set — the API and the Function
/// alike, through the shared rules in <c>NoticeRelayOptionsValidation</c> (ADR-0051 D3). None of
/// these is a secret, and none is added to the six.
/// </para>
/// <para>
/// ONE RUNNER AT A TIME. The second runner shipped (ADR-0051): naming it here is what makes the
/// API's loop step aside and the Function deliver, rather than both sending. The lease stops two
/// runners holding one row at the same moment; it does not stop two runners existing — only this
/// flag does. It gates the two HOSTED runners and not the operator's <c>notify</c> verb, which
/// delivers whenever a person runs it and is kept off a live host's rows by the lease alone.
/// </para>
/// </remarks>
public class NoticeRelayOptions
{
    public const string SectionName = "Notices";

    /// <summary>Which runner delivers. Default: nobody.</summary>
    public NoticeRunner Runner { get; set; } = NoticeRunner.None;

    /// <summary>
    /// An existing directory OUTSIDE any git repository; one <c>.eml</c> per notice. Required of
    /// whichever host <see cref="Runner"/> names — the API or the Function (ADR-0051 D3).
    /// </summary>
    public string? PickupDirectory { get; set; }

    /// <summary>
    /// How a recipient repudiates the event: an address, a number. Mandatory content of every
    /// notice (NIST SP 800-63B-4 §4.6). Required of whichever host <see cref="Runner"/> names.
    /// </summary>
    public string? Contact { get; set; }

    /// <summary>
    /// How often the API's relay looks for owed rows. Seconds; the first look is one period after
    /// start. THE API'S ONLY: the Function's cadence is <c>Notices:Schedule</c>, a trigger
    /// expression the Functions host binds before any of this project's code runs, and that host
    /// never reads this value (ADR-0051 D3, D5).
    /// </summary>
    [Range(5, 3600, ErrorMessage = "Notices:PeriodSeconds must be between 5 and 3600.")]
    public int PeriodSeconds { get; set; } = 15;

    /// <summary>
    /// How long a claimed row stays this runner's before another may take it.
    /// <para>
    /// TWO HOSTS CHECK "at least twice the period" AT TWO DIFFERENT MOMENTS, and the difference is
    /// worth knowing before trusting either. The API validates
    /// <see cref="LeaseSeconds"/> against <see cref="PeriodSeconds"/> AT STARTUP
    /// (<c>ValidateThePeriodItSleepsFor</c> with <c>ValidateOnStart</c>) and refuses to start when it
    /// fails. The Function cannot: its cadence is <c>Notices:Schedule</c>, bound by the Functions
    /// host before any of this code runs, and it never reads <see cref="PeriodSeconds"/> at all — so
    /// it compares the lease against the interval it OBSERVES between its own ticks and warns per
    /// tick (ADR-0051 D5). Startup refusal against a declared number; a running warning against a
    /// real one.
    /// </para>
    /// <para>
    /// Either way the rule only keeps a sweep and the next claim from overlapping in the normal
    /// case; what stops a delivery under a lapsed lease is the check before each row, and neither
    /// makes the protocol more than at-least-once (ADR-0048 D3).
    /// </para>
    /// </summary>
    [Range(30, 3600, ErrorMessage = "Notices:LeaseSeconds must be between 30 and 3600.")]
    public int LeaseSeconds { get; set; } = 120;

    /// <summary>
    /// The Function's cadence: a CRON or TimeSpan expression the Functions host binds into the timer
    /// trigger as <c>%Notices:Schedule%</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE FUNCTION'S ONLY, and the mirror image of <see cref="PeriodSeconds"/>: the API sleeps for
    /// a number and never reads this; the Function ticks on this and never reads that. It is bound
    /// here — where nothing in this process consumes it — SO THAT IT CAN BE VALIDATED. The Functions
    /// runtime resolves the trigger's <c>%setting%</c> during indexing, and a value it cannot
    /// resolve disables the function while leaving the host running: a process that is up, exit code
    /// zero, and delivering nothing. A validator in the worker's own composition root refuses first,
    /// with a message naming the key (ADR-0051 D3).
    /// </para>
    /// </remarks>
    public string? Schedule { get; set; }

    /// <summary>
    /// How many free owed rows one sweep claims, oldest first. Bounds a claim to what one lease can
    /// deliver: an unbounded claim over a backlog would hold the whole table while it ran out of
    /// lease, and leave the rest to nobody until the lease lapsed.
    /// </summary>
    [Range(1, 10000, ErrorMessage = "Notices:BatchSize must be between 1 and 10000.")]
    public int BatchSize { get; set; } = 100;

    public TimeSpan Period => TimeSpan.FromSeconds(PeriodSeconds);

    public TimeSpan Lease => TimeSpan.FromSeconds(LeaseSeconds);
}
