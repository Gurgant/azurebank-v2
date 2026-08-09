using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AzureBank.Seeder.Seeders;

/// <summary>
/// Seeds sample transactions for testing.
/// Creates transactions on John's savings account to demonstrate transaction history.
/// </summary>
public class TransactionSeeder : ISeeder
{
    private readonly AzureBankDbContext _context;
    private readonly ILogger<TransactionSeeder> _logger;

    public string Name => "TransactionSeeder";
    public int Order => 4; // After AccountSeeder

    public TransactionSeeder(
        AzureBankDbContext context,
        ILogger<TransactionSeeder> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        // Idempotent: skip if transactions already exist
        if (await _context.Transactions.AnyAsync(cancellationToken))
        {
            _logger.LogInformation("Transactions already exist. Skipping transaction seeding.");
            return;
        }

        // Get John's savings account for sample transactions
        var johnSavings = await _context.Accounts
            .FirstOrDefaultAsync(a => a.AccountNumber == "AB-1234-5678-90", cancellationToken);

        if (johnSavings == null)
        {
            _logger.LogWarning("John's savings account not found. Cannot seed transactions.");
            return;
        }

        var transactions = CreateTransactions(johnSavings);

        await _context.Transactions.AddRangeAsync(transactions, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Seeded {Count} transactions", transactions.Count);
    }

    /// <summary>
    /// Creates sample transactions for demonstration.
    /// Uses Guid.CreateVersion7() for time-sortable IDs.
    /// </summary>
    private List<Transaction> CreateTransactions(Account johnSavings)
    {
        var transactions = new List<Transaction>();

        /*
          The real generator, for every row. The literals these replaced were the pre-#89 19-character
          shape, which the generator has not produced since 7a9fa26 — and this seeder is the ONLY
          place a retired transaction-number shape reached a real database rather than a test double.

          An earlier draft of this comment justified keeping hand-built numbers on the grounds that
          the rows are back-dated, so today's date inside the number would contradict CreatedAt.
          MEASURED, and it is the other way round: AzureBankDbContext.UpdateTimestamps() has a second
          loop specifically for Transaction ("doesn't inherit BaseEntity") that assigns
          CreatedAt = DateTime.UtcNow to every Added row, so the AddDays(-N) below never survives the
          save. Probed against LocalDB — requested 2026-08-06 11:57, stored 2026-08-09 11:57, while
          the old literal still carried 20260806. The hand-built numbers were the contradiction; the
          generator is what removes it, and it makes the check symbol correct as a side effect.
        */

        // Salary deposit (3 days ago)
        transactions.Add(new Transaction
        {
            Id = Guid.CreateVersion7(),
            TransactionNumber = IdGenerator.GenerateTransactionNumber(),
            AccountId = johnSavings.Id,
            Account = johnSavings,
            Type = TransactionType.Deposit,
            Amount = 5000.00m,
            BalanceBefore = 7450.00m,
            BalanceAfter = 12450.00m,
            Description = "Salary deposit - January 2026",
            Status = TransactionStatus.Completed,
            CreatedAt = DateTime.UtcNow.AddDays(-3)
        });

        // ATM withdrawal (2 days ago)
        transactions.Add(new Transaction
        {
            Id = Guid.CreateVersion7(),
            TransactionNumber = IdGenerator.GenerateTransactionNumber(),
            AccountId = johnSavings.Id,
            Account = johnSavings,
            Type = TransactionType.Withdrawal,
            Amount = 200.00m,
            BalanceBefore = 12450.00m,
            BalanceAfter = 12250.00m,
            Description = "ATM withdrawal",
            Status = TransactionStatus.Completed,
            CreatedAt = DateTime.UtcNow.AddDays(-2)
        });

        // Online purchase (yesterday)
        transactions.Add(new Transaction
        {
            Id = Guid.CreateVersion7(),
            TransactionNumber = IdGenerator.GenerateTransactionNumber(),
            AccountId = johnSavings.Id,
            Account = johnSavings,
            Type = TransactionType.Withdrawal,
            Amount = 150.00m,
            BalanceBefore = 12250.00m,
            BalanceAfter = 12100.00m,
            Description = "Online purchase - Electronics",
            Status = TransactionStatus.Completed,
            CreatedAt = DateTime.UtcNow.AddDays(-1)
        });

        // Refund (today)
        transactions.Add(new Transaction
        {
            Id = Guid.CreateVersion7(),
            TransactionNumber = IdGenerator.GenerateTransactionNumber(),
            AccountId = johnSavings.Id,
            Account = johnSavings,
            Type = TransactionType.Deposit,
            Amount = 350.00m,
            BalanceBefore = 12100.00m,
            BalanceAfter = 12450.00m,
            Description = "Refund - Return item",
            Status = TransactionStatus.Completed,
            CreatedAt = DateTime.UtcNow
        });

        return transactions;
    }
}
