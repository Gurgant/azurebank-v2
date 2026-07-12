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


// Note: potential future features:
// - AccountStatus enum (Active, Frozen, Closed) for better account state management
// - OverdraftLimit property for Checking accounts
// - InterestRate property for Savings and Investment accounts
// - LinkedCards collection to associate debit/credit cards with the account
// - TransactionHistory collection to track all transactions on the account
// - JointAccountOwners collection for joint accounts
// - AccountSettings for user-specific preferences (e.g., notifications, statements)
// - Currency property for multi-currency support
// - AccountNickname for user-defined names
// - ScheduledPayments collection for recurring payments
// - AuditLogs for tracking changes to the account
// - Integration with budgeting tools or financial planning features
// - AccountTier enum (Standard, Premium, VIP) for differentiated services
// - RewardsPoints property for accounts with reward programs
// - MobileBankingEnabled boolean for mobile access control
// - AccountOpeningDate property to track when the account was opened

