using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AzureBank.Seeder.Seeders;

/// <summary>
/// Seeds the demo ledger: two months of history on John's two accounts, and the transfers that
/// connect them to Jane's and Mike's.
///
/// <para>
/// Every account's rows chain to the balance <see cref="AccountSeeder"/> gave it: each row's
/// <c>BalanceBefore</c> is the previous row's <c>BalanceAfter</c>, and the newest row ends at the
/// account's balance. The balances are computed backwards from that figure rather than written
/// down, so the history cannot drift from the account it belongs to. Transfers are written the way
/// <c>TransferService</c> writes them: a <c>TransferOut</c> and a <c>TransferIn</c> linked to each
/// other, an external one carrying the other party's handle, an internal one described by the two
/// accounts' names.
/// </para>
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

    // The accounts the demo ledger hangs off, looked up by the literal numbers AccountSeeder gives them.
    private const string JohnSavings = "AB-1234-5678-90";
    private const string JohnChecking = "AB-1234-5678-91";
    private const string JaneSavings = "AB-2345-6789-01";
    private const string MikeInvestment = "AB-3456-7890-12";

    private static readonly string[] LedgerAccounts = [JohnSavings, JohnChecking, JaneSavings, MikeInvestment];

    /// <summary>A deposit or a withdrawal: one row, <paramref name="Ago"/> before the seed.</summary>
    private sealed record Movement(string Account, TimeSpan Ago, TransactionType Type, decimal Amount, string Description);

    /// <summary>
    /// A transfer: two rows, linked to each other. <paramref name="Note"/> is what the sender typed; an
    /// internal transfer has none and is described by its accounts, as <c>TransferService</c> does.
    /// </summary>
    private sealed record Transfer(string From, string To, TimeSpan Ago, decimal Amount, string? Note);

    private static TimeSpan Ago(int days, int hours = 0) => TimeSpan.FromDays(days) + TimeSpan.FromHours(hours);

    /*
      THE LEDGER. The last four savings movements are the original demo rows — salary three days back,
      ATM two, online purchase one, refund today — and SeededDemoDataSqlServerTests still pins them at
      those offsets. The rest reaches two months behind them, so the history has weeks to group, the
      dashboard's month has income and spending, and its five most recent rows include two people:
      the dashboard names recent recipients from them.

      Hours are offsets too, so no two rows of one account share an instant, and the two rows of a
      transfer share theirs, as a real transfer's do.
    */
    private static readonly Movement[] Movements =
    [
        new(JohnSavings, Ago(63, 4), TransactionType.Deposit, 5000.00m, "Salary deposit"),
        new(JohnSavings, Ago(48, 2), TransactionType.Withdrawal, 300.00m, "ATM withdrawal"),
        new(JohnSavings, Ago(33, 4), TransactionType.Deposit, 5000.00m, "Salary deposit"),
        new(JohnChecking, Ago(31, 3), TransactionType.Withdrawal, 950.00m, "Monthly rent"),
        new(JohnChecking, Ago(27, 7), TransactionType.Withdrawal, 84.60m, "Groceries - supermarket"),
        new(JohnSavings, Ago(25, 3), TransactionType.Withdrawal, 1200.00m, "Holiday booking - flights"),
        new(JohnChecking, Ago(20, 5), TransactionType.Withdrawal, 120.35m, "Electricity and gas bill"),
        new(JohnSavings, Ago(18, 5), TransactionType.Deposit, 120.00m, "Cashback reward"),
        new(JohnChecking, Ago(12, 4), TransactionType.Withdrawal, 15.99m, "Mobile phone plan"),
        new(JohnChecking, Ago(4, 6), TransactionType.Withdrawal, 67.20m, "Groceries - supermarket"),
        new(JohnSavings, Ago(3), TransactionType.Deposit, 5000.00m, "Salary deposit"),
        new(JohnSavings, Ago(2), TransactionType.Withdrawal, 200.00m, "ATM withdrawal"),
        new(JohnSavings, Ago(1), TransactionType.Withdrawal, 150.00m, "Online purchase - Electronics"),
        new(JohnSavings, Ago(0), TransactionType.Deposit, 350.00m, "Refund - Return item"),
    ];

    private static readonly Transfer[] Transfers =
    [
        new(JohnSavings, JohnChecking, Ago(34, 6), 1500.00m, null),
        new(JohnChecking, JaneSavings, Ago(16, 8), 60.00m, "Dinner split"),
        new(JaneSavings, JohnChecking, Ago(10, 3), 30.00m, "Taxi share"),
        new(JohnSavings, JohnChecking, Ago(6, 2), 800.00m, null),
        new(JohnChecking, MikeInvestment, Ago(1, 12), 45.00m, "Concert tickets"),
        new(JohnChecking, JaneSavings, Ago(0, 12), 25.00m, "Coffee and cake"),
    ];

    /// <summary>A row to insert, the instant it happened, and the other half of its transfer.</summary>
    private sealed record LedgerRow(Transaction Row, DateTime OccurredAt, Transaction? Pair);

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        // Idempotent: skip if transactions already exist
        if (await _context.Transactions.AnyAsync(cancellationToken))
        {
            _logger.LogInformation("Transactions already exist. Skipping transaction seeding.");
            return;
        }

        // All four, or none: a transfer with one side missing would leave a chain that ends
        // somewhere other than its account's balance.
        var present = await _context.Accounts
            .CountAsync(a => LedgerAccounts.Contains(a.AccountNumber), cancellationToken);
        if (present != LedgerAccounts.Length)
        {
            _logger.LogWarning(
                "The demo ledger spans four seeded accounts and {Count} of them exist. Cannot seed transactions.",
                present);
            return;
        }

        /*
          INSERT, AGE AND LINK ATOMICALLY. The steps have to commit together or not at all, because
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
          alongside the first and insert both, of which only one carries its dates. Clearing the
          tracker and re-reading the accounts is what actually makes the attempt self-contained. A
          fresh DbContext per attempt would do the same, but nothing registers an
          IDbContextFactory here, and one cleared context is the smaller change for a
          single-threaded tool.

          verifySucceeded covers the remaining case: if the COMMIT itself fails ambiguously, the
          strategy asks whether the work landed anyway rather than blindly seeding a second time.
        */
        var count = 0;
        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteInTransactionAsync(
            async ct =>
            {
                _context.ChangeTracker.Clear();
                var accounts = await _context.Accounts
                    .Include(a => a.User)
                    .Where(a => LedgerAccounts.Contains(a.AccountNumber))
                    .ToDictionaryAsync(a => a.AccountNumber, ct);

                var ledger = BuildLedger(accounts, DateTime.UtcNow);

                await _context.Transactions.AddRangeAsync(ledger.Select(r => r.Row), ct);
                await _context.SaveChangesAsync(ct);

                await AgeAndLinkAsync(ledger, ct);
                count = ledger.Count;
            },
            async ct => await _context.Transactions.AnyAsync(ct),
            cancellationToken);

        _logger.LogInformation("Seeded {Count} transactions", count);
    }

    /// <summary>
    /// The rows, oldest first, with their balances chained and their ids in date order.
    /// </summary>
    private static List<LedgerRow> BuildLedger(IReadOnlyDictionary<string, Account> accounts, DateTime now)
    {
        var ledger = new List<LedgerRow>();

        foreach (var movement in Movements)
        {
            var row = NewRow(accounts[movement.Account], movement.Type, movement.Amount, movement.Description);
            ledger.Add(new LedgerRow(row, now - movement.Ago, null));
        }

        foreach (var transfer in Transfers)
        {
            var from = accounts[transfer.From];
            var to = accounts[transfer.To];
            var isInternal = from.UserId == to.UserId;

            var outgoing = NewRow(from, TransactionType.TransferOut, transfer.Amount,
                isInternal ? $"Internal transfer to {to.Name}" : transfer.Note ?? $"Transfer to @{to.User.AzureTag}");
            var incoming = NewRow(to, TransactionType.TransferIn, transfer.Amount,
                isInternal ? $"Internal transfer from {from.Name}" : transfer.Note ?? $"Transfer from @{from.User.AzureTag}");
            if (!isInternal)
            {
                outgoing.RecipientAzureTag = to.User.AzureTag;
                incoming.SenderAzureTag = from.User.AzureTag;
            }

            var at = now - transfer.Ago;
            ledger.Add(new LedgerRow(outgoing, at, incoming));
            ledger.Add(new LedgerRow(incoming, at, outgoing));
        }

        ledger = [.. ledger.OrderBy(r => r.OccurredAt)];

        // Each account's chain, computed backwards from the balance it holds: the opening balance is
        // what is left once every row is taken back out, and each row then moves it forward.
        foreach (var chain in ledger.GroupBy(r => r.Row.AccountId))
        {
            var balance = chain.First().Row.Account.Balance - chain.Sum(r => Signed(r.Row));
            foreach (var entry in chain)
            {
                entry.Row.BalanceBefore = balance;
                balance += Signed(entry.Row);
                entry.Row.BalanceAfter = balance;
            }
        }

        // Version 7 ids carry a timestamp. Stamping each with the row's own instant keeps the history's
        // tie-break (CreatedAt, then Id) in agreement with the dates, as a real row's id is.
        foreach (var entry in ledger)
        {
            entry.Row.Id = Guid.CreateVersion7(new DateTimeOffset(entry.OccurredAt, TimeSpan.Zero));
            entry.Row.CreatedAt = entry.OccurredAt;
        }

        return ledger;
    }

    private static decimal Signed(Transaction row) =>
        row.Type is TransactionType.Deposit or TransactionType.TransferIn ? row.Amount : -row.Amount;

    /*
      The real generator, for every number. The literals it replaced were the pre-#89 19-character
      shape, which the generator has not produced since 7a9fa26 — and this seeder was the ONLY place a
      retired transaction-number shape reached a real database rather than a test double.

      A NOTE ON THE DATE INSIDE THE NUMBER, because it is deliberately not the row's date. The
      generator stamps DateTime.UtcNow, so every number carries today while the rows are back-dated by
      up to two months. That is accepted rather than overlooked: the alternative is a
      date-parameterised generator seam existing only for demo data, and the number's date has never
      been load-bearing — nothing parses it, and ADR-0035 put the check symbol over it precisely so it
      is verified rather than interpreted.
    */
    private static Transaction NewRow(Account account, TransactionType type, decimal amount, string description) => new()
    {
        TransactionNumber = IdGenerator.GenerateTransactionNumber(),
        AccountId = account.Id,
        Account = account,
        Type = type,
        Amount = amount,
        Description = description,
        Status = TransactionStatus.Completed,
    };

    /// <summary>
    /// Applies each row's date, and links the two halves of each transfer, with UPDATEs that never pass
    /// through the change tracker.
    ///
    /// <para>
    /// The dates are required, not stylistic. <c>AzureBankDbContext.UpdateTimestamps()</c> has a loop
    /// specifically for <c>Transaction</c> ("doesn't inherit BaseEntity") that assigns
    /// <c>CreatedAt = DateTime.UtcNow</c> to every Added row, so the dates set in
    /// <see cref="BuildLedger"/> never survive the save — measured against LocalDB, a row asked for
    /// 2026-08-06 11:57 was stored as 2026-08-09 11:57. Saving a second time is not an option either:
    /// <c>EnforceTransactionImmutability</c> rejects any Modified transaction.
    /// </para>
    /// <para>
    /// The links belong here for the same reason. Two rows that point at each other cannot go in one
    /// INSERT, so <c>TransferService</c> saves the pair and then sets the outgoing row's link in a
    /// second step; here both links are set in the same UPDATE as the date.
    /// </para>
    /// <para>
    /// <c>ExecuteUpdateAsync</c> is the technique this repository already sanctions for exactly this
    /// problem — <c>HistoricalBalanceSqlServerTests.AgeTransactionsAsync</c> ages its fixture rows the
    /// same way, and documents the same two reasons. It bypasses both guards precisely because it
    /// bypasses the change tracker, which is why it belongs in dev tooling and a SQL-gated test and
    /// nowhere near a request path. Relational-only, which the seeder always is.
    /// </para>
    /// <para>
    /// Without the dates the demo rows all landed in the same minute: the history screen grouped them
    /// under one date, and — because CI seeds the database the real-stack contract, integration and
    /// E2E jobs run against — any date filtering or historical-balance behaviour was being exercised
    /// against a ledger with no time spread at all.
    /// </para>
    /// </summary>
    private async Task AgeAndLinkAsync(IReadOnlyList<LedgerRow> ledger, CancellationToken cancellationToken)
    {
        foreach (var entry in ledger)
        {
            var id = entry.Row.Id;
            var at = entry.OccurredAt;
            if (entry.Pair is { } pair)
            {
                Guid? related = pair.Id;
                await _context.Transactions
                    .Where(t => t.Id == id)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(t => t.CreatedAt, at).SetProperty(t => t.RelatedTransactionId, related),
                        cancellationToken);
            }
            else
            {
                await _context.Transactions
                    .Where(t => t.Id == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.CreatedAt, at), cancellationToken);
            }
        }
    }
}
