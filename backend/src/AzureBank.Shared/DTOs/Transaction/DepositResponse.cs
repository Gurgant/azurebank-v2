namespace AzureBank.Shared.DTOs.Transaction;

/// <summary>
/// Response returned after a successful deposit.
/// Includes the created transaction and updated balance.
/// </summary>
public class DepositResponse
{
    /// <summary>
    /// The created deposit transaction
    /// </summary>
    public required TransactionResponse Transaction { get; set; }

    /// <summary>
    /// Updated account balance after the deposit
    /// </summary>
    public decimal NewBalance { get; set; }
}
