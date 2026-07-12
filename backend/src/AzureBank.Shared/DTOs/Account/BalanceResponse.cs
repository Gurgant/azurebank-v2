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
    /// Currency code (default: EUR)
    /// </summary>
    public string Currency { get; set; } = "EUR";

    /// <summary>
    /// Timestamp of the balance (current time or requested historical time)
    /// </summary>
    public DateTime AsOf { get; set; }

    /// <summary>
    /// True if this is a historical balance query, false for current balance
    /// </summary>
    public bool IsHistorical { get; set; }
}
