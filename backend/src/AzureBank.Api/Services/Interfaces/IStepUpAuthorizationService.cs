using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;

namespace AzureBank.Api.Services.Interfaces;

/// <summary>
/// Mints and spends the authorisations that make a transfer's PIN specific to one movement of money
/// (ADR-0042).
///
/// <para>
/// Before this, a PIN proved only that someone knew the PIN — measured, the same six digits
/// authorised two different transfers back to back. What becomes single-use here is not the secret
/// the user types but the authorisation minted from it.
/// </para>
/// </summary>
public interface IStepUpAuthorizationService
{
    /// <summary>
    /// Proves the PIN and records the authorisation. Throws exactly what a transfer's own PIN check
    /// throws — 404 for a missing user, 422 <c>PIN_REQUIRED</c> when none is enrolled, 401
    /// <c>INVALID_PIN</c> when it is wrong, 429 <c>PIN_LOCKED</c> when the account is locked — so a
    /// caller cannot tell the two paths apart, and a wrong PIN costs an attempt in both.
    /// </summary>
    Task<StepUpAuthorization> MintAsync(
        Guid userId,
        StepUpOperation operation,
        StepUpBinding binding,
        string pin,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks that the authorisation exists, belongs to this user, is still Pending, has not expired,
    /// and was minted for exactly this operation and these fields. Read-only: it produces the right
    /// refusal, and <see cref="ConsumeAsync"/> is what actually guards against a double spend.
    /// </summary>
    Task ValidateAsync(
        Guid userId,
        Guid authorizationId,
        StepUpOperation operation,
        StepUpBinding binding,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Spends it: one set-based UPDATE conditional on the row still being Pending and unexpired.
    /// Must be called INSIDE the caller's database transaction, so an authorisation is never spent by
    /// a transfer that did not commit, and so two concurrent transfers presenting the same
    /// authorisation resolve to exactly one success.
    /// </summary>
    Task ConsumeAsync(
        Guid userId,
        Guid authorizationId,
        Guid consumedByTransactionId,
        CancellationToken cancellationToken = default);
}
