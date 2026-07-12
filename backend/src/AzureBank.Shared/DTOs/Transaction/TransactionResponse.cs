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
}
