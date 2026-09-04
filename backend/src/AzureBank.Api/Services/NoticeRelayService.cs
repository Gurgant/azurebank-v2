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
/// <see cref="NoticeRunner.Api"/>, logs that no runner is live in this process and returns. That
/// flag — not the lease — is what keeps two runners from both sending.
/// </para>
/// <para>
/// THE CLAIM IS ONE SET-BASED UPDATE: every owed row whose lease is null or expired is stamped with
/// this runner's name and a lease end, in one statement, so two runners cannot hold one row at the
/// same moment — the database serialises the two UPDATEs and the second finds nothing free. What
/// the claim cannot do is make delivery once: a runner that delivers and dies before marking is
/// succeeded when its lease lapses, and that row goes out again. At-least-once, said here and in
/// the ADR rather than implied away by the column.
/// </para>
/// <para>
/// THE HOUSE SHAPE for a loop (the two hygiene sweeps): <see cref="PeriodicTimer"/>, so the first
/// look is one full period after start and a fast test host is never interrupted; a catch-all per
/// tick logged at Error so one bad sweep never ends the loop; cancellation rethrown past it and
/// absorbed at the outer level as shutdown.
/// </para>
/// <para>
/// THE ADDRESS NEVER REACHES A LOG LINE. <see cref="NoticeDeliveryRun"/> returns outcomes without it,
/// and every line below names the reference, the kind and the receipt only. No SecurityEvent: an
/// owed notice is not a security event (ADR-0045's precedent), and a delivered one is a receipt.
/// </para>
/// </remarks>
public sealed class NoticeRelayService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly NoticeRelayOptions _options;
    private readonly ILogger<NoticeRelayService> _logger;

    /// <summary>
    /// Host, process and a short random suffix: distinguishes two runners on one machine, and is a
    /// name for the log, never a secret. Bounded to the column width.
    /// </summary>
    public string RunnerName { get; }

    public NoticeRelayService(
        IServiceScopeFactory scopes,
        IOptions<NoticeRelayOptions> options,
        ILogger<NoticeRelayService> logger)
    {
        _scopes = scopes;
        _options = options.Value;
        _logger = logger;

        var name = $"{Environment.MachineName}/{Environment.ProcessId}/{Guid.NewGuid():N}"[..^24];
        RunnerName = name.Length > 64 ? name[..64] : name;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.Runner != NoticeRunner.Api)
        {
            _logger.LogInformation(
                "Notice relay: runner is {Runner}; this process delivers nothing (Notices:Runner)",
                _options.Runner);
            return;
        }

        // Validated at start (registration); resolved once so every sweep delivers to the same place.
        var directory = Path.GetFullPath(_options.PickupDirectory!);

        _logger.LogInformation(
            "Notice relay: live as {RunnerName}, every {PeriodSeconds}s, lease {LeaseSeconds}s, into {Directory}",
            RunnerName, _options.PeriodSeconds, _options.LeaseSeconds, directory);

        using var timer = new PeriodicTimer(_options.Period);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await SweepAsync(directory, stoppingToken);
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
    /// One sweep: claim, deliver, report. Exposed so a test drives it without waiting a period.
    /// </summary>
    /// <returns>How many rows this sweep claimed, delivered, and left owed.</returns>
    public async Task<NoticeSweepSummary> SweepAsync(string directory, CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var transport = scope.ServiceProvider.GetRequiredService<INoticeTransport>();

        var now = DateTime.UtcNow;
        var leaseEnd = now.Add(_options.Lease);
        var claimed = await ClaimAsync(context, now, leaseEnd, cancellationToken);
        if (claimed == 0)
        {
            return new NoticeSweepSummary(0, 0, 0);
        }

        var mine = await context.SubscriberNotices
            .Where(n => n.DeliveredAt == null && n.LeasedBy == RunnerName && n.LeasedUntil == leaseEnd)
            .OrderBy(n => n.OccurredAt)
            .ToListAsync(cancellationToken);

        var run = new NoticeDeliveryRun(context, transport);
        var delivered = 0;
        var owed = 0;

        foreach (var notice in mine)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await run.DeliverAsync(notice, _options.Contact!, directory, now, cancellationToken);

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
                        "Notice relay: notice {Reference} could not be delivered ({FailureType}); still owed, retried after the lease",
                        result.Reference, result.FailureType);
                    break;
            }
        }

        return new NoticeSweepSummary(claimed, delivered, owed);
    }

    /// <summary>
    /// Stamps every free owed row with this runner's lease. Set-based on a relational store; the
    /// load-and-save fallback exists for the InMemory test hosts, which cannot translate
    /// ExecuteUpdate and register this service like any other.
    /// </summary>
    private async Task<int> ClaimAsync(
        AzureBankDbContext context, DateTime now, DateTime leaseEnd, CancellationToken cancellationToken)
    {
        var free = context.SubscriberNotices
            .Where(n => n.DeliveredAt == null && (n.LeasedUntil == null || n.LeasedUntil <= now));

        if (context.Database.IsRelational())
        {
            return await free.ExecuteUpdateAsync(
                set => set
                    .SetProperty(n => n.LeasedUntil, leaseEnd)
                    .SetProperty(n => n.LeasedBy, RunnerName),
                cancellationToken);
        }

        var rows = await free.ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            row.LeasedUntil = leaseEnd;
            row.LeasedBy = RunnerName;
        }

        await context.SaveChangesAsync(cancellationToken);
        return rows.Count;
    }
}

/// <summary>What one sweep did: rows claimed, rows delivered, rows left owed after a named failure.</summary>
public sealed record NoticeSweepSummary(int Claimed, int Delivered, int Owed);
