using AzureBank.Shared.Enums;

namespace AzureBank.Shared.DTOs.Account;

public class AccountResponse
{
    public Guid Id { get; set; }
    public required string AccountNumber { get; set; }
    public required string Name { get; set; }
    public AccountType Type { get; set; }
    public decimal Balance { get; set; }
    public bool IsPrimary { get; set; }
    public DateTime CreatedAt { get; set; }
}
