using AzureBank.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AzureBank.Api.Services;

/// <summary>
/// Periodically removes expired refresh tokens.
///
/// Notes:
/// - Cleanup is HYGIENE, not correctness: every read path already filters on expiry and
///   revocation (RotateAsync treats an expired/absent token as invalid), so an un-swept row
///   is inert. This sweep just stops the table growing unbounded — each login and each
///   rotation writes a row.
/// - The RefreshTokens table self-references itself (ReplacedByTokenId, DeleteBehavior.Restrict)
///   to form the rotation chain, so a single set-based DELETE can trip the FK when a surviving
///   row still points at a row being deleted. We first NULL every link whose TARGET is expiring
///   (from live rows too, not just expired ones), then delete. Nulling by target — rather than
///   by the holder's own expiry — stays correct even if the token lifetime config is SHRUNK
///   (or the clock jumps back), which can leave a still-live predecessor pointing at an
///   already-expired successor.
/// - First sweep runs one full interval after startup (no interference with fast test hosts).
/// </summary>
public class RefreshTokenCleanupService : BackgroundService
{
    // Expiry is enforced on read, so the sweep only bounds table growth; every 6 hours is
    // ample against a 7-day token lifetime.
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(6);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RefreshTokenCleanupService> _logger;

    public RefreshTokenCleanupService(
        IServiceScopeFactory scopeFactory,
        ILogger<RefreshTokenCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
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
                    _logger.LogError(ex, "Refresh-token cleanup sweep failed; will retry next interval");
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
        int removed;

        if (db.Database.IsRelational())
        {
            // Null every link whose TARGET is expiring — from live rows too, not only expired
            // ones — so the self-referencing Restrict FK cannot block the set-based delete even
            // if a still-live predecessor points at an already-expired successor (see remarks).
            await db.RefreshTokens
                .Where(t => t.ReplacedByTokenId != null
                    && db.RefreshTokens.Any(target => target.Id == t.ReplacedByTokenId && target.ExpiresAt <= now))
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.ReplacedByTokenId, (Guid?)null), cancellationToken);

            removed = await db.RefreshTokens
                .Where(t => t.ExpiresAt <= now)
                .ExecuteDeleteAsync(cancellationToken);
        }
        else
        {
            // ExecuteUpdate/ExecuteDelete are relational-only; InMemory test hosts (which
            // register this BackgroundService) load + clear links + remove.
            var expired = await db.RefreshTokens
                .Where(t => t.ExpiresAt <= now)
                .ToListAsync(cancellationToken);
            var expiringIds = expired.Select(t => t.Id).ToHashSet();

            // Break every link that targets an expiring row (from live rows too).
            var referrers = await db.RefreshTokens
                .Where(t => t.ReplacedByTokenId != null && expiringIds.Contains(t.ReplacedByTokenId.Value))
                .ToListAsync(cancellationToken);
            foreach (var token in referrers)
            {
                token.ReplacedByTokenId = null;
            }
            await db.SaveChangesAsync(cancellationToken);

            db.RefreshTokens.RemoveRange(expired);
            await db.SaveChangesAsync(cancellationToken);
            removed = expired.Count;
        }

        if (removed > 0)
        {
            _logger.LogInformation("Refresh-token cleanup removed {Count} expired tokens", removed);
        }
    }
}
