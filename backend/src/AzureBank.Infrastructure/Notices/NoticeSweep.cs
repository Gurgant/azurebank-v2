using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AzureBank.Infrastructure.Notices;

/// <summary>What one sweep did: rows claimed, rows delivered, rows left owed after a named failure or a lapsed lease.</summary>
public sealed record NoticeSweepSummary(int Claimed, int Delivered, int Owed);

/// <summary>
/// ONE sweep of the notice relay — claim, deliver what this runner holds, report — for whichever
/// host is RUNNING it (ADR-0048 D1, ADR-0051). Which host that may be is the caller's business:
/// this class is handed a runner name and never reads <c>Notices:Runner</c>.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS IS IN INFRASTRUCTURE AND NOT IN A HOST. ADR-0048 D4 made this argument one level down:
/// the per-row unit is <see cref="NoticeDeliveryRun"/>, which exists so two runners cannot drift
/// on what "delivered" costs. The sweep is the same argument one level up — the renew-then-read
/// order, the capacity
/// arithmetic, the per-row lease check and the outcome arms are the protocol, not the host's
/// business, and a second runner that reimplemented them would be a second protocol wearing the
/// first one's name. Until ADR-0051 this lived in <c>AzureBank.Api</c> as an internal method, which
/// was correct while the API was the only runner and became the obstacle the moment it was not.
/// </para>
/// <para>
/// WHAT IT DOES NOT OWN. Not the schedule — a <see cref="System.Threading.PeriodicTimer"/> in the
/// API, a timer trigger in the Function. Not the scope — the caller resolves a context and a
/// transport and hands them over, because the two hosts create scopes differently and neither
/// should teach the other. Not the flag: a caller that constructs this has already decided it is
/// the runner.
/// </para>
/// <para>
/// THE ADDRESS NEVER REACHES A LOG LINE. <see cref="NoticeDeliveryRun"/> returns outcomes without
/// it, and every line below names the reference, the kind, the receipt and the exception TYPE only.
/// The pickup directory IS logged: it is the operator's own configuration, and the thing the rule
/// protects is the recipient, not the path. No SecurityEvent: an owed notice is not a security
/// event (ADR-0045's precedent), and a delivered one is a receipt.
/// </para>
/// <para>
/// THE LOGGER IS THE CALLER'S, deliberately non-generic. Each runner keeps its own category — the
/// API's lines stay under <c>NoticeRelayService</c>, where ADR-0048's measured transcript quotes
/// them, and the Function's arrive under its own — while the sentences stay identical, which is the
/// half that matters when two runners' logs are read side by side.
/// </para>
/// </remarks>
public sealed class NoticeSweep
{
    private readonly AzureBankDbContext _context;
    private readonly INoticeTransport _transport;
    private readonly NoticeRelayOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    private readonly string _directory;
    private readonly string _contact;

    /// <summary>
    /// Binds one sweep to one unit of work. <paramref name="options"/> must carry a pickup directory
    /// and a contact: the runner's own configuration is validated by
    /// <see cref="NoticeRelayOptionsValidation.ValidateAsRunner"/> at whatever moment its host
    /// chose, and this constructor is the second, unconditional guard — a host that skipped that
    /// validation fails here rather than delivering a notice with no repudiation contact in it.
    /// </summary>
    public NoticeSweep(
        AzureBankDbContext context,
        INoticeTransport transport,
        NoticeRelayOptions options,
        ILogger logger,
        TimeProvider? clock = null)
    {
        _context = context;
        _transport = transport;
        _options = options;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;

        if (string.IsNullOrWhiteSpace(options.PickupDirectory))
        {
            throw new InvalidOperationException(
                "The relay has no pickup directory: Notices:PickupDirectory is not set.");
        }

        if (string.IsNullOrWhiteSpace(options.Contact))
        {
            throw new InvalidOperationException(
                "The relay has no repudiation contact: Notices:Contact is not set. It is mandatory "
                + "content of every notice (NIST SP 800-63B-4 §4.6).");
        }

        // Resolved once so every row of this sweep is delivered to the same place, even if the
        // process's working directory moves under it.
        _directory = Path.GetFullPath(options.PickupDirectory);
        _contact = options.Contact;
    }

    /// <summary>The absolute pickup directory this sweep delivers into; the caller logs it when it starts.</summary>
    public string Directory => _directory;

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// One sweep under <paramref name="runner"/>'s name: renew what it still holds, claim what it
    /// has room for, deliver what it holds, report.
    /// </summary>
    /// <returns>How many rows this sweep claimed, delivered, and left owed after a named failure.</returns>
    public async Task<NoticeSweepSummary> RunAsync(string runner, CancellationToken cancellationToken)
    {
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
        await NoticeClaim.RenewAsync(_context, runner, now, leaseEnd, cancellationToken);
        var held = await NoticeClaim.HeldBy(_context, runner, now).ToListAsync(cancellationToken);
        var capacity = Math.Max(0, _options.BatchSize - held.Count);
        var claimed = await NoticeClaim.ClaimAsync(
            _context, runner, now, leaseEnd, capacity, cancellationToken);

        var mine = claimed == 0
            ? held
            : await NoticeClaim.HeldBy(_context, runner, now).ToListAsync(cancellationToken);
        if (claimed > 0 && mine.Count == 0)
        {
            _logger.LogWarning(
                "Notice relay: claimed {Claimed} row(s) but re-read none by name; the claim and the "
                + "re-read disagree, and the rows stay leased until the lease lapses",
                claimed);
        }

        var run = new NoticeDeliveryRun(_context, _transport);
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
            var result = await run.DeliverAsync(notice, _contact, _directory, cancellationToken);

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
