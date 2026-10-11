namespace AzureBank.Shared.Exceptions;

/// <summary>
/// A write lost the race for its account's row on every attempt it is given, and wrote nothing:
/// the request only has to be sent again. Answered 503 <c>SERVICE_UNAVAILABLE</c> with
/// <see cref="RetryAfterSeconds"/>, where the conflict left to propagate is a 500.
/// </summary>
/// <remarks>
/// <para>
/// NOT AN <see cref="AppException"/>, on purpose. A domain exception is answered with its own
/// details, and what a 503 may say about <c>applied</c> is decided in one place, the outage
/// handler (<c>ServiceUnavailableExceptionHandler.NothingApplied</c>, ADR-0058 D7). This one
/// reaches that handler, so a busy account says <c>applied: false</c> under the same four
/// conditions as an outage and under no rule of its own.
/// </para>
/// <para>
/// Raised only for an account race: a conflict that involves the idempotency record means the
/// claim was taken over, and that request must never be told to send it again
/// (<c>ConcurrencyRetry.IsAccountRace</c>).
/// </para>
/// </remarks>
public sealed class AccountBusyException : Exception
{
    /// <summary>
    /// The wait a busy account names, as <c>retryAfterSeconds</c> and <c>Retry-After</c>: one
    /// second, the shortest the header can say. The writes it lost to are short, and a caller that
    /// comes back to an account still busy is answered the same way.
    /// </summary>
    public const int RetryAfterSeconds = 1;

    public AccountBusyException(Exception conflict)
        : base("Every attempt at the write lost the account's row to another write.", conflict)
    {
    }
}
