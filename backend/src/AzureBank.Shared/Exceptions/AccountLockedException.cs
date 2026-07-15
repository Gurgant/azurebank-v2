using AzureBank.Shared.Constants;

namespace AzureBank.Shared.Exceptions;

/// <summary>
/// Thrown when an account is temporarily locked after too many failed logins
/// (<see cref="ValidationRules.MaxLoginAttempts"/>). Rendered as HTTP 429 Too Many
/// Requests + errorCode ACCOUNT_LOCKED + a Retry-After header, with the unlock time
/// surfaced via Details (retryAfterSeconds, lockedUntil) so clients back off precisely.
///
/// It is returned ONLY when the CORRECT password is presented to a locked account:
/// a wrong password always yields the generic 401 (Invalid email or password) so the
/// lock state is never an enumeration oracle for a password guesser (ADR-0012).
/// </summary>
public sealed class AccountLockedException : AppException
{
    private AccountLockedException(string message, int retryAfterSeconds, DateTimeOffset lockedUntil)
        : base(message, ErrorCodes.AccountLocked, 429)
    {
        Details = new Dictionary<string, object>
        {
            ["retryAfterSeconds"] = retryAfterSeconds,
            ["lockedUntil"] = lockedUntil.ToString("o"),
        };
    }

    /// <summary>Builds the exception for a lock expiring at <paramref name="lockedUntil"/>, relative to <paramref name="now"/>.</summary>
    public static AccountLockedException Until(DateTimeOffset lockedUntil, DateTimeOffset now)
    {
        var remaining = lockedUntil - now;
        var retryAfterSeconds = remaining > TimeSpan.Zero
            ? (int)Math.Ceiling(remaining.TotalSeconds)
            : 0;
        return new AccountLockedException(
            "Too many failed login attempts. Your account is temporarily locked; try again later.",
            retryAfterSeconds, lockedUntil);
    }
}
