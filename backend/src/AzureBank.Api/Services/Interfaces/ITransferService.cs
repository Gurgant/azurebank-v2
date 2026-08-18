using AzureBank.Shared.DTOs.Transfer;

namespace AzureBank.Api.Services.Interfaces;

/// <summary>
/// Service interface for transfer operations (external and internal).
/// </summary>
public interface ITransferService
{
    /// <summary>
    /// Proves the PIN and mints an authorisation for exactly this amount and this payee (ADR-0042).
    /// Applies the transfer's own refusals first — unknown source account, self-transfer, unknown or
    /// unreceivable recipient — so an authorisation can never name something the transfer would go
    /// on to reject. This is the ONLY place a transfer's PIN is presented: a wrong one costs an
    /// attempt and a locked one answers 429, both here rather than on the transfer itself.
    /// </summary>
    Task<StepUpAuthorizationResponse> AuthoriseTransferAsync(
        Guid userId, TransferAuthorizationRequest request);

    /// <summary>
    /// The same, for a transfer between the caller's own accounts.
    /// </summary>
    Task<StepUpAuthorizationResponse> AuthoriseInternalTransferAsync(
        Guid userId, InternalTransferAuthorizationRequest request);

    /// <summary>
    /// Transfers money to another user's primary account.
    /// Throws NotFoundException if recipient doesn't exist.
    /// Throws BusinessRuleException if transferring to self.
    /// Throws InsufficientFundsException if balance is insufficient.
    /// </summary>
    /// <param name="userId">The authenticated caller.</param>
    /// <param name="request">Transfer details. No PIN: it is spent at the mint endpoint.</param>
    /// <param name="stepUpAuthorizationId">
    /// The authorisation minted from the PIN for exactly this amount and payee (ADR-0042), taken
    /// from the <c>Step-Up-Authorization</c> request HEADER. REQUIRED — <c>null</c> is refused
    /// <c>401 AUTHORIZATION_REQUIRED</c>, and it is the only proof a transfer accepts. Nullable in
    /// the signature rather than required at the binding layer so that an absent header and an
    /// empty one (which MVC binds to <c>null</c> alike) reach the same refusal; see
    /// <c>TransferService.RequireAuthorization</c>. No default value, so a call site cannot omit it
    /// by accident. Never a body field — the idempotency fingerprint is computed over the body
    /// alone, so an authorisation in the body would make every retry that carries a different one a
    /// 422 instead of reaching the endpoint.
    /// </param>
    Task<TransferResponse> TransferAsync(
        Guid userId, TransferRequest request, Guid? stepUpAuthorizationId);

    /// <summary>
    /// Transfers money between own accounts.
    /// Throws NotFoundException if account doesn't exist.
    /// Throws AuthorizationException if account doesn't belong to user.
    /// Throws InsufficientFundsException if balance is insufficient.
    /// </summary>
    /// <param name="userId">The authenticated caller.</param>
    /// <param name="request">Internal transfer details. No PIN, for the same reason.</param>
    /// <param name="stepUpAuthorizationId">See <see cref="TransferAsync"/>. Internal transfers mint
    /// and spend one too: they already ask for the PIN, so binding it costs the user nothing and
    /// spares the codebase an exception to explain later.</param>
    Task<InternalTransferResponse> InternalTransferAsync(
        Guid userId, InternalTransferRequest request, Guid? stepUpAuthorizationId);
}
