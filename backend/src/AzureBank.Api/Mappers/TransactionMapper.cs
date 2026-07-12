using AzureBank.Shared.DTOs.Transaction;
using AzureBank.Shared.Entities;
using Riok.Mapperly.Abstractions;

namespace AzureBank.Api.Mappers;

/// <summary>
/// Mapperly-based mapper for Transaction entity to DTO conversions.
/// Source generator - no runtime reflection overhead.
/// </summary>
/// <remarks>
/// Uses RequiredMappingStrategy.Target: only validates DTO properties are filled.
/// Intentionally unmapped entity properties (by design):
/// - AccountId, RelatedTransactionId: Internal FKs
/// - BalanceBefore: Client has BalanceAfter, can calculate if needed
/// - Account, RelatedTransaction: Navigation properties
/// </remarks>
[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public partial class TransactionMapper
{
    /// <summary>
    /// Maps Transaction entity to TransactionResponse DTO.
    /// </summary>
    public partial TransactionResponse ToResponse(Transaction entity);

    /// <summary>
    /// Maps list of Transaction entities to list of TransactionResponse DTOs.
    /// </summary>
    public partial List<TransactionResponse> ToResponseList(List<Transaction> entities);

    /// <summary>
    /// Creates a DepositResponse from transaction data.
    /// </summary>
    public DepositResponse ToDepositResponse(Transaction transaction, decimal newBalance)
    {
        return new DepositResponse
        {
            Transaction = ToResponse(transaction),
            NewBalance = newBalance
        };
    }

    /// <summary>
    /// Creates a WithdrawResponse from transaction data.
    /// </summary>
    public WithdrawResponse ToWithdrawResponse(Transaction transaction, decimal newBalance)
    {
        return new WithdrawResponse
        {
            Transaction = ToResponse(transaction),
            NewBalance = newBalance
        };
    }
}
