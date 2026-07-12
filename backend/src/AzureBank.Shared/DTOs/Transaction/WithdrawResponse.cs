namespace AzureBank.Shared.DTOs.Transaction;

/// <summary>
/// Response returned after a successful withdrawal.
/// Includes the created transaction and updated balance.
/// </summary>
public class WithdrawResponse
{
    /// <summary>
    /// The created withdrawal transaction
    /// </summary>
    public required TransactionResponse Transaction { get; set; }

    /// <summary>
    /// Updated account balance after the withdrawal
    /// </summary>
    public decimal NewBalance { get; set; }
}
