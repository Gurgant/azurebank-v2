using AzureBank.Shared.DTOs.Account;

namespace AzureBank.Api.Services.Interfaces;

/// <summary>
/// Service interface for account management operations.
/// </summary>
public interface IAccountService
{
    /// <summary>
    /// Gets all accounts for a user, ordered by primary status then creation date.
    /// </summary>
    Task<List<AccountResponse>> GetUserAccountsAsync(Guid userId);

    /// <summary>
    /// Gets a specific account by ID with ownership verification.
    /// </summary>
    Task<AccountResponse> GetAccountByIdAsync(Guid accountId, Guid userId);

    /// <summary>
    /// Creates a new account for a user.
    /// </summary>
    Task<AccountResponse> CreateAccountAsync(Guid userId, CreateAccountRequest request);

    /// <summary>
    /// Updates an existing account (name only).
    /// </summary>
    Task<AccountResponse> UpdateAccountAsync(Guid accountId, Guid userId, UpdateAccountRequest request);

    /// <summary>
    /// Sets an account as the primary account for the user.
    /// </summary>
    Task SetPrimaryAccountAsync(Guid userId, Guid accountId);

    /// <summary>
    /// Soft deletes an account (balance must be zero, cannot be primary).
    /// </summary>
    Task DeleteAccountAsync(Guid accountId, Guid userId);

    /// <summary>
    /// Gets the current or historical balance for an account.
    /// </summary>
    /// <param name="accountId">Account identifier</param>
    /// <param name="userId">User identifier for ownership verification</param>
    /// <param name="atTime">Optional: Get balance at specific point in time (null = current)</param>
    Task<BalanceResponse> GetBalanceAsync(Guid accountId, Guid userId, DateTime? atTime = null);

    /// <summary>
    /// Reveals the FULL (unmasked) account number of an owned account — the one read
    /// that bypasses the mapper's masking. Audited (SecurityEvent AccountNumberRevealed).
    /// </summary>
    Task<AccountNumberResponse> GetFullAccountNumberAsync(Guid accountId, Guid userId);
}
