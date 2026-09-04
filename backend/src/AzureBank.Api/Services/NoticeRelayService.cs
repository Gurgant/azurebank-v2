using AzureBank.Infrastructure.Data;
using AzureBank.Infrastructure.Notices;
using AzureBank.Shared.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AzureBank.Api.Services;

/// <summary>
/// The relay: claims owed <c>SubscriberNotices</c> rows under a lease and delivers each through the
/// registered <see cref="INoticeTransport"/> (ADR-0048). The API's first correctness-bearing loop,
/// and it says so.
/// </summary>
/// <remarks>
/// <para>
/// WHAT RUNS AND WHAT DOES NOT. Always registered, so a second host that calls the same extension
/// inherits it; it reads <see cref="NoticeRelayOptions.Runner"/> once at start and, unless that is
/// <see cref="NoticeRunner.Api"/>, says so and returns — at Information for <c>None</c>, at Warning
/// for <c>Function</c>, because nothing in this repository implements that runner yet and an
/// operator who named it believes something is delivering. The flag, not the lease, is what keeps
/// two KINDS of runner from both sending; two hosts of this API with the flag set both run the
/// loop, and the lease keeps them off each other's rows.
/// </para>
/// <para>
/// THE CLAIM is <see cref="NoticeClaim"/>'s: one set-based UPDATE over a batch, then a re-read of
/// what this runner holds BY NAME. The batch bounds a claim to what one lease can deliver, so a
/// backlog is drained a sweep at a time and a second runner finds free rows beside this one. A
/// sweep that outlives its own lease all the same stops delivering: the rows it has not reached are
/// free to the next claimant, and finishing them here would produce exactly the duplicate the lease
/// exists to prevent. The lease is validated to be at least twice the period so that a sweep and the
/// next claim do not overlap in the normal case; that bounds the overlap, not the duplicates — a
/// delivery already in flight when the lease lapses still completes, and at-least-once is still the
/// honest word (see the claim's remarks).
/// </para>
/// <para>
/// THE HOUSE SHAPE for a loop (the two hygiene sweeps): <see cref="PeriodicTimer"/>, so the first
/// look is one full period after start and a fast test host is never interrupted; a catch-all per
/// tick logged at Error so one bad sweep never ends the loop; cancellation rethrown past it and
/// absorbed at the outer level as shutdown.
/// </para>
/// <para>
/// THE ADDRESS NEVER REACHES A LOG LINE. <see cref="NoticeDeliveryRun"/> returns outcomes without it,
/// and every line below names the reference, the kind, the receipt and the exception TYPE only.
/// The pickup directory IS logged: it is the operator's own configuration and the thing the rule
/// protects is the recipient, not the path. No SecurityEvent: an owed notice is not a security
/// event (ADR-0045's precedent), and a delivered one is a receipt.
/// </para>
/// <para>
/// Clock: <see cref="TimeProvider"/>, defaulting to the system one as <c>AuditService</c> does, so
/// a test can lapse a lease by advancing time rather than by rewriting the row.
/// </para>
/// </remarks>
public sealed class NoticeRelayService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly NoticeRelayOptions _options;
    private readonly ILogger<NoticeRelayService> _logger;
    private readonly TimeProvider _clock;
    private readonly string? _directory;

    /// <summary>
    /// <c>api/{host}/{pid}/{8 hex}</c>: distinguishes two runners on one machine, and is a name
    /// for the log, never a secret. Bounded to the column width by <see cref="NoticeClaim.RunnerNameFor"/>.
    /// </summary>
    public string RunnerName { get; }

    public NoticeRelayService(
        IServiceScopeFactory scopes,
        IOptions<NoticeRelayOptions> options,
        ILogger<NoticeRelayService> logger,
        TimeProvider? clock = null)
    {
        _scopes = scopes;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        RunnerName = NoticeClaim.RunnerNameFor(
            "api", Environment.MachineName, Environment.ProcessId, Guid.NewGuid());

        // Validated at start by the registration; resolved once so every sweep delivers to the same
        // place, and only when this process is the runner — nothing else may hand the sweep a path.
        _directory = _options.Runner == NoticeRunner.Api && !string.IsNullOrWhiteSpace(_options.PickupDirectory)
            ? Path.GetFullPath(_options.PickupDirectory)
            : null;
    }

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.Runner == NoticeRunner.Function)
        {
            _logger.LogWarning(
                "Notice relay: runner is {Runner}, which nothing in this repository implements yet; "
                + "this process delivers nothing and notices stay owed until the verb runs (Notices:Runner)",
                _options.Runner);
            return;
        }

        if (_options.Runner != NoticeRunner.Api || _directory is null)
        {
            _logger.LogInformation(
                "Notice relay: runner is {Runner}; this process delivers nothing (Notices:Runner)",
                _options.Runner);
            return;
        }

        _logger.LogInformation(
            "Notice relay: live as {RunnerName}, every {PeriodSeconds}s, lease {LeaseSeconds}s, batch {BatchSize}, into {Directory}",
            RunnerName, _options.PeriodSeconds, _options.LeaseSeconds, _options.BatchSize, _directory);

        using var timer = new PeriodicTimer(_options.Period, _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await SweepAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Notice relay: sweep failed; will retry next period");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown.
        }
    }

    /// <summary>
    /// One sweep: claim, deliver what this runner holds, report. Internal so a test drives it without
    /// waiting a period; it delivers only into the directory the options validated.
    /// </summary>
    /// <returns>How many rows this sweep claimed, delivered, and left owed after a named failure.</returns>
    internal async Task<NoticeSweepSummary> SweepAsync(CancellationToken cancellationToken)
    {
        if (_directory is null)
        {
            throw new InvalidOperationException("The relay has no pickup directory: Notices:Runner is not Api.");
        }

        using var scope = _scopes.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var transport = scope.ServiceProvider.GetRequiredService<INoticeTransport>();

        var now = UtcNow;
        var leaseEnd = now.Add(_options.Lease);

        /*
          THE BATCH CAPS LIVE WORK, not new claims. Rows this runner still holds from an earlier
          sweep — delivered to a transport that refused them — are retried first, and only the
          remaining capacity is claimed afresh; otherwise a runner that kept failing would claim a
          full batch on top of what it held, sweep after sweep, until it held more than one lease
          could deliver.

          AND THE HELD ROWS ARE RENEWED FIRST, to this sweep's lease end. The per-row check below
          compares against that end; a row still carrying an earlier sweep's end could lapse in the
          middle of its delivery, and another runner could claim it while this one was writing it.
        */
        await NoticeClaim.RenewAsync(context, RunnerName, now, leaseEnd, cancellationToken);
        var held = await NoticeClaim.HeldBy(context, RunnerName, now).ToListAsync(cancellationToken);
        var capacity = Math.Max(0, _options.BatchSize - held.Count);
        var claimed = await NoticeClaim.ClaimAsync(
            context, RunnerName, now, leaseEnd, capacity, cancellationToken);

        var mine = claimed == 0
            ? held
            : await NoticeClaim.HeldBy(context, RunnerName, now).ToListAsync(cancellationToken);
        if (claimed > 0 && mine.Count == 0)
        {
            _logger.LogWarning(
                "Notice relay: claimed {Claimed} row(s) but re-read none by name; the claim and the "
                + "re-read disagree, and the rows stay leased until the lease lapses",
                claimed);
        }

        var run = new NoticeDeliveryRun(context, transport);
        var delivered = 0;
        var owed = 0;
        var attempted = 0;

        foreach (var notice in mine)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (UtcNow >= leaseEnd)
            {
                // Attempted is the count that matters: a row another runner marked first was
                // attempted too, and is nobody's to owe.
                var unreached = mine.Count - attempted;
                _logger.LogWarning(
                    "Notice relay: lease lapsed mid-sweep after {Attempted} of {Held} row(s); the rest are "
                    + "free to the next claim rather than delivered under a lease this runner no longer holds",
                    attempted, mine.Count);
                owed += unreached;
                break;
            }

            attempted++;
            var result = await run.DeliverAsync(notice, _options.Contact!, _directory, cancellationToken);

            if (result.AuditRowMissing)
            {
                _logger.LogWarning(
                    "Notice relay: NO AUDIT ROW backs notice {Reference} ({Event}); delivered anyway, the absence is the finding",
                    result.Reference, notice.Event);
            }

            switch (result.Outcome)
            {
                case NoticeOutcome.Delivered:
                    delivered++;
                    _logger.LogInformation(
                        "Notice relay: delivered notice {Reference} ({Event}) as {Receipt}",
                        result.Reference, notice.Event, result.Receipt);
                    break;
                case NoticeOutcome.MarkedByAnother:
                    _logger.LogWarning(
                        "Notice relay: notice {Reference} was marked by another runner while this one wrote {Receipt}; that artefact is a duplicate",
                        result.Reference, result.Receipt);
                    break;
                case NoticeOutcome.NoAddress:
                case NoticeOutcome.UnusableAddress:
                    owed++;
                    _logger.LogWarning(
                        "Notice relay: notice {Reference} has no usable email on the account ({Outcome}); still owed",
                        result.Reference, result.Outcome);
                    break;
                case NoticeOutcome.Unrenderable:
                    owed++;
                    _logger.LogWarning(
                        "Notice relay: notice {Reference} names event {Event}, which this build cannot render; still owed",
                        result.Reference, notice.Event);
                    break;
                case NoticeOutcome.TransportFailed:
                    owed++;
                    _logger.LogWarning(
                        "Notice relay: notice {Reference} could not be delivered ({FailureType}); still owed and held, retried next sweep",
                        result.Reference, result.FailureType);
                    break;
            }
        }

        return new NoticeSweepSummary(claimed, delivered, owed);
    }
}

/// <summary>What one sweep did: rows claimed, rows delivered, rows left owed after a named failure or a lapsed lease.</summary>
public sealed record NoticeSweepSummary(int Claimed, int Delivered, int Owed);
