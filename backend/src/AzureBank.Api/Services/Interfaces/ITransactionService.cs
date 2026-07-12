using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transaction;

namespace AzureBank.Api.Services.Interfaces;

/// <summary>
/// Service interface for transaction operations (deposits, withdrawals, history).
/// </summary>
public interface ITransactionService
{
    /// <summary>
    /// Deposits money into an account.
    /// Returns the created transaction and updated balance.
    /// </summary>
    Task<DepositResponse> DepositAsync(Guid userId, DepositRequest request);

    /// <summary>
    /// Withdraws money from an account.
    /// Returns the created transaction and updated balance.
    /// Throws InsufficientFundsException if balance is insufficient.
    /// </summary>
    Task<WithdrawResponse> WithdrawAsync(Guid userId, WithdrawRequest request);

    /// <summary>
    /// Gets paginated transaction history with filtering.
    /// </summary>
    Task<PaginatedResponse<TransactionResponse>> GetTransactionsAsync(Guid userId, TransactionFilter filter);

    /// <summary>
    /// Gets a specific transaction by ID with ownership verification.
    /// </summary>
    Task<TransactionResponse> GetTransactionByIdAsync(Guid transactionId, Guid userId);
}
