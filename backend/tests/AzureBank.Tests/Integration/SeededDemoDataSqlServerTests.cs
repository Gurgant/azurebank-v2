extern alias seeder;

using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using TransactionSeeder = seeder::AzureBank.Seeder.Seeders.TransactionSeeder;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The seeded demo ledger is spread over real days, every account's history ends at the balance
/// the account holds, and every transfer is two rows linked to each other.
///
/// <para>
/// This pins dev tooling, which normally would not earn a test — except that CI runs
/// <c>AzureBank.Seeder</c> (<c>ci.yml</c>: <c>reset --confirm</c> then <c>seed</c>) to build the
/// database the real-stack contract, integration and E2E jobs run against, and the same ledger is
/// what the demo and its screenshots show. A flat ledger is not only an ugly demo; it is a fixture
/// in which date filtering and historical balance cannot be meaningfully exercised at all. And a
/// history that does not add up to the balance beside it is a bug a visitor can see: the dashboard
/// shows a running balance once an account is selected.
/// </para>
/// <para>
/// It was flat. <c>AzureBankDbContext.UpdateTimestamps()</c> has a loop specifically for
/// <c>Transaction</c> ("doesn't inherit BaseEntity") that overwrites every Added row with
/// <c>DateTime.UtcNow</c>, so the seeder's <c>AddDays(-N)</c> never survived the save — measured
/// against LocalDB, a row asked for 2026-08-06 11:57 was stored as 2026-08-09 11:57. All four demo
/// transactions of the time landed in the same minute while reading as though they were spread over
/// three days.
/// </para>
/// <para>
/// SQL-gated for the same reason <c>HistoricalBalanceSqlServerTests</c> is: the fix leans on
/// <c>ExecuteUpdateAsync</c>, which is relational-only.
/// </para>
/// </summary>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class SeededDemoDataSqlServerTests : IDisposable
{
    // The numbers the seeder looks the accounts up by, and the balances AccountSeeder gives them.
    private const string JohnSavings = "AB-1234-5678-90";
    private const string JohnChecking = "AB-1234-5678-91";
    private const string JaneSavings = "AB-2345-6789-01";
    private const string MikeInvestment = "AB-3456-7890-12";

    // Fourteen movements, and six transfers of two rows each.
    private const int LedgerRows = 26;

    [SqlServerFact]
    public async Task TheSeededLedgerIsSpreadOverDaysRatherThanOneInstant()
    {
        /*
          Its OWN database, not the shared proofs one. TransactionSeeder is idempotent by design —
          "skip if transactions already exist" — so pointed at a database other SQL-gated tests have
          written to, it does nothing and this test passes vacuously with zero rows. Found the hard
          way: the first run reported "found 0".
        */
        await using var db = NewContext();
        await db.Database.MigrateAsync();

        var accounts = await SeedTheDemoAccountsAsync(db);
        await new TransactionSeeder(db, NullLogger<TransactionSeeder>.Instance).SeedAsync();

        var rows = await db.Transactions
            .AsNoTracking()
            .OrderBy(t => t.CreatedAt)
            .ToListAsync();

        rows.Should().HaveCount(LedgerRows, "fourteen movements and six transfers of two rows each");

        // The property that was broken: distinct instants, not copies of "now". Per account, because
        // the two rows of a transfer share their instant on purpose, as a real transfer's do.
        foreach (var account in rows.GroupBy(t => t.AccountId))
        {
            account.Select(t => t.CreatedAt).Should().OnlyHaveUniqueItems(
                "a flat ledger cannot exercise date filtering or historical balance, and reads as a bug");
        }

        /*
          And they are genuinely in the PAST, spanning about two months. Distinctness alone would be
          satisfied by timestamps a millisecond apart — which is what a partial revert saving the rows
          one at a time would produce — so the span is asserted too, BOUNDED at both ends:
          greater-than alone would accept a ledger years old, so a wrong offset — AddYears for
          AddDays, say — would slip through the very assertion meant to catch a wrong offset.
        */
        var now = DateTime.UtcNow;
        rows.Should().OnlyContain(t => t.CreatedAt <= now, "seeded history must not be in the future");
        (now - rows[0].CreatedAt).TotalDays.Should().BeInRange(63, 63.5, "the oldest row, the first salary, 63 days and 4 hours back");

        /*
          EVERY offset of the original four, not just the ends. Bounding only the oldest row and the
          total span still accepts a ledger whose middle rows have collapsed next to one end — the shape
          a half-applied revert produces. Ordered ascending, the last four savings rows are the salary
          deposit (-3), the ATM withdrawal (-2), the online purchase (-1) and the refund (today).
        */
        var lastFour = rows.Where(t => t.AccountId == accounts[JohnSavings]).TakeLast(4).ToList();
        (now - lastFour[0].CreatedAt).TotalDays.Should().BeInRange(2.5, 3.5, "salary deposit, 3 days back");
        (now - lastFour[1].CreatedAt).TotalDays.Should().BeInRange(1.5, 2.5, "ATM withdrawal, 2 days back");
        (now - lastFour[2].CreatedAt).TotalDays.Should().BeInRange(0.5, 1.5, "online purchase, 1 day back");
        (now - lastFour[3].CreatedAt).TotalDays.Should().BeInRange(0, 0.5, "the refund is today");
    }

    [SqlServerFact]
    public async Task EveryAccountsHistoryEndsAtTheBalanceTheAccountHolds()
    {
        await using var db = NewContext();
        await db.Database.MigrateAsync();

        var accounts = await SeedTheDemoAccountsAsync(db);
        await new TransactionSeeder(db, NullLogger<TransactionSeeder>.Instance).SeedAsync();

        var balances = await db.Accounts.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Balance);
        var rows = await db.Transactions.AsNoTracking().ToListAsync();

        // ThenBy(Id) is the history's own tie-break (TransactionService orders by CreatedAt, then Id).
        foreach (var chain in rows.GroupBy(t => t.AccountId))
        {
            var ordered = chain.OrderBy(t => t.CreatedAt).ThenBy(t => t.Id).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var row = ordered[i];
                var signed = row.Type is TransactionType.Deposit or TransactionType.TransferIn ? row.Amount : -row.Amount;
                row.BalanceAfter.Should().Be(row.BalanceBefore + signed, "a row moves its balance by its own amount");
                if (i > 0)
                {
                    row.BalanceBefore.Should().Be(ordered[i - 1].BalanceAfter, "each row starts where the one before it ended");
                }
            }

            ordered[^1].BalanceAfter.Should().Be(balances[chain.Key], "the newest row ends at the account's balance");
            ordered[0].BalanceBefore.Should().BeGreaterThanOrEqualTo(0, "no account opened overdrawn");
        }

        rows.Select(t => t.AccountId).Distinct().Should().BeEquivalentTo(accounts.Values,
            "the ledger reaches all four accounts: John's two, and Jane's and Mike's through transfers");

        // The four original savings rows keep their numbers: salary 7,450 -> 12,450, then down to
        // 12,100 and back to the 12,450 AccountSeeder gives the account.
        var savings = rows.Where(t => t.AccountId == accounts[JohnSavings]).OrderBy(t => t.CreatedAt).TakeLast(4).ToList();
        savings.Select(t => (t.BalanceBefore, t.BalanceAfter)).Should().Equal(
            (7450.00m, 12450.00m), (12450.00m, 12250.00m), (12250.00m, 12100.00m), (12100.00m, 12450.00m));
    }

    [SqlServerFact]
    public async Task EveryTransferIsTwoRowsLinkedToEachOther()
    {
        await using var db = NewContext();
        await db.Database.MigrateAsync();

        var accounts = await SeedTheDemoAccountsAsync(db);
        await new TransactionSeeder(db, NullLogger<TransactionSeeder>.Instance).SeedAsync();

        var rows = await db.Transactions.AsNoTracking().ToDictionaryAsync(t => t.Id);
        var owners = await db.Accounts.AsNoTracking()
            .Select(a => new { a.Id, a.UserId, a.User.AzureTag })
            .ToDictionaryAsync(a => a.Id);

        var outgoing = rows.Values.Where(t => t.Type == TransactionType.TransferOut).ToList();
        outgoing.Should().HaveCount(6);
        rows.Values.Count(t => t.Type == TransactionType.TransferIn).Should().Be(6);

        foreach (var sent in outgoing)
        {
            sent.RelatedTransactionId.Should().NotBeNull("both halves are linked, as TransferService links them");
            var received = rows[sent.RelatedTransactionId!.Value];

            received.Type.Should().Be(TransactionType.TransferIn);
            received.RelatedTransactionId.Should().Be(sent.Id, "the link runs both ways");
            received.Amount.Should().Be(sent.Amount);
            received.CreatedAt.Should().Be(sent.CreatedAt, "one transfer, one instant");
            received.AccountId.Should().NotBe(sent.AccountId);

            var from = owners[sent.AccountId];
            var to = owners[received.AccountId];
            if (from.UserId == to.UserId)
            {
                // Internal: described by the accounts, and no handle on either side.
                sent.RecipientAzureTag.Should().BeNull();
                received.SenderAzureTag.Should().BeNull();
                sent.Description.Should().StartWith("Internal transfer to ");
                received.Description.Should().StartWith("Internal transfer from ");
            }
            else
            {
                // External: each side names the other party by the handle its owner has.
                sent.RecipientAzureTag.Should().Be(to.AzureTag);
                received.SenderAzureTag.Should().Be(from.AzureTag);
                received.Description.Should().Be(sent.Description, "the sender's note travels with the money");
            }
        }

        // The dashboard names recent recipients from its five newest rows: two people should be there.
        var newestFive = rows.Values
            .Where(t => t.AccountId == accounts[JohnSavings] || t.AccountId == accounts[JohnChecking])
            .OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id)
            .Take(5)
            .ToList();
        newestFive.Where(t => t.Type == TransactionType.TransferOut).Select(t => t.RecipientAzureTag)
            .Distinct().Should().BeEquivalentTo(new[] { "jane_seeded", "mike_seeded" });
    }

    [SqlServerFact]
    public async Task WithoutAllFourAccountsNothingIsSeeded()
    {
        await using var db = NewContext();
        await db.Database.MigrateAsync();

        // John's savings alone: every transfer would have a missing side, so the ledger is all or nothing.
        await SeedTheDemoAccountsAsync(db, only: JohnSavings);
        await new TransactionSeeder(db, NullLogger<TransactionSeeder>.Instance).SeedAsync();

        (await db.Transactions.CountAsync()).Should().Be(0);
    }

    [SqlServerFact]
    public async Task ATransientFailureMidSeedDoesNotDoubleInsert()
    {
        /*
          THE RETRY PATH, injected rather than reasoned about.

          The seeder runs inside EF's retrying execution strategy, and the strategy re-runs the whole
          delegate. An earlier version built the rows inside the delegate but reused the injected
          DbContext, and a comment claimed that made each attempt "a clean slate". It did not: a
          failed SaveChanges leaves the first batch tracked as Added, so the retry's second batch
          would be inserted ALONGSIDE it — twice the rows, of which only half carry their dates. The
          fix is ChangeTracker.Clear() plus re-reading the accounts, and this is what proves it.

          A bare TimeoutException is the injectable transient fault: EF 10.0.1's detector treats it
          as transient, while a real command timeout (SqlException -2) is deliberately not retried.
        */
        // Attached from the start: the marker is the Transactions INSERT, so migration DDL and the
        // account/user inserts pass through untouched.
        var transient = new TransientFailureInterceptor("INSERT INTO [Transactions]");

        await using var db = NewContext(transient);
        await db.Database.MigrateAsync();

        await SeedTheDemoAccountsAsync(db);
        await new TransactionSeeder(db, NullLogger<TransactionSeeder>.Instance).SeedAsync();

        transient.Fired.Should().BeTrue(
            "the test proves nothing if the transient fault was never actually injected");

        var rows = await db.Transactions
            .AsNoTracking()
            .OrderBy(t => t.CreatedAt)
            .ToListAsync();

        rows.Should().HaveCount(LedgerRows,
            "the retry must replace the failed attempt, not add a second batch beside it");
        foreach (var account in rows.GroupBy(t => t.AccountId))
        {
            account.Select(t => t.CreatedAt).Should().OnlyHaveUniqueItems();
        }

        // And the surviving rows are the AGED ones: a double-insert would leave half the ledger
        // stamped with the run's own clock, which this catches even if the count somehow matched.
        var now = DateTime.UtcNow;
        (now - rows[0].CreatedAt).TotalDays.Should().BeInRange(63, 63.5);
        rows.Should().OnlyContain(t => t.TransactionNumber.StartsWith("TXN-"));
        rows.Should().OnlyContain(t => t.Type == TransactionType.Deposit || t.Type == TransactionType.Withdrawal
            || t.RelatedTransactionId != null, "the links are set in the same pass as the dates");
    }

    /// <summary>
    /// The seeder looks the four accounts up by their literal numbers, so the test has to provide
    /// exactly those rows. Built by hand rather than via the API because registration mints a random
    /// number and these are fixed lookup keys inside the seeder. The owners' handles are not the
    /// seeded ones on purpose: the transfers must take them from the owners, not from a literal.
    /// </summary>
    private static async Task<Dictionary<string, Guid>> SeedTheDemoAccountsAsync(AzureBankDbContext db, string? only = null)
    {
        var john = NewUser("john", "John");
        var jane = NewUser("jane", "Jane");
        var mike = NewUser("mike", "Mike");
        var accounts = new[]
        {
            NewAccount(john, JohnSavings, "Main Savings", AccountType.Savings, 12450.00m, isPrimary: true),
            NewAccount(john, JohnChecking, "Checking", AccountType.Checking, 2300.00m, isPrimary: false),
            NewAccount(jane, JaneSavings, "Personal Savings", AccountType.Savings, 8500.00m, isPrimary: true),
            NewAccount(mike, MikeInvestment, "Investment Account", AccountType.Investment, 25000.00m, isPrimary: true),
        }.Where(a => only is null || a.AccountNumber == only).ToList();

        db.Users.AddRange(john, jane, mike);
        db.Accounts.AddRange(accounts);
        await db.SaveChangesAsync();

        return accounts.ToDictionary(a => a.AccountNumber, a => a.Id);
    }

    private static ApplicationUser NewUser(string name, string firstName) => new()
    {
        Id = Guid.CreateVersion7(),
        Email = $"{name}@seeded.example",
        UserName = $"{name}@seeded.example",
        AzureTag = $"{name}_seeded",
        FirstName = firstName,
        LastName = "Seeded",
    };

    private static Account NewAccount(
        ApplicationUser owner, string number, string name, AccountType type, decimal balance, bool isPrimary) => new()
        {
            UserId = owner.Id,
            User = owner,
            AccountNumber = number,
            Name = name,
            Type = type,
            Balance = balance,
            IsPrimary = isPrimary,
        };

    private AzureBankDbContext NewContext(IInterceptor? interceptor = null)
    {
        _database = $"AzureBankSeedProof_{Guid.NewGuid():N}";
        var cs = new SqlConnectionStringBuilder(SqlServerFactAttribute.ConnectionString!)
        {
            InitialCatalog = _database,
        }.ConnectionString;
        _connectionString = cs;

        // EnableRetryOnFailure MATCHES PRODUCTION, and it is load-bearing rather than decoration:
        // the seeder runs inside CreateExecutionStrategy(), and without a retrying strategy
        // configured here the retry path this fixture exists to exercise simply does not exist.
        // The first run of the transient test proved it — EF answered "consider enabling transient
        // error resiliency by adding 'EnableRetryOnFailure'". Same maxRetryCount as
        // AddInfrastructure uses.
        var options = new DbContextOptionsBuilder<AzureBankDbContext>()
            .UseSqlServer(cs, sql => sql.EnableRetryOnFailure(maxRetryCount: 3));
        if (interceptor is not null)
        {
            options.AddInterceptors(interceptor);
        }

        return new AzureBankDbContext(options.Options);
    }

    private string? _database;
    private string? _connectionString;

    public void Dispose()
    {
        if (_connectionString is null)
        {
            return;
        }

        // Drop the scratch database so repeated runs do not accumulate one per execution.
        using var db = new AzureBankDbContext(
            new DbContextOptionsBuilder<AzureBankDbContext>().UseSqlServer(_connectionString).Options);
        db.Database.EnsureDeleted();
    }
}
