using AzureBank.Shared.Options;
using Microsoft.Extensions.Options;

namespace AzureBank.Infrastructure.Notices;

/// <summary>
/// The three rules EVERY host must satisfy before it may call itself the notice runner (ADR-0048
/// D6), written once for every host that can be one (ADR-0051 D3) — plus a fourth,
/// <see cref="ValidateThePeriodItSleepsFor"/>, that belongs only to a host whose cadence is
/// <c>Notices:PeriodSeconds</c>.
/// </summary>
/// <remarks>
/// <para>
/// WHY SHARED RATHER THAN MIRRORED, with a measurement behind it. Until ADR-0051 these four rules
/// lived in <c>AzureBank.Api</c>, each guarded by <c>o.Runner != NoticeRunner.Api ||</c> — correct
/// while the API was the only host that could deliver, and silent for every other value: with
/// <c>Notices:Runner=Function</c> the API accepted a missing contact, a pickup directory that did
/// not exist, and one INSIDE A GIT REPOSITORY, because none of the rules applied to it. A second
/// runner needs all four, and the repository has already paid for the alternative:
/// <c>AddVerifierServices</c> mirrors the API's audit-key validation by hand and records, in its own
/// comment, that the mirror failed — <c>Audit:AnchorKey</c> was added to the API and not to the
/// tool, "so for one release this tool started, read the chain, and would have refused to write an
/// anchor at the point of use". Three copies of four rules is three chances at that.
/// </para>
/// <para>
/// THE RULES ARE RELATIVE TO THE ASKING HOST, which is why the runner is a parameter rather than a
/// constant. A host validates the configuration it would act on: the API asks
/// <see cref="NoticeRunner.Api"/>, the Function asks <see cref="NoticeRunner.Function"/>, and each
/// refuses to start only when the flag names IT and the section cannot support it. A host the flag
/// does not name starts regardless — it is going to step aside anyway, and refusing to start over
/// a directory it will never write to would take the API down for the Function's misconfiguration.
/// </para>
/// <para>
/// TWO OF THE THREE TOUCH THE FILE SYSTEM (<c>Directory.Exists</c>, the git walk). That is
/// acceptable because the only consumer is <see cref="IOptions{TOptions}"/>, which is built once and
/// cached; a future <see cref="IOptionsSnapshot{TOptions}"/> or <see cref="IOptionsMonitor{TOptions}"/>
/// consumer would pay the walk on every resolve. The <c>[Range]</c> annotations are NOT here: they
/// apply whatever the runner, because a period or a lease out of range is a misconfiguration even
/// when nothing runs, so a caller adds <c>ValidateDataAnnotations()</c> of its own.
/// </para>
/// </remarks>
public static class NoticeRelayOptionsValidation
{
    /// <summary>
    /// Adds the three universal rules, each conditional on <paramref name="thisProcess"/> being the runner the
    /// flag names. The caller still chooses <c>ValidateDataAnnotations()</c> and
    /// <c>ValidateOnStart()</c>: whether a bad configuration stops the host or surfaces at first
    /// resolve is the host's decision, not this one's.
    /// </summary>
    public static OptionsBuilder<NoticeRelayOptions> ValidateAsRunner(
        this OptionsBuilder<NoticeRelayOptions> builder, NoticeRunner thisProcess) =>
        builder
            .Validate(
                o => o.Runner != thisProcess || !string.IsNullOrWhiteSpace(o.Contact),
                $"Notices:Contact must be set when Notices:Runner is {thisProcess} — it is mandatory "
                + "content of every notice (NIST SP 800-63B-4 §4.6): an address or a number a recipient "
                + "uses to say \"this was not me\".")
            .Validate(
                o => o.Runner != thisProcess
                     || (!string.IsNullOrWhiteSpace(o.PickupDirectory)
                         && !o.PickupDirectory.Contains('\0')
                         && Directory.Exists(o.PickupDirectory)),
                $"Notices:PickupDirectory must name an EXISTING directory when Notices:Runner is {thisProcess}. "
                + "The relay does not create it: a spool of addresses should land only where somebody "
                + "meant it to.")
            .Validate(
                o => o.Runner != thisProcess
                     || string.IsNullOrWhiteSpace(o.PickupDirectory)
                     || !Directory.Exists(o.PickupDirectory)
                     || !PickupDirectoryGuard.InsideAGitRepository(Path.GetFullPath(o.PickupDirectory)),
                "Notices:PickupDirectory is inside a git repository. A pickup directory is a spool of "
                + "addresses at rest, and one under a repository is one commit away from being "
                + "published. Name a directory outside the tree.");

    /// <summary>
    /// The lease-against-period rule (ADR-0048 D6), for a host whose cadence IS
    /// <c>Notices:PeriodSeconds</c> — which is the API and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SEPARATE FROM <see cref="ValidateAsRunner"/> BECAUSE IT IS NOT UNIVERSAL, and the first draft
    /// of ADR-0051 got this wrong: it gave the Function all four rules, and the fourth would have
    /// made that host refuse to start over a number it never reads. Worse than pointless — a host
    /// that validated `LeaseSeconds >= 2 * PeriodSeconds` and then ticked on
    /// <c>Notices:Schedule</c> would be offering an assurance about a cadence it does not have.
    /// </para>
    /// <para>
    /// The Function's equivalent is a RUNTIME check against the interval it observes between its own
    /// ticks (ADR-0051 D5), because that is the only cadence it can know: a trigger's schedule is
    /// resolved by the Functions host before any of this code runs. A host that adopts this rule is
    /// promising that <c>PeriodSeconds</c> is what it sleeps for.
    /// </para>
    /// </remarks>
    public static OptionsBuilder<NoticeRelayOptions> ValidateThePeriodItSleepsFor(
        this OptionsBuilder<NoticeRelayOptions> builder, NoticeRunner thisProcess) =>
        builder.Validate(
            o => o.Runner != thisProcess || o.LeaseSeconds >= 2 * o.PeriodSeconds,
            "Notices:LeaseSeconds must exceed Notices:PeriodSeconds — at least twice it — so a sweep and "
            + "the next claim do not overlap in the normal case.");
}
