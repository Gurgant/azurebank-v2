namespace AzureBank.Shared.Exceptions;

/// <summary>
/// A write lost the race for its account's row on every attempt it is given, and the store shows
/// that nothing of the request was written: the request only has to be sent again. Answered 503
/// <c>SERVICE_UNAVAILABLE</c> with <see cref="RetryAfterSeconds"/>, where the conflict left to
/// propagate is a 500.
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
/// RAISED ON TWO PROOFS, and a deposit is the one write that raises it. The last conflict names
/// accounts only (<c>ConcurrencyRetry.IsAccountRace</c>), and the idempotency record, read then,
/// is still the request's own claim and still <c>Processing</c>, with no commit let start
/// (<c>ConcurrencyRetry.ClaimIsOwnAndNothingCommittedAsync</c>). The first is not enough: a save
/// that lost the account's row and the record's names the account alone. Without the second the
/// conflict propagates, as it does when another request holds the key or the deposit is made.
/// </para>
/// </remarks>
public sealed class AccountBusyException : Exception
{
    /// <summary>
    /// The wait a busy account names, as <c>retryAfterSeconds</c> and <c>Retry-After</c>: one
    /// second. The header counts whole seconds, and 0 would send the caller straight back into the
    /// writes it lost to, so 1 is the least wait it can name. It does not say the account is free
    /// by then: a caller that comes back to an account still busy is answered the same way.
    /// </summary>
    public const int RetryAfterSeconds = 1;

    public AccountBusyException(Exception conflict)
        : base("Every attempt at the write lost the account's row to another write.", conflict)
    {
    }
}
