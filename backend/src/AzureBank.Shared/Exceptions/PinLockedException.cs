using AzureBank.Shared.Constants;

namespace AzureBank.Shared.Exceptions;

/// <summary>
/// Thrown when a user's step-up PIN is temporarily locked after too many wrong
/// attempts (ValidationRules.MaxPinAttempts). Rendered as HTTP 429 Too Many
/// Requests + errorCode PIN_LOCKED + a Retry-After header, with the unlock time
/// surfaced via Details (retryAfterSeconds, lockedUntil) so clients back off precisely.
/// </summary>
public sealed class PinLockedException : AppException
{
    private PinLockedException(
        string message, int retryAfterSeconds, DateTimeOffset lockedUntil, bool pinWasCompared)
        : base(message, ErrorCodes.PinLocked, 429)
    {
        PinWasCompared = pinWasCompared;
        Details = new Dictionary<string, object>
        {
            ["retryAfterSeconds"] = retryAfterSeconds,
            ["lockedUntil"] = lockedUntil.ToString("o"),
        };
    }

    /// <summary>
    /// Whether the PIN of the refused request was compared with the stored one. True: it was read
    /// and was wrong, and the lock answers that attempt. False: the lock refused the request unread,
    /// so nothing is known of its PIN.
    /// </summary>
    /// <remarks>
    /// For the server alone, which records a refused guess and not a request against a lock
    /// (ADR-0044). It is a property and no entry of <c>Details</c>, because every entry there is
    /// written into the answer, and the answer of the lock is the same for both.
    /// </remarks>
    public bool PinWasCompared { get; }

    /// <summary>Builds the exception for a lock expiring at <paramref name="lockedUntil"/>, relative to <paramref name="now"/>.</summary>
    public static PinLockedException Until(DateTimeOffset lockedUntil, DateTimeOffset now, bool pinWasCompared)
    {
        var remaining = lockedUntil - now;
        var retryAfterSeconds = remaining > TimeSpan.Zero
            ? (int)Math.Ceiling(remaining.TotalSeconds)
            : 0;
        return new PinLockedException(
            "Too many incorrect PIN attempts. Your PIN is temporarily locked; try again later.",
            retryAfterSeconds, lockedUntil, pinWasCompared);
    }
}
