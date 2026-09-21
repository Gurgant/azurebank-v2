using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transaction;
using AzureBank.Shared.DTOs.Transfer;

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
    /// Proves the PIN for one withdrawal and returns the authorisation to present on the
    /// withdrawal itself (ADR-0056). Throws exactly what a transfer mint throws — 404 for a
    /// missing account, 403 for a foreign one, 422 <c>PIN_REQUIRED</c>, 401 <c>INVALID_PIN</c>,
    /// 429 <c>PIN_LOCKED</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT check the balance: a mint is an authentication event, and the funds
    /// guard belongs inside the transaction that spends the authorisation. Asking to withdraw more
    /// than the account holds therefore mints a 201 and is refused with 422 at the withdrawal.
    /// </remarks>
    Task<StepUpAuthorizationResponse> AuthoriseWithdrawalAsync(
        Guid userId, WithdrawalAuthorizationRequest request);

    /// <summary>
    /// Withdraws money from an account, spending the authorisation minted for exactly this account
    /// and amount. Returns the created transaction and updated balance.
    /// Throws InsufficientFundsException if balance is insufficient, and
    /// <c>401 AUTHORIZATION_REQUIRED</c> when no authorisation is presented.
    /// </summary>
    /// <param name="userId">The authenticated caller.</param>
    /// <param name="request">The account, the amount, and an optional description.</param>
    /// <param name="stepUpAuthorizationId">
    /// The reference from <see cref="AuthoriseWithdrawalAsync"/>, carried in the
    /// <c>Step-Up-Authorization</c> header. Null when the header is absent or empty — both are one
    /// refusal, never a model-state 400.
    /// </param>
    Task<WithdrawResponse> WithdrawAsync(
        Guid userId, WithdrawRequest request, Guid? stepUpAuthorizationId);

    /// <summary>
    /// Gets paginated transaction history with filtering.
    /// </summary>
    Task<PaginatedResponse<TransactionResponse>> GetTransactionsAsync(Guid userId, TransactionFilter filter);

    /// <summary>
    /// Gets a specific transaction by ID with ownership verification.
    /// </summary>
    Task<TransactionResponse> GetTransactionByIdAsync(Guid transactionId, Guid userId);

    /// <summary>
    /// Aggregates the user's transactions over a date window (defaults to the current
    /// UTC calendar month): income/expenses/net from Completed transactions plus a
    /// Pending count. Computed in SQL, scoped to the caller's accounts.
    /// </summary>
    Task<TransactionSummaryResponse> GetSummaryAsync(Guid userId, TransactionSummaryFilter filter);
}
