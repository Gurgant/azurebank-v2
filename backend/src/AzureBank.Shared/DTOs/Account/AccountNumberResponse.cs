namespace AzureBank.Shared.DTOs.Account;

/// <summary>
/// The on-demand reveal of a single account's FULL (unmasked) account number.
/// Every other account DTO carries the masked form (AccountMapper); this dedicated type
/// exists so the unmasked value is a deliberate, self-describing, separately-audited
/// shape — never a field a generic mapping could adopt by accident.
/// </summary>
public class AccountNumberResponse
{
    public Guid AccountId { get; set; }

    /// <summary>
    /// The full, unmasked account number (e.g. AB-1234-5678-90).
    /// </summary>
    public required string AccountNumber { get; set; }
}
