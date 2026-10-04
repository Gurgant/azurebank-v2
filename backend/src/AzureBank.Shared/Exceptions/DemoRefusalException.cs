using AzureBank.Shared.Constants;

namespace AzureBank.Shared.Exceptions;

/// <summary>
/// 429: the public demo refuses for now. Three refusals, each with a code of its own, because
/// what a visitor can do about each differs: come back when a copy is free, come back when the
/// client's allowance of copies is, or start over with a fresh copy.
/// </summary>
/// <remarks>
/// Only the daily limit ends at an instant that can be computed, so only <see cref="DailyLimit"/>
/// carries <c>retryAfterSeconds</c>, which <c>AppExceptionHandler</c> puts in the body and in the
/// <c>Retry-After</c> header. The other two name no wait, in the body or in a header.
/// </remarks>
public sealed class DemoRefusalException : AppException
{
    /// <summary>The sentence of <see cref="PoolEmpty"/>.</summary>
    public const string PoolEmptyDetail = "All demo copies are in use right now. Please try again later.";

    /// <summary>The sentence of <see cref="DailyLimit"/>. The wait is a number beside it, never in it.</summary>
    public const string DailyLimitDetail = "This network has used its demo copies for today. Please try again later.";

    /// <summary>The sentence of <see cref="CopyLimit"/>.</summary>
    public const string CopyLimitDetail = "This demo copy has reached its limit of changes. Start over to get a fresh copy.";

    private DemoRefusalException(string message, string errorCode)
        : base(message, errorCode, 429)
    {
    }

    /// <summary>No free copy is left to hand out.</summary>
    public static DemoRefusalException PoolEmpty() => new(PoolEmptyDetail, ErrorCodes.DemoPoolEmpty);

    /// <summary>
    /// This client has claimed as many copies as one client may in a day, and is back under that
    /// number in <paramref name="retryAfterSeconds"/> seconds.
    /// </summary>
    /// <remarks>
    /// A number under 1 is carried as 1. <c>AppExceptionHandler</c> writes it to the
    /// <c>Retry-After</c> header as it stands, where a negative number has no meaning, and a client
    /// told to wait no time would ask again while the refusal still holds.
    /// </remarks>
    public static DemoRefusalException DailyLimit(int retryAfterSeconds) =>
        new(DailyLimitDetail, ErrorCodes.DemoDailyLimit)
        {
            Details = new Dictionary<string, object> { ["retryAfterSeconds"] = Math.Max(1, retryAfterSeconds) },
        };

    /// <summary>This copy has made as many changes as one copy may.</summary>
    public static DemoRefusalException CopyLimit() => new(CopyLimitDetail, ErrorCodes.DemoCopyLimit);
}
