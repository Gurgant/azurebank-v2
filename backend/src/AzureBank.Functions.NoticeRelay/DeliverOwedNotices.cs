using AzureBank.Infrastructure.Data;
using AzureBank.Infrastructure.Notices;
using AzureBank.Shared.Options;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzureBank.Functions.NoticeRelay;

/// <summary>
/// The Function that delivers owed notices: one <see cref="NoticeSweep"/> per timer tick, under this
/// host's own runner name (ADR-0051 D2).
/// </summary>
/// <remarks>
/// <para>
/// A TIMER, NOT A QUEUE, and ADR-0048 already decided it. Its "Alternatives declined" answered the
/// queue: "a queue would carry a copy of the obligation, and the row is the obligation … A lease on
/// the row keeps that property; a queue would have to be reconciled with it." A queue trigger needs
/// a producer, and the only honest producer here is the row — so a queue would be the declined
/// design wearing a trigger. What Azurite actually provides is the HOST's own storage: the timer's
/// schedule state and the host singleton lease, neither of which carries an obligation.
/// </para>
/// <para>
/// THE FLAG IS CHECKED HERE TOO, and symmetrically: this Function delivers only when
/// <c>Notices:Runner</c> is <see cref="NoticeRunner.Function"/>, exactly as the API's loop delivers
/// only when it is <see cref="NoticeRunner.Api"/>. Both step aside otherwise, so a configuration
/// naming neither delivers NOTHING rather than both delivering — a misconfiguration that owes a
/// notice is recoverable; one that sends it twice is not.
/// </para>
/// <para>
/// SCOPED DEPENDENCIES BY CONSTRUCTOR. The Functions host creates a scope per invocation and
/// resolves this class from it, so the <see cref="AzureBankDbContext"/> injected here is the
/// per-invocation one — the same property the API buys with an explicit <c>CreateScope()</c> in its
/// loop. <see cref="NoticeRelayHostState"/> is the exception and is a singleton: the name and the
/// previous tick both have to outlive the invocation.
/// </para>
/// </remarks>
public sealed class DeliverOwedNotices
{
    private readonly AzureBankDbContext _context;
    private readonly INoticeTransport _transport;
    private readonly NoticeRelayOptions _options;
    private readonly NoticeRelayHostState _host;
    private readonly ILogger<DeliverOwedNotices> _logger;
    private readonly TimeProvider _clock;

    public DeliverOwedNotices(
        AzureBankDbContext context,
        INoticeTransport transport,
        IOptions<NoticeRelayOptions> options,
        NoticeRelayHostState host,
        ILogger<DeliverOwedNotices> logger,
        TimeProvider? clock = null)
    {
        _context = context;
        _transport = transport;
        _options = options.Value;
        _host = host;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// One sweep per tick. The schedule is <c>Notices:Schedule</c>, read through the Functions host's
    /// own <c>%setting%</c> binding rather than from <see cref="NoticeRelayOptions"/>: a trigger's
    /// schedule must be a constant the host can resolve before any of this code runs, so it cannot
    /// be an <c>int</c> the options bind later.
    /// </summary>
    [Function(nameof(DeliverOwedNotices))]
    public async Task RunAsync([TimerTrigger("%Notices:Schedule%")] TimerInfo timer, CancellationToken cancellationToken)
    {
        if (_options.Runner != NoticeRunner.Function)
        {
            _logger.LogInformation(
                "Notice relay: runner is {Runner}; this process delivers nothing (Notices:Runner)",
                _options.Runner);
            return;
        }

        WarnIfTheLeaseIsShorterThanTwoTicks(_host.ObserveTick(_clock.GetUtcNow().UtcDateTime));

        var sweep = new NoticeSweep(_context, _transport, _options, _logger, _clock);
        var summary = await sweep.RunAsync(_host.RunnerName, cancellationToken);

        _logger.LogInformation(
            "Notice relay: sweep as {RunnerName} claimed {Claimed}, delivered {Delivered}, left {Owed} owed, into {Directory}",
            _host.RunnerName, summary.Claimed, summary.Delivered, summary.Owed, sweep.Directory);
    }

    /// <summary>
    /// ADR-0048 D6's lease rule, checked against the interval this host is ACTUALLY ticking at
    /// rather than against a declared number.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The API validates <c>LeaseSeconds &gt;= 2 * PeriodSeconds</c> at start and refuses to start
    /// when it fails. This host cannot: its cadence is a CRON or TimeSpan expression the trigger
    /// owns, not an integer the options carry — and <c>Notices:PeriodSeconds</c> is a value this
    /// process never reads, so validating against it would be a rule guarding a number nothing uses.
    /// What it has instead is better in one way and worse in another: better because the interval is
    /// the one that really happened, so a schedule that behaves differently from what it says is
    /// caught; worse because it is a Warning on a tick rather than a refusal at start, which means a
    /// misconfigured host runs.
    /// </para>
    /// <para>
    /// It repeats every tick on purpose: a standing fault should keep saying so, and it stops the
    /// moment it is fixed. The FIRST tick of a process says nothing — there is no previous tick to
    /// measure against, and treating that as an interval of zero would warn on every cold start,
    /// which is how a real warning becomes noise.
    /// </para>
    /// </remarks>
    private void WarnIfTheLeaseIsShorterThanTwoTicks(TimeSpan? interval)
    {
        if (interval is not { } gap || _options.Lease >= 2 * gap)
        {
            return;
        }

        _logger.LogWarning(
            "Notice relay: Notices:LeaseSeconds is {LeaseSeconds}s but this host is ticking every "
            + "{IntervalSeconds}s; a lease shorter than two ticks lets a sweep and the next claim overlap "
            + "(ADR-0048 D6). Nothing is delivered twice — the pickup transport refuses a second file — "
            + "but rows will read as held by a runner that has moved on.",
            _options.LeaseSeconds, (int)gap.TotalSeconds);
    }
}
