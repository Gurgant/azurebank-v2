using AzureBank.Shared.Entities;

namespace AzureBank.Api.Services.Interfaces;

/// <summary>
/// Service for retrieving accounts with ownership verification.
/// Eliminates duplicate ownership check logic across services.
/// </summary>
public interface IAccountAccessService
{
    /// <summary>
    /// Gets an account with ownership verification.
    /// </summary>
    /// <param name="accountId">The account ID to retrieve.</param>
    /// <param name="userId">The user ID who must own the account.</param>
    /// <returns>The account if found and owned by the user.</returns>
    /// <exception cref="AzureBank.Shared.Exceptions.NotFoundException">
    /// Thrown when the account doesn't exist or is soft-deleted.
    /// </exception>
    /// <exception cref="AzureBank.Shared.Exceptions.AuthorizationException">
    /// Thrown when the account doesn't belong to the specified user.
    /// </exception>
    /// <param name="cancellationToken">Cancels the work: the request's token, which its deadline or the caller hanging up cancels (ADR-0058).</param>
    Task<Account> GetAccountWithOwnershipCheckAsync(
        Guid accountId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if an account exists and belongs to the specified user.
    /// Does not throw exceptions - returns false if check fails.
    /// </summary>
    /// <param name="accountId">The account ID to check.</param>
    /// <param name="userId">The user ID who must own the account.</param>
    /// <returns>True if account exists and belongs to user, false otherwise.</returns>
    /// <param name="cancellationToken">Cancels the work: the request's token, which its deadline or the caller hanging up cancels (ADR-0058).</param>
    Task<bool> ValidateAccountOwnershipAsync(Guid accountId, Guid userId, CancellationToken cancellationToken = default);
}
