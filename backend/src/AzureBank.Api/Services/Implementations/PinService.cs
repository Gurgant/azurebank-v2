using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Exceptions;
using AzureBank.Shared.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBank.Api.Services.Implementations;

/// <summary>
/// Verifies step-up PINs with attempt-limiting: after
/// <see cref="ValidationRules.MaxPinAttempts"/> consecutive wrong attempts the
/// PIN is locked for <see cref="ValidationRules.PinLockoutMinutes"/> minutes.
///
/// The lockout counter is security bookkeeping that must persist INDEPENDENTLY
/// of the caller's transaction: a wrong PIN during a withdrawal must record the
/// failed attempt even though the withdrawal itself fails, and — critically —
/// must NOT finalize the caller's pending idempotency record (ADR-0009). So all
/// reads/writes run in this service's OWN DbContext scope, never the request's.
/// </summary>
public sealed class PinService : IPinVerifier
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ILogger<PinService> _logger;

    public PinService(
        IServiceScopeFactory scopeFactory,
        IPasswordHasher passwordHasher,
        ILogger<PinService> logger)
    {
        _scopeFactory = scopeFactory;
        _passwordHasher = passwordHasher;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> VerifyPinAsync(Guid userId, string pin)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null)
        {
            _logger.LogWarning("PIN verification attempted for non-existent user {UserId}", userId);
            return false;
        }

        var now = DateTimeOffset.UtcNow;

        // Locked: refuse without running Argon2id — this also avoids leaking PIN
        // correctness via verify latency while the account is locked.
        if (user.PinLockoutEnd is { } lockedUntil && lockedUntil > now)
        {
            _logger.LogWarning("PIN verification refused for locked user {UserId} until {Until}", userId, lockedUntil);
            throw PinLockedException.Until(lockedUntil, now);
        }

        if (string.IsNullOrEmpty(user.PinHash))
        {
            _logger.LogWarning("PIN verification attempted for user {UserId} without PIN set", userId);
            return false;
        }

        if (_passwordHasher.VerifyPin(user.PinHash, pin))
        {
            // Success: clear any accumulated failures / expired lock.
            if (user.PinAccessFailedCount != 0 || user.PinLockoutEnd is not null)
            {
                await ResetLockoutAsync(db, user);
            }
            _logger.LogInformation("PIN verified successfully for user {UserId}", userId);
            return true;
        }

        // Wrong PIN. Increment ATOMICALLY (a set-based UPDATE, not read-modify-write)
        // so concurrent wrong attempts cannot lose updates and slip past the
        // threshold; also clear any now-expired lock. Because crossing the
        // threshold resets the counter to 0, a fresh window after an expired lock
        // starts naturally at 1 — no special case needed.
        var failures = await IncrementFailureAsync(db, user);

        if (failures >= ValidationRules.MaxPinAttempts)
        {
            var until = now.AddMinutes(ValidationRules.PinLockoutMinutes);
            await ApplyLockAsync(db, user, until);
            _logger.LogWarning("PIN locked for user {UserId} until {Until} after {Max} wrong attempts",
                userId, until, ValidationRules.MaxPinAttempts);
            throw PinLockedException.Until(until, now);
        }

        _logger.LogWarning("Invalid PIN attempt {Count}/{Max} for user {UserId}",
            failures, ValidationRules.MaxPinAttempts, userId);
        return false;
    }

    // ---- Atomic lockout-state writers ----
    // ExecuteUpdate issues a single set-based SQL UPDATE (no read-modify-write, so
    // no lost updates under concurrency). It is relational-only, so the InMemory
    // test provider falls back to a tracked write (single-threaded there anyway).

    private static async Task<int> IncrementFailureAsync(AzureBankDbContext db, ApplicationUser user)
    {
        if (db.Database.IsRelational())
        {
            await db.Users.Where(u => u.Id == user.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.PinAccessFailedCount, u => u.PinAccessFailedCount + 1)
                    .SetProperty(u => u.PinLockoutEnd, (DateTimeOffset?)null));
            return await db.Users.Where(u => u.Id == user.Id)
                .Select(u => u.PinAccessFailedCount).FirstAsync();
        }

        user.PinAccessFailedCount += 1;
        user.PinLockoutEnd = null;
        await db.SaveChangesAsync();
        return user.PinAccessFailedCount;
    }

    private static async Task ApplyLockAsync(AzureBankDbContext db, ApplicationUser user, DateTimeOffset until)
    {
        if (db.Database.IsRelational())
        {
            await db.Users.Where(u => u.Id == user.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.PinLockoutEnd, until)
                    .SetProperty(u => u.PinAccessFailedCount, 0));
            return;
        }

        user.PinLockoutEnd = until;      // the lock window is now authoritative
        user.PinAccessFailedCount = 0;
        await db.SaveChangesAsync();
    }

    private static async Task ResetLockoutAsync(AzureBankDbContext db, ApplicationUser user)
    {
        if (db.Database.IsRelational())
        {
            await db.Users.Where(u => u.Id == user.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.PinAccessFailedCount, 0)
                    .SetProperty(u => u.PinLockoutEnd, (DateTimeOffset?)null));
            return;
        }

        user.PinAccessFailedCount = 0;
        user.PinLockoutEnd = null;
        await db.SaveChangesAsync();
    }
}
