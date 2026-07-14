using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
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
                user.PinAccessFailedCount = 0;
                user.PinLockoutEnd = null;
                await db.SaveChangesAsync();
            }
            _logger.LogInformation("PIN verified successfully for user {UserId}", userId);
            return true;
        }

        // Wrong PIN. An expired lock (cleared here) starts a fresh window at #1.
        var failures = user.PinLockoutEnd is not null ? 1 : user.PinAccessFailedCount + 1;
        user.PinLockoutEnd = null;

        if (failures >= ValidationRules.MaxPinAttempts)
        {
            var until = now.AddMinutes(ValidationRules.PinLockoutMinutes);
            user.PinAccessFailedCount = 0;   // the lock window is now authoritative
            user.PinLockoutEnd = until;
            await db.SaveChangesAsync();
            _logger.LogWarning("PIN locked for user {UserId} until {Until} after {Max} wrong attempts",
                userId, until, ValidationRules.MaxPinAttempts);
            throw PinLockedException.Until(until, now);
        }

        user.PinAccessFailedCount = failures;
        await db.SaveChangesAsync();
        _logger.LogWarning("Invalid PIN attempt {Count}/{Max} for user {UserId}",
            failures, ValidationRules.MaxPinAttempts, userId);
        return false;
    }
}
