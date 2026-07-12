using AzureBank.Shared.DTOs.Transfer;

namespace AzureBank.Api.Services.Interfaces;

/// <summary>
/// Service interface for transfer operations (external and internal).
/// </summary>
public interface ITransferService
{
    /// <summary>
    /// Transfers money to another user's primary account.
    /// Throws NotFoundException if recipient doesn't exist.
    /// Throws BusinessRuleException if transferring to self.
    /// Throws InsufficientFundsException if balance is insufficient.
    /// </summary>
    Task<TransferResponse> TransferAsync(Guid userId, TransferRequest request);

    /// <summary>
    /// Transfers money between own accounts.
    /// Throws NotFoundException if account doesn't exist.
    /// Throws AuthorizationException if account doesn't belong to user.
    /// Throws InsufficientFundsException if balance is insufficient.
    /// </summary>
    Task<InternalTransferResponse> InternalTransferAsync(Guid userId, InternalTransferRequest request);
}
