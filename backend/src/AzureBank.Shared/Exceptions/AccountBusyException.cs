namespace AzureBank.Shared.Exceptions;

/// <summary>
/// A write lost the race for an account's row on every attempt it is given, the request can show
/// that nothing of the write was committed, and nothing an attempt looks at refuses the write at
/// that moment: the request can be sent again. Answered 503 <c>SERVICE_UNAVAILABLE</c> with
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
/// RAISED ON TWO PROOFS, by seven writes. The first is the same for all: the last conflict names
/// accounts only (<c>ConcurrencyRetry.IsAccountRace</c>). The second depends on what the write
/// holds. A deposit, a withdrawal and both transfers hold an idempotency key: the record, read
/// then, is still the request's own claim and still <c>Processing</c>, with no commit let start
/// (<c>ConcurrencyRetry.ClaimIsOwnAndNothingCommittedAsync</c>). A closure, a change of the
/// primary account and a rename hold none: the request has let no commit start
/// (<c>ConcurrencyRetry.NoCommitWasLetStart</c>), which is a proof for them because each of their
/// saves lands at a gated commit, and their 503 says nothing of <c>applied</c>.
/// </para>
/// <para>
/// The first proof is not enough: a save that lost an account's row and the record's names the
/// account alone. Without the second the conflict propagates, as it does when another request
/// holds the key or the write is made.
/// </para>
/// <para>
/// AND AFTER THE SECOND PROOF, THE LOOKS OF AN ATTEMPT. Each write first takes once more the
/// looks one of its attempts takes before its save, by the method the attempt calls: what an
/// attempt refuses there is refused with the attempt's answer, and this is not raised. Where the
/// write presents a step-up authorisation, the validation that runs before the first attempt
/// then reads it again and refuses with its own 401: <c>AUTHORIZATION_INVALID</c> for one another
/// request spent, the code an attempt's spend gives, and <c>AUTHORIZATION_EXPIRED</c> for one that
/// expired, where an attempt's spend gives <c>AUTHORIZATION_INVALID</c>. So this says that
/// nothing an attempt looks at refused the write at that moment, and no more: what a request
/// resolves once, before its first attempt, as a transfer does its payee's handle, is not looked
/// at again.
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
