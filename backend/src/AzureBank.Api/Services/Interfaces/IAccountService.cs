using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.DTOs.Transfer;

namespace AzureBank.Api.Services.Interfaces;

/// <summary>
/// Service interface for account management operations.
/// </summary>
public interface IAccountService
{
    /// <summary>
    /// Gets all accounts for a user, ordered by primary status then creation date.
    /// </summary>
    Task<List<AccountResponse>> GetUserAccountsAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific account by ID with ownership verification.
    /// </summary>
    Task<AccountResponse> GetAccountByIdAsync(Guid accountId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new account for a user.
    /// </summary>
    Task<AccountResponse> CreateAccountAsync(
        Guid userId, CreateAccountRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing account (name only).
    /// </summary>
    Task<AccountResponse> UpdateAccountAsync(
        Guid accountId, Guid userId, UpdateAccountRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets an account as the primary account for the user.
    /// </summary>
    Task SetPrimaryAccountAsync(Guid userId, Guid accountId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Proves the PIN and mints the one-shot authorisation a closure of
    /// <paramref name="accountId"/> must present (ADR-0049). Ownership first, then the two closure
    /// guards (422 <c>NON_ZERO_BALANCE</c>, 422 <c>PRIMARY_ACCOUNT_DELETE</c>), and only then the
    /// PIN — so a closure that cannot happen never costs an attempt. The PIN refusals are the
    /// mint's own:
    /// 422 <c>PIN_REQUIRED</c>, 401 <c>INVALID_PIN</c>, 429 <c>PIN_LOCKED</c>.
    /// </summary>
    Task<StepUpAuthorizationResponse> AuthoriseDeletionAsync(
        Guid userId, Guid accountId, string pin, CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft deletes an account (balance must be zero, cannot be primary) under an authorisation
    /// minted by <see cref="AuthoriseDeletionAsync"/> and presented in the
    /// <c>Step-Up-Authorization</c> header (ADR-0049). None presented is 401
    /// <c>AUTHORIZATION_REQUIRED</c> and writes an <c>AccountDeletionRefused</c> row; one that does
    /// not match is 401 <c>AUTHORIZATION_INVALID</c>; one that has lapsed is 401
    /// <c>AUTHORIZATION_EXPIRED</c>. The soft delete, its <c>AccountDeleted</c> row and the spend
    /// of the authorisation commit together or not at all.
    /// </summary>
    Task DeleteAccountAsync(
        Guid accountId, Guid userId, Guid? stepUpAuthorizationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current or historical balance for an account.
    /// </summary>
    /// <param name="accountId">Account identifier</param>
    /// <param name="userId">User identifier for ownership verification</param>
    /// <param name="atTime">Optional: Get balance at specific point in time (null = current)</param>
    /// <param name="cancellationToken">Cancels the work: the request's token, which its deadline or the caller hanging up cancels (ADR-0058).</param>
    Task<BalanceResponse> GetBalanceAsync(
        Guid accountId, Guid userId, DateTime? atTime = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reveals the FULL (unmasked) account number of an owned account — the one read
    /// that bypasses the mapper's masking. Audited (SecurityEvent AccountNumberRevealed).
    /// </summary>
    Task<AccountNumberResponse> GetFullAccountNumberAsync(
        Guid accountId, Guid userId, CancellationToken cancellationToken = default);
}
