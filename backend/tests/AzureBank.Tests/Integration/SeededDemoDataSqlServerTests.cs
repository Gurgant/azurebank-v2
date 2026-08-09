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
/// The seeded demo ledger is spread over real days, not collapsed into one instant.
///
/// <para>
/// This pins dev tooling, which normally would not earn a test — except that CI runs
/// <c>AzureBank.Seeder</c> (<c>ci.yml</c>: <c>reset --confirm</c> then <c>seed</c>) to build the
/// database the real-stack contract, integration and E2E jobs run against. A flat ledger is not
/// only an ugly demo; it is a fixture in which date filtering and historical balance cannot be
/// meaningfully exercised at all.
/// </para>
/// <para>
/// It was flat. <c>AzureBankDbContext.UpdateTimestamps()</c> has a loop specifically for
/// <c>Transaction</c> ("doesn't inherit BaseEntity") that overwrites every Added row with
/// <c>DateTime.UtcNow</c>, so the seeder's <c>AddDays(-N)</c> never survived the save — measured
/// against LocalDB, a row asked for 2026-08-06 11:57 was stored as 2026-08-09 11:57. All four demo
/// transactions landed in the same minute while reading as though they were spread over three days.
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

        var accountId = await SeedJohnsSavingsAccountAsync(db);
        await new TransactionSeeder(db, NullLogger<TransactionSeeder>.Instance).SeedAsync();

        var rows = await db.Transactions
            .AsNoTracking()
            .Where(t => t.AccountId == accountId)
            .OrderBy(t => t.CreatedAt)
            .ToListAsync();

        rows.Should().HaveCount(4, "the seeder creates four demo transactions");

        // The property that was broken: four DISTINCT instants, not four copies of "now".
        rows.Select(t => t.CreatedAt).Distinct().Should().HaveCount(4,
            "a flat ledger cannot exercise date filtering or historical balance, and reads as a bug");

        /*
          And they are genuinely in the PAST, spanning about three days. Distinctness alone would be
          satisfied by four timestamps a millisecond apart — which is what a partial revert saving
          the rows one at a time would produce — so the span is asserted too.
        */
        var now = DateTime.UtcNow;
        rows.Should().OnlyContain(t => t.CreatedAt <= now, "seeded history must not be in the future");

        // BOUNDED at both ends. Greater-than alone would accept a ledger years old, so a wrong
        // offset — AddYears for AddDays, say — would slip through the very assertion meant to catch
        // a wrong offset.
        /*
          EVERY offset, not just the ends. Bounding only the oldest row and the total span still
          accepts a ledger whose two middle rows have collapsed next to the oldest one — the shape a
          half-applied revert produces — because the ends would be untouched. Ordered ascending, the
          four demo rows are the salary deposit (-3), the ATM withdrawal (-2), the online purchase
          (-1) and the refund (today).
        */
        (now - rows[0].CreatedAt).TotalDays.Should().BeInRange(2.5, 3.5, "salary deposit, 3 days back");
        (now - rows[1].CreatedAt).TotalDays.Should().BeInRange(1.5, 2.5, "ATM withdrawal, 2 days back");
        (now - rows[2].CreatedAt).TotalDays.Should().BeInRange(0.5, 1.5, "online purchase, 1 day back");
        (now - rows[3].CreatedAt).TotalDays.Should().BeInRange(0, 0.5, "the refund is today");
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
          would be inserted ALONGSIDE it — eight rows, of which only four carry their dates. The fix
          is ChangeTracker.Clear() plus re-reading the account, and this is what proves it.

          A bare TimeoutException is the injectable transient fault: EF 10.0.1's detector treats it
          as transient, while a real command timeout (SqlException -2) is deliberately not retried.
        */
        // Attached from the start: the marker is the Transactions INSERT, so migration DDL and the
        // account/user inserts pass through untouched.
        var transient = new TransientFailureInterceptor("INSERT INTO [Transactions]");

        await using var db = NewContext(transient);
        await db.Database.MigrateAsync();

        var accountId = await SeedJohnsSavingsAccountAsync(db);
        await new TransactionSeeder(db, NullLogger<TransactionSeeder>.Instance).SeedAsync();

        transient.Fired.Should().BeTrue(
            "the test proves nothing if the transient fault was never actually injected");

        var rows = await db.Transactions
            .AsNoTracking()
            .Where(t => t.AccountId == accountId)
            .OrderBy(t => t.CreatedAt)
            .ToListAsync();

        rows.Should().HaveCount(4,
            "the retry must replace the failed attempt, not add a second batch beside it");
        rows.Select(t => t.CreatedAt).Distinct().Should().HaveCount(4);

        // And the surviving rows are the AGED ones: a double-insert would leave half the ledger
        // stamped with the run's own clock, which this catches even if the count somehow matched.
        var now = DateTime.UtcNow;
        (now - rows[0].CreatedAt).TotalDays.Should().BeInRange(2.5, 3.5);
        rows.Should().OnlyContain(t => t.TransactionNumber.StartsWith("TXN-"));
    }

    /// <summary>
    /// The seeder looks up John's savings account by its literal number, so the test has to provide
    /// exactly that row. Built by hand rather than via the API because registration mints a random
    /// number and this one is a fixed lookup key inside the seeder.
    /// </summary>
    private static async Task<Guid> SeedJohnsSavingsAccountAsync(AzureBankDbContext db)
    {
        var user = new ApplicationUser
        {
            Id = Guid.CreateVersion7(),
            Email = "john@seeded.example",
            UserName = "john@seeded.example",
            AzureTag = "john_seeded",
            FirstName = "John",
            LastName = "Seeded",
        };
        var account = new Account
        {
            UserId = user.Id,
            User = user,
            AccountNumber = "AB-1234-5678-90",
            Name = "Savings",
            Type = AccountType.Savings,
            Balance = 7450.00m,
            IsPrimary = true,
        };

        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        return account.Id;
    }

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
