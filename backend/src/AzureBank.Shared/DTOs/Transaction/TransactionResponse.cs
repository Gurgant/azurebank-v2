using AzureBank.Shared.Enums;

namespace AzureBank.Shared.DTOs.Transaction;

public class TransactionResponse
{
    public Guid Id { get; set; }
    public required string TransactionNumber { get; set; }
    public TransactionType Type { get; set; }
    public decimal Amount { get; set; }
    public decimal BalanceAfter { get; set; }
    public string? Description { get; set; }
    public string? RecipientAzureTag { get; set; }
    public string? SenderAzureTag { get; set; }
    public TransactionStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// The <c>Idempotency-Key</c> of the money request that wrote this row, so that a client
    /// that lists by a key can check each row against it. Null on a row no keyed request wrote,
    /// and on the payee's row of a transfer from another user, which never carries the sender's
    /// key (ADR-0009).
    /// </summary>
    public Guid? IdempotencyKey { get; set; }
}
