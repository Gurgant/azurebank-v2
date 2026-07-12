namespace AzureBank.Shared.DTOs.Transfer;

/// <summary>
/// Response returned after a successful internal transfer (between own accounts).
/// Includes transfer details and both account balances.
/// </summary>
public class InternalTransferResponse
{
    /// <summary>
    /// Unique transfer identifier
    /// </summary>
    public Guid TransferId { get; set; }

    /// <summary>
    /// Transaction number for reference
    /// </summary>
    public required string TransactionNumber { get; set; }

    /// <summary>
    /// Source account ID
    /// </summary>
    public Guid FromAccountId { get; set; }

    /// <summary>
    /// Destination account ID
    /// </summary>
    public Guid ToAccountId { get; set; }

    /// <summary>
    /// Transfer amount
    /// </summary>
    public decimal Amount { get; set; }

    /// <summary>
    /// Optional transfer description
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Updated balance of the source account
    /// </summary>
    public decimal FromAccountNewBalance { get; set; }

    /// <summary>
    /// Updated balance of the destination account
    /// </summary>
    public decimal ToAccountNewBalance { get; set; }

    /// <summary>
    /// Transfer completion timestamp
    /// </summary>
    public DateTime ProcessedAt { get; set; }
}
