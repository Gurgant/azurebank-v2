using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AzureBank.Api.Services;

/// <summary>
/// Periodically removes expired idempotency records (past their 24h window).
///
/// Notes (ADR-0009):
/// - Reads are already TTL-filtered (expired rows are treated as absent), so
///   this sweep is hygiene, not correctness.
/// - Expired Executed rows are logged at Warning: they mark operations that
///   committed but whose client never saw the response — reconciliation signal.
/// - Deletes are fenced by the ClaimId concurrency token; racing a live
///   takeover simply defers the row to the next sweep.
/// - First sweep runs one full interval after startup (no interference with
///   fast test hosts).
/// </summary>
public class IdempotencyCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IdempotencyOptions _options;
    private readonly ILogger<IdempotencyCleanupService> _logger;

    public IdempotencyCleanupService(
        IServiceScopeFactory scopeFactory,
        IOptions<IdempotencyOptions> options,
        ILogger<IdempotencyCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.CleanupInterval);
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
                    _logger.LogError(ex, "Idempotency cleanup sweep failed; will retry next interval");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown.
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

        var now = DateTime.UtcNow;

        // Reconciliation signal (read-only, AsNoTracking): expired rows still in
        // Executed committed their business operation but never stored a response.
        // Log them BEFORE the bulk delete removes them. Filtered to Executed only,
        // so this stays cheap even though the delete below is set-based.
        var executedExpired = await db.IdempotencyRecords
            .AsNoTracking()
            .Where(r => r.ExpiresAt <= now && r.Status == IdempotencyStatus.Executed)
            .ToListAsync(cancellationToken);

        foreach (var record in executedExpired)
        {
            _logger.LogWarning(
                "Cleaning up EXECUTED idempotency record {Endpoint}/{Key} (user {UserId}): " +
                "the operation committed but its response was never stored — reconciliation candidate",
                record.Endpoint, record.Key, record.UserId);
        }

        try
        {
            int removed;
            if (db.Database.IsRelational())
            {
                // Set-based bulk delete: one round-trip, nothing loaded or tracked,
                // and no whole-batch failure if a single row races a takeover.
                // ExecuteDelete does NOT apply the ClaimId optimistic token, but the
                // "ExpiresAt <= now" predicate is self-fencing: reads treat expired
                // rows as absent, so no live claimant depends on one, and a concurrent
                // takeover re-inserts a fresh row with a FUTURE ExpiresAt that this
                // predicate cannot match.
                removed = await db.IdempotencyRecords
                    .Where(r => r.ExpiresAt <= now)
                    .ExecuteDeleteAsync(cancellationToken);
            }
            else
            {
                // ExecuteDelete is relational-only; the EF InMemory provider throws
                // on it. Fall back to load + RemoveRange so InMemory-backed test hosts
                // (which register this BackgroundService) behave identically. Guarding
                // on IsRelational() rather than IsInMemory() keeps the InMemory provider
                // package out of the production API.
                var expired = await db.IdempotencyRecords
                    .Where(r => r.ExpiresAt <= now)
                    .ToListAsync(cancellationToken);
                db.IdempotencyRecords.RemoveRange(expired);
                removed = await db.SaveChangesAsync(cancellationToken);
            }

            if (removed > 0)
            {
                _logger.LogInformation("Idempotency cleanup removed {Count} expired records", removed);
            }
        }
        catch (DbUpdateConcurrencyException)
        {
            // A row changed between read and delete on the tracked (InMemory) path
            // (takeover race). Leave it for the next sweep.
            _logger.LogDebug("Idempotency cleanup raced a concurrent claim; deferring to next sweep");
        }
    }
}
