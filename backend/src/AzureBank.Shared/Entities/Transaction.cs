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
    /// Human-readable transaction ID: TXN-YYYYMMDD-XXXXXXXXXXC, where the final character is a
    /// check symbol over the date and the suffix — not over the <c>TXN-</c> literal or the
    /// hyphens, which are outside the encoding alphabet (see
    /// <c>IdGenerator.GenerateTransactionNumber</c>).
    ///
    /// <para>
    /// Rows written before <c>WidenTransactionNumberForCheckSymbol</c> carry an older, shorter
    /// form — <b>19</b> characters before PR #89 and 20 between #89 and this change — and are left
    /// as they are. No call site validates a stored number, and renumbering a saved transaction is
    /// refused by <c>EnforceTransactionImmutability</c>. Were such a call site ever added,
    /// <c>IdGenerator.IsValidTransactionNumber</c> rejects both older shapes, so it would need a
    /// legacy branch covering <b>both</b> widths: #89 landed hours before this change, so a branch
    /// written against 20 alone would miss nearly every real row.
    /// </para>
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
