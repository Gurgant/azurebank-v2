using AzureBank.Shared.Enums;

namespace AzureBank.Shared.DTOs.Account;

public class AccountSummaryResponse
{
    public Guid Id { get; set; }

    /// <summary>
    /// Masked account number for display: AB-****-****-90
    /// </summary>
    public required string MaskedAccountNumber { get; set; }

    public required string Name { get; set; }
    public AccountType Type { get; set; }
    public decimal Balance { get; set; }
}
