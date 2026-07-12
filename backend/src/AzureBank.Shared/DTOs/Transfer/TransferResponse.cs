namespace AzureBank.Shared.DTOs.Transfer;

public class TransferResponse
{
    public required string TransactionNumber { get; set; }
    public decimal Amount { get; set; }
    public decimal NewBalance { get; set; }
    public required string RecipientAzureTag { get; set; }
    public string? RecipientName { get; set; }
    public DateTime ProcessedAt { get; set; }
}