using AzureBank.Shared.Enums;
using System.ComponentModel.DataAnnotations;

namespace AzureBank.Shared.Entities;

public class Account : BaseEntity
{
    public Guid UserId { get; set; }
    [Required]
    public required string AccountNumber { get; set; }
    [Required]
    public required string Name { get; set; }
    public AccountType Type { get; set; }
    // DECIMAL(19,4)
    public decimal Balance { get; set; }
    public bool IsPrimary { get; set; }

    /// <summary>
    /// Optimistic concurrency token - auto-managed by SQL Server
    /// Only property that needs = null! (DB generates it)
    /// </summary>
    public byte[] RowVersion { get; set; } = null!;

    // Navigation properties
    public required ApplicationUser User { get; set; }
    public ICollection<Transaction> Transactions { get; set; } = [];
}
