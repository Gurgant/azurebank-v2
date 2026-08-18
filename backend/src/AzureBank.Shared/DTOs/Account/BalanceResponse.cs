using AzureBank.Shared.Constants;

namespace AzureBank.Shared.DTOs.Account;

/// <summary>
/// Response for balance inquiries (current or historical).
/// Used by GET /api/accounts/{id}/balance endpoint.
/// </summary>
public class BalanceResponse
{
    /// <summary>
    /// Account identifier
    /// </summary>
    public Guid AccountId { get; set; }

    /// <summary>
    /// Balance amount
    /// </summary>
    public decimal Balance { get; set; }

    /// <summary>
    /// The product's denomination, from the single declaration rather than repeated here.
    /// </summary>
    public string Currency { get; set; } = ValidationRules.Currency;

    /// <summary>
    /// Timestamp of the balance (current time or requested historical time)
    /// </summary>
    public DateTime AsOf { get; set; }

    /// <summary>
    /// True if this is a historical balance query, false for current balance
    /// </summary>
    public bool IsHistorical { get; set; }
}
