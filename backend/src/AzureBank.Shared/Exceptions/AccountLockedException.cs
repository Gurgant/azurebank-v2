using AzureBank.Shared.Constants;

namespace AzureBank.Shared.Exceptions;

/// <summary>
/// Thrown when the login's lock (<see cref="ValidationRules.MaxLoginAttempts"/> failures)
/// refuses a password proof made behind a session, where the account is already known: the
/// password proof of a first PIN (ADR-0040). Rendered as HTTP 429 Too Many Requests +
/// errorCode ACCOUNT_LOCKED + a Retry-After header, which the BFF does not forward, with the
/// unlock time surfaced via Details (retryAfterSeconds, lockedUntil).
///
/// It is thrown for every password, the right one included, without reading it, so it cannot
/// tell whether the password was right. Sign-in never throws it: there a locked account is
/// answered with the 401 of a wrong password and of an email nobody has, so the lock is no
/// oracle for a password guesser (ADR-0012 decision 3).
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
