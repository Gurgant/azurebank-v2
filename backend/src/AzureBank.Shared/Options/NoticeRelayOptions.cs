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
    /// A runner outside this process — the Azure Function the backlog names — is live, and the API
    /// must not also send. Reserved: nothing in this repository implements it yet.
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
/// can ship. Set <c>Notices__Runner=Api</c>, <c>Notices__PickupDirectory</c> and
/// <c>Notices__Contact</c> together; the API refuses to start with a partial set (see the
/// registration's validation). None of these is a secret, and none is added to the six.
/// </para>
/// <para>
/// ONE RUNNER AT A TIME. The flag exists so that when a second runner ships it can be named here and
/// the API's loop steps aside, rather than both sending. The lease stops two runners holding one
/// row at the same moment; it does not stop two runners existing — only this flag does.
/// </para>
/// </remarks>
public class NoticeRelayOptions
{
    public const string SectionName = "Notices";

    /// <summary>Which runner delivers. Default: nobody.</summary>
    public NoticeRunner Runner { get; set; } = NoticeRunner.None;

    /// <summary>
    /// An existing directory OUTSIDE any git repository; one <c>.eml</c> per notice. Required when
    /// <see cref="Runner"/> is <see cref="NoticeRunner.Api"/>.
    /// </summary>
    public string? PickupDirectory { get; set; }

    /// <summary>
    /// How a recipient repudiates the event: an address, a number. Mandatory content of every
    /// notice (NIST SP 800-63B-4 §4.6). Required when <see cref="Runner"/> is <see cref="NoticeRunner.Api"/>.
    /// </summary>
    public string? Contact { get; set; }

    /// <summary>How often the relay looks for owed rows. Seconds; the first look is one period after start.</summary>
    [Range(5, 3600, ErrorMessage = "Notices:PeriodSeconds must be between 5 and 3600.")]
    public int PeriodSeconds { get; set; } = 15;

    /// <summary>
    /// How long a claimed row stays this runner's before another may take it. Validated to be at
    /// least twice the period, which keeps a sweep and the next claim from overlapping in the normal
    /// case; what stops a delivery under a lapsed lease is the check before each row, and neither
    /// makes the protocol more than at-least-once (ADR-0048 D3).
    /// </summary>
    [Range(30, 3600, ErrorMessage = "Notices:LeaseSeconds must be between 30 and 3600.")]
    public int LeaseSeconds { get; set; } = 120;

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
