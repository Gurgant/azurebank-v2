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

    /// <summary>The account the demo ledger hangs off, looked up by its literal number.</summary>
    private const string JohnSavingsAccountNumber = "AB-1234-5678-90";

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
            .FirstOrDefaultAsync(a => a.AccountNumber == JohnSavingsAccountNumber, cancellationToken);

        if (johnSavings == null)
        {
            _logger.LogWarning("John's savings account not found. Cannot seed transactions.");
            return;
        }

        /*
          INSERT AND AGE ATOMICALLY. The two steps have to commit together or not at all, because
          the guard at the top of this method treats "any transactions exist" as "already seeded":
          rows committed without their dates would leave a permanently flat ledger that a re-run
          silently declines to repair. That is the same two-phase shape ADR-0036 was written about —
          a first step that commits, a second that can fail, and no path back.

          RUN THROUGH THE EXECUTION STRATEGY, because AddInfrastructure enables EnableRetryOnFailure
          and EF refuses a user-initiated transaction under a retrying strategy otherwise.

          EACH ATTEMPT STARTS FROM DATABASE TRUTH, and this is the part an earlier draft of this
          comment got wrong — it claimed a retry "starts from a clean slate" because the rows are
          built inside the delegate. They are, but the CONTEXT is not: a failed SaveChanges leaves
          the first batch tracked as Added, so a retry that builds a second batch would add it
          alongside the first and insert EIGHT rows, of which only four carry their dates. Clearing
          the tracker and re-reading the account is what actually makes the attempt self-contained.
          A fresh DbContext per attempt would do the same, but nothing registers an
          IDbContextFactory here, and one cleared context is the smaller change for a single-threaded
          tool.

          verifySucceeded covers the remaining case: if the COMMIT itself fails ambiguously, the
          strategy asks whether the work landed anyway rather than blindly seeding a second time.
        */
        var count = 0;
        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteInTransactionAsync(
            async ct =>
            {
                _context.ChangeTracker.Clear();
                var account = await _context.Accounts
                    .FirstAsync(a => a.AccountNumber == JohnSavingsAccountNumber, ct);

                var transactions = CreateTransactions(account);

                // Capture the dates BEFORE saving, because SaveChanges is about to overwrite them.
                var occurredAt = transactions.ToDictionary(t => t.Id, t => t.CreatedAt);

                await _context.Transactions.AddRangeAsync(transactions, ct);
                await _context.SaveChangesAsync(ct);

                await AgeToOccurrenceDatesAsync(occurredAt, ct);
                count = transactions.Count;
            },
            async ct => await _context.Transactions.AnyAsync(ct),
            cancellationToken);

        _logger.LogInformation("Seeded {Count} transactions", count);
    }

    /// <summary>
    /// Applies each transaction's intended date with an UPDATE that never passes through the change
    /// tracker.
    ///
    /// <para>
    /// This is required, not stylistic. <c>AzureBankDbContext.UpdateTimestamps()</c> has a loop
    /// specifically for <c>Transaction</c> ("doesn't inherit BaseEntity") that assigns
    /// <c>CreatedAt = DateTime.UtcNow</c> to every Added row, so the dates set in
    /// <c>CreateTransactions</c> never survive the save — measured against LocalDB, a row asked for
    /// 2026-08-06 11:57 was stored as 2026-08-09 11:57. Saving a second time is not an option
    /// either: <c>EnforceTransactionImmutability</c> rejects any Modified transaction.
    /// </para>
    /// <para>
    /// <c>ExecuteUpdateAsync</c> is the technique this repository already sanctions for exactly this
    /// problem — <c>HistoricalBalanceSqlServerTests.AgeTransactionsAsync</c> ages its fixture rows
    /// the same way, and documents the same two reasons. It bypasses both guards precisely because
    /// it bypasses the change tracker, which is why it belongs in dev tooling and a SQL-gated test
    /// and nowhere near a request path. Relational-only, which the seeder always is.
    /// </para>
    /// <para>
    /// Without this the four demo rows all landed in the same minute: the history screen grouped
    /// them under one date, and — because CI seeds the database the real-stack contract, integration
    /// and E2E jobs run against — any date filtering or historical-balance behaviour was being
    /// exercised against a ledger with no time spread at all.
    /// </para>
    /// </summary>
    private async Task AgeToOccurrenceDatesAsync(
        IReadOnlyDictionary<Guid, DateTime> occurredAt, CancellationToken cancellationToken)
    {
        foreach (var (id, at) in occurredAt)
        {
            await _context.Transactions
                .Where(t => t.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.CreatedAt, at), cancellationToken);
        }
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

          A NOTE ON THE DATE INSIDE THE NUMBER, because it is deliberately not the row's date. The
          generator stamps DateTime.UtcNow, so all four numbers carry today while the rows below are
          back-dated by up to three days. That is accepted rather than overlooked: the alternative is
          a date-parameterised generator seam existing only for demo data, and the number's date has
          never been load-bearing — nothing parses it, and ADR-0035 put the check symbol over it
          precisely so it is verified rather than interpreted.

          The CreatedAt values below do NOT survive SaveChanges on their own —
          AzureBankDbContext.UpdateTimestamps() has a loop specifically for Transaction ("doesn't
          inherit BaseEntity") that overwrites every Added row with UtcNow. Measured against LocalDB:
          requested 2026-08-06 11:57, stored 2026-08-09 11:57. They are applied afterwards by
          AgeToOccurrenceDatesAsync, so what is written here is the intent and that method is what
          makes it true.
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
