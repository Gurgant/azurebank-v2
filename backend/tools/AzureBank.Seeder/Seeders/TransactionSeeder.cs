using AzureBank.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AzureBank.Seeder.Seeders;

/// <summary>
/// Seeds the fixed demo's ledger: two months of history on John's two accounts, and the transfers
/// that connect them to Jane's and Mike's.
///
/// <para>
/// The rows themselves are <see cref="DemoLedger"/>'s, the same history every copy of the demo pool
/// is built from: what moved, when, and how each account's rows chain to its balance are described
/// there. This class finds the four accounts <see cref="AccountSeeder"/> created, by their literal
/// numbers, and writes that ledger onto them once.
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

    // The accounts the demo ledger hangs off, looked up by the literal numbers AccountSeeder gives
    // them, and the part each plays in the ledger.
    private static readonly IReadOnlyDictionary<string, DemoLedgerAccount> LedgerAccounts =
        new Dictionary<string, DemoLedgerAccount>
        {
            ["AB-1234-5678-90"] = DemoLedgerAccount.OwnerSavings,
            ["AB-1234-5678-91"] = DemoLedgerAccount.OwnerChecking,
            ["AB-2345-6789-01"] = DemoLedgerAccount.JaneSavings,
            ["AB-3456-7890-12"] = DemoLedgerAccount.MikeInvestment,
        };

    /// <summary>
    /// How many rows the demo ledger has (<see cref="DemoLedger.RowCount"/>). <c>seed</c> and
    /// <c>reset</c> ask for this many afterwards.
    /// </summary>
    internal static int DemoLedgerRowCount => DemoLedger.RowCount;

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
        var numbers = LedgerAccounts.Keys.ToArray();
        var present = await _context.Accounts
            .CountAsync(a => numbers.Contains(a.AccountNumber), cancellationToken);
        if (present != numbers.Length)
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
                    .Where(a => numbers.Contains(a.AccountNumber))
                    .ToDictionaryAsync(a => LedgerAccounts[a.AccountNumber], ct);

                var ledger = DemoLedger.Build(accounts, DateTime.UtcNow);

                await _context.Transactions.AddRangeAsync(ledger.Select(r => r.Row), ct);
                await _context.SaveChangesAsync(ct);

                await DemoLedger.AgeAndLinkAsync(_context, ledger, ct);
                count = ledger.Count;
            },
            async ct => await _context.Transactions.AnyAsync(ct),
            cancellationToken);

        _logger.LogInformation("Seeded {Count} transactions", count);
    }
}
