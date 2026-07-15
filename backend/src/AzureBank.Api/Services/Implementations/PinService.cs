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
            // Transparent pepper migration (ADR-0011): if the stored hash predates the
            // active pepper key, re-hash the PIN now that the plaintext is available and
            // persist it — in this service's own scope, like the lockout bookkeeping.
            // Best-effort: the PIN was already verified, so a transient failure of this
            // background upgrade must NOT fail the login — it retries on the next use.
            if (_passwordHasher.PinNeedsRehash(user.PinHash))
            {
                try
                {
                    await UpgradePinHashAsync(db, user, pin);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Transparent PIN-hash upgrade failed for user {UserId}; the correct PIN still " +
                        "verified, so the login proceeds and the upgrade retries next time.", userId);
                }
            }
            _logger.LogInformation("PIN verified successfully for user {UserId}", userId);
            return true;
        }

        // A verify can fail for two very different reasons: a genuinely wrong PIN, or
        // a stored hash whose pepper key the keyring no longer holds (a pepper retired
        // before its hashes were drained — ADR-0011). The latter fails closed here and
        // is indistinguishable to the counter, so surface it as an operator diagnostic.
        if (_passwordHasher.PinPepperMissingFor(user.PinHash))
        {
            _logger.LogWarning(
                "PIN verification for user {UserId} failed because the stored hash references a pepper key " +
                "not in the keyring — a pepper may have been retired before its hashes were migrated.", userId);
        }

        // Wrong PIN. Increment the counter and — if it crosses the threshold —
        // apply the lock in ONE atomic set-based statement. Doing both in a single
        // UPDATE (rather than increment-then-separately-lock) removes the window in
        // which a concurrent late increment could clear a just-applied lock and let
        // a burst of parallel wrong PINs slip past the threshold.
        var until = now.AddMinutes(ValidationRules.PinLockoutMinutes);
        var lockoutEnd = await IncrementAndMaybeLockAsync(db, user, now, until);

        if (lockoutEnd is { } enforcedUntil && enforcedUntil > now)
        {
            _logger.LogWarning("PIN locked for user {UserId} until {Until} after too many wrong attempts",
                userId, enforcedUntil);
            throw PinLockedException.Until(enforcedUntil, now);
        }

        _logger.LogWarning("Invalid PIN attempt for user {UserId}", userId);
        return false;
    }

    // ---- Atomic lockout-state writers ----
    // ExecuteUpdate issues a single set-based SQL UPDATE (no read-modify-write, so
    // no lost updates under concurrency). It is relational-only, so the InMemory
    // test provider falls back to an equivalent tracked write (single-threaded there).

    /// <summary>
    /// One atomic wrong-PIN transition: increment the failure counter, and when it
    /// crosses <see cref="ValidationRules.MaxPinAttempts"/> set the lock and reset the
    /// counter — all evaluated against the row's CURRENT value in a single statement.
    /// The WHERE guard excludes a currently-locked row, so a burst of parallel wrong
    /// PINs can neither bypass the threshold nor leave a residual count on a just-locked
    /// account (a late increment updates zero rows). Returns the resulting PinLockoutEnd.
    /// </summary>
    private static async Task<DateTimeOffset?> IncrementAndMaybeLockAsync(
        AzureBankDbContext db, ApplicationUser user, DateTimeOffset now, DateTimeOffset until)
    {
        var max = ValidationRules.MaxPinAttempts;

        if (db.Database.IsRelational())
        {
            await db.Users
                .Where(u => u.Id == user.Id && (u.PinLockoutEnd == null || u.PinLockoutEnd < now))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.PinAccessFailedCount,
                        u => u.PinAccessFailedCount + 1 >= max ? 0 : u.PinAccessFailedCount + 1)
                    .SetProperty(u => u.PinLockoutEnd,
                        u => u.PinAccessFailedCount + 1 >= max ? (DateTimeOffset?)until : null)
                    .SetProperty(u => u.UpdatedAt, (DateTime?)DateTime.UtcNow));
            return await db.Users.Where(u => u.Id == user.Id)
                .Select(u => u.PinLockoutEnd).FirstAsync();
        }

        if (user.PinAccessFailedCount + 1 >= max)
        {
            user.PinAccessFailedCount = 0;   // the lock window is now authoritative
            user.PinLockoutEnd = until;
        }
        else
        {
            user.PinAccessFailedCount += 1;
            if (user.PinLockoutEnd is { } expired && expired < now)
            {
                user.PinLockoutEnd = null;
            }
        }
        user.UpdatedAt = DateTime.UtcNow;   // parity with the relational writer's audit bump
        await db.SaveChangesAsync();
        return user.PinLockoutEnd;
    }

    /// <summary>
    /// Re-hashes the PIN with the currently active pepper and persists it
    /// (rehash-on-use migration, ADR-0011). Runs in this service's own scope; the
    /// hash column is not part of any concurrency-sensitive counter, so a single
    /// set-based update is sufficient.
    /// </summary>
    private async Task UpgradePinHashAsync(AzureBankDbContext db, ApplicationUser user, string pin)
    {
        var newHash = _passwordHasher.HashPin(pin);
        if (db.Database.IsRelational())
        {
            await db.Users.Where(u => u.Id == user.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.PinHash, newHash));
        }
        else
        {
            user.PinHash = newHash;
            await db.SaveChangesAsync();
        }
        _logger.LogInformation("Upgraded PIN hash to the active pepper for user {UserId}", user.Id);
    }

    private static async Task ResetLockoutAsync(AzureBankDbContext db, ApplicationUser user)
    {
        if (db.Database.IsRelational())
        {
            await db.Users.Where(u => u.Id == user.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.PinAccessFailedCount, 0)
                    .SetProperty(u => u.PinLockoutEnd, (DateTimeOffset?)null)
                    .SetProperty(u => u.UpdatedAt, (DateTime?)DateTime.UtcNow));
            return;
        }

        user.PinAccessFailedCount = 0;
        user.PinLockoutEnd = null;
        user.UpdatedAt = DateTime.UtcNow;   // parity with the relational writer's audit bump
        await db.SaveChangesAsync();
    }
}
