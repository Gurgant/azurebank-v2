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
        var expired = await db.IdempotencyRecords
            .Where(r => r.ExpiresAt <= now)
            .ToListAsync(cancellationToken);

        if (expired.Count == 0)
        {
            return;
        }

        foreach (var record in expired.Where(r => r.Status == IdempotencyStatus.Executed))
        {
            _logger.LogWarning(
                "Cleaning up EXECUTED idempotency record {Endpoint}/{Key} (user {UserId}): " +
                "the operation committed but its response was never stored — reconciliation candidate",
                record.Endpoint, record.Key, record.UserId);
        }

        db.IdempotencyRecords.RemoveRange(expired);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Idempotency cleanup removed {Count} expired records", expired.Count);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A row changed between read and delete (takeover race).
            // Leave it for the next sweep.
            _logger.LogDebug("Idempotency cleanup raced a concurrent claim; deferring to next sweep");
        }
    }
}
