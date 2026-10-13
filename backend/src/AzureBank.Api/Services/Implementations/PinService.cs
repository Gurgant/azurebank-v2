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
/// An attempt is claimed on the user's row BEFORE the PIN is compared, in one conditional
/// UPDATE that also applies the lock on the claim that reaches the threshold. So requests that
/// arrive together cannot each read "not locked" and each be compared: the row hands out
/// <see cref="ValidationRules.MaxPinAttempts"/> comparisons per window and refuses the rest
/// unread (ADR-0010). A claim whose request then dies stays counted: the limiter fails closed.
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
    public async Task<bool> VerifyPinAsync(Guid userId, string pin, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
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

        var claim = await ClaimAttemptAsync(db, user, cancellationToken);
        if (claim.LockedUntil is { } refusedUntil)
        {
            _logger.LogWarning("PIN verification refused for locked user {UserId} until {Until}", userId, refusedUntil);
            throw PinLockedException.Until(refusedUntil, DateTimeOffset.UtcNow);
        }
        if (!claim.Claimed)
        {
            _logger.LogWarning("PIN verification attempted for non-existent user {UserId}", userId);
            return false;
        }

        if (_passwordHasher.VerifyPin(user.PinHash, pin))
        {
            // Unconditional: the claim above counted this attempt, and this read of the user
            // does not show it. It clears whatever lock is on the row as well: the one this
            // claim applied at the threshold, or one a wrong PIN sent beside it applied while
            // this PIN was being compared.
            await ResetLockoutAsync(db, user, cancellationToken);
            // Transparent pepper migration (ADR-0011): if the stored hash predates the
            // active pepper key, re-hash the PIN now that the plaintext is available and
            // persist it — in this service's own scope, like the lockout bookkeeping.
            // Best-effort: the PIN was already verified, so a transient failure of this
            // background upgrade must NOT fail the login — it retries on the next use.
            if (_passwordHasher.PinNeedsRehash(user.PinHash))
            {
                try
                {
                    await UpgradePinHashAsync(db, user, pin, cancellationToken);
                }
                // Not a request that was cancelled (ADR-0058): that is no failed upgrade, and the
                // request is ending anyway, so the cancellation goes on up instead of an Error line.
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
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

        // Wrong PIN, already counted by the claim. Read from the row, not from the claim: the
        // lock that answers may be the one this attempt applied or one a parallel attempt did.
        var lockoutEnd = await db.Users.Where(u => u.Id == userId)
            .Select(u => u.PinLockoutEnd).FirstOrDefaultAsync(cancellationToken);
        now = DateTimeOffset.UtcNow;

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
    /// What a claim came to: the attempt is counted and may be compared (<c>Claimed</c>), or the
    /// row is locked until <c>LockedUntil</c>, or, with neither, the user's row is gone.
    /// </summary>
    private readonly record struct AttemptClaim(bool Claimed, DateTimeOffset? LockedUntil);

    /// <summary>
    /// Claims one attempt in a single statement, evaluated against the row's CURRENT value:
    /// increment the counter, and on the claim that reaches
    /// <see cref="ValidationRules.MaxPinAttempts"/> set the lock and reset the counter. The WHERE
    /// guard excludes a currently-locked row, so of any number of parallel claims only the
    /// threshold's worth update a row, and a late one can neither clear a lock that was just
    /// applied nor leave a residual count on it.
    /// </summary>
    private static async Task<AttemptClaim> ClaimAttemptAsync(
        AzureBankDbContext db, ApplicationUser user, CancellationToken cancellationToken)
    {
        var max = ValidationRules.MaxPinAttempts;

        if (db.Database.IsRelational())
        {
            while (true)
            {
                var now = DateTimeOffset.UtcNow;
                var until = now.AddMinutes(ValidationRules.PinLockoutMinutes);
                var claimed = await db.Users
                    .Where(u => u.Id == user.Id && (u.PinLockoutEnd == null || u.PinLockoutEnd <= now))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(u => u.PinAccessFailedCount,
                            u => u.PinAccessFailedCount + 1 >= max ? 0 : u.PinAccessFailedCount + 1)
                        .SetProperty(u => u.PinLockoutEnd,
                            u => u.PinAccessFailedCount + 1 >= max ? (DateTimeOffset?)until : null)
                        .SetProperty(u => u.UpdatedAt, (DateTime?)DateTime.UtcNow), cancellationToken);
                if (claimed > 0)
                {
                    return new AttemptClaim(Claimed: true, LockedUntil: null);
                }

                var row = await db.Users.Where(u => u.Id == user.Id)
                    .Select(u => new { u.PinLockoutEnd }).FirstOrDefaultAsync(cancellationToken);
                if (row is null)
                {
                    return new AttemptClaim(Claimed: false, LockedUntil: null);
                }
                if (row.PinLockoutEnd is { } lockedUntil && lockedUntil > now)
                {
                    return new AttemptClaim(Claimed: false, LockedUntil: lockedUntil);
                }
                // The lock that refused the claim is gone by the time it is read: a right PIN
                // took it back in between. The row is free again, so the claim is made again.
            }
        }

        var untilInMemory = DateTimeOffset.UtcNow.AddMinutes(ValidationRules.PinLockoutMinutes);
        if (user.PinAccessFailedCount + 1 >= max)
        {
            user.PinAccessFailedCount = 0;   // the lock window is now authoritative
            user.PinLockoutEnd = untilInMemory;
        }
        else
        {
            // The caller has already refused a row that is still locked, so a lock read here
            // has run out.
            user.PinAccessFailedCount += 1;
            user.PinLockoutEnd = null;
        }
        user.UpdatedAt = DateTime.UtcNow;   // parity with the relational writer's audit bump
        await db.SaveChangesAsync(cancellationToken);
        return new AttemptClaim(Claimed: true, LockedUntil: null);
    }

    /// <summary>
    /// Re-hashes the PIN with the currently active pepper and persists it
    /// (rehash-on-use migration, ADR-0011). Runs in this service's own scope; the
    /// hash column is not part of any concurrency-sensitive counter, so a single
    /// set-based update is sufficient.
    /// </summary>
    private async Task UpgradePinHashAsync(
        AzureBankDbContext db, ApplicationUser user, string pin, CancellationToken cancellationToken)
    {
        var newHash = _passwordHasher.HashPin(pin);
        if (db.Database.IsRelational())
        {
            // The UpdatedAt bump is not decoration: this writes a CREDENTIAL, and ExecuteUpdate
            // bypasses SaveChanges, so nothing else stamps the row. Without it the relational path
            // silently diverged from the InMemory one below — which goes through SaveChanges and
            // therefore gets stamped by UpdateTimestamps — leaving a credential change that is
            // invisible in the audit columns on the provider that actually ships.
            await db.Users.Where(u => u.Id == user.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.PinHash, newHash)
                    .SetProperty(u => u.UpdatedAt, (DateTime?)DateTime.UtcNow), cancellationToken);
        }
        else
        {
            user.PinHash = newHash;
            await db.SaveChangesAsync(cancellationToken);   // UpdateTimestamps stamps UpdatedAt on this path
        }
        _logger.LogInformation("Upgraded PIN hash to the active pepper for user {UserId}", user.Id);
    }

    private static async Task ResetLockoutAsync(
        AzureBankDbContext db, ApplicationUser user, CancellationToken cancellationToken)
    {
        if (db.Database.IsRelational())
        {
            await db.Users.Where(u => u.Id == user.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.PinAccessFailedCount, 0)
                    .SetProperty(u => u.PinLockoutEnd, (DateTimeOffset?)null)
                    .SetProperty(u => u.UpdatedAt, (DateTime?)DateTime.UtcNow), cancellationToken);
            return;
        }

        user.PinAccessFailedCount = 0;
        user.PinLockoutEnd = null;
        user.UpdatedAt = DateTime.UtcNow;   // parity with the relational writer's audit bump
        await db.SaveChangesAsync(cancellationToken);
    }
}
