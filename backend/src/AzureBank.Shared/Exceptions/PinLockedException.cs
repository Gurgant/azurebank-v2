using AzureBank.Shared.Constants;

namespace AzureBank.Shared.Exceptions;

/// <summary>
/// Thrown when a user's step-up PIN is temporarily locked after too many wrong
/// attempts (ValidationRules.MaxPinAttempts). Rendered as HTTP 423 Locked +
/// errorCode PIN_LOCKED, with the unlock time surfaced via Details
/// (retryAfterSeconds, lockedUntil) so clients can back off precisely.
/// </summary>
public sealed class PinLockedException : AppException
{
    private PinLockedException(string message, int retryAfterSeconds, DateTimeOffset lockedUntil)
        : base(message, ErrorCodes.PinLocked, 423)
    {
        Details = new Dictionary<string, object>
        {
            ["retryAfterSeconds"] = retryAfterSeconds,
            ["lockedUntil"] = lockedUntil.ToString("o"),
        };
    }

    /// <summary>Builds the exception for a lock expiring at <paramref name="lockedUntil"/>, relative to <paramref name="now"/>.</summary>
    public static PinLockedException Until(DateTimeOffset lockedUntil, DateTimeOffset now)
    {
        var remaining = lockedUntil - now;
        var retryAfterSeconds = remaining > TimeSpan.Zero
            ? (int)Math.Ceiling(remaining.TotalSeconds)
            : 0;
        return new PinLockedException(
            "Too many incorrect PIN attempts. Your PIN is temporarily locked; try again later.",
            retryAfterSeconds, lockedUntil);
    }
}
