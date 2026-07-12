using AzureBank.Shared.Enums;

namespace AzureBank.Shared.Entities;

/// <summary>
/// Transaction entity - IMMUTABLE (no updates, no deletes)
/// Financial records must be preserved for audit/compliance
/// </summary>
public class Transaction
{
    public Guid Id { get; set; }

    /// <summary>
    /// Human-readable transaction ID: TXN-YYYYMMDD-XXXXXX
    /// </summary>
    public required string TransactionNumber { get; set; }

    public Guid AccountId { get; set; }
    public TransactionType Type { get; set; }

    /// <summary>
    /// Transaction amount (always positive) - DECIMAL(19,4)
    /// </summary>
    public decimal Amount { get; set; }

    public decimal BalanceBefore { get; set; }

    public decimal BalanceAfter { get; set; }

    public string? Description { get; set; }

    public Guid? RelatedTransactionId { get; set; }

    public string? RecipientAzureTag { get; set; }

    public string? SenderAzureTag { get; set; }

    public TransactionStatus Status { get; set; } = TransactionStatus.Unspecified;

    /// <summary>
    /// Transaction timestamp (UTC) - set by EF Core
    /// </summary>
    public DateTime CreatedAt { get; set; }

    // Navigation properties
    public required Account Account { get; set; }
    public Transaction? RelatedTransaction { get; set; }
}
