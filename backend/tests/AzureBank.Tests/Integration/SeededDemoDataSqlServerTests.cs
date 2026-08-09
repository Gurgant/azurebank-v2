extern alias seeder;

using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
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
        var oldestAge = (now - rows[0].CreatedAt).TotalDays;
        oldestAge.Should().BeInRange(2.5, 3.5,
            "the oldest demo row is the salary deposit, three days back");
        (rows[^1].CreatedAt - rows[0].CreatedAt).TotalDays.Should().BeInRange(2.5, 3.5,
            "the ledger spans the three days between the salary deposit and the refund");
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

    private AzureBankDbContext NewContext()
    {
        _database = $"AzureBankSeedProof_{Guid.NewGuid():N}";
        var cs = new SqlConnectionStringBuilder(SqlServerFactAttribute.ConnectionString!)
        {
            InitialCatalog = _database,
        }.ConnectionString;
        _connectionString = cs;

        return new AzureBankDbContext(
            new DbContextOptionsBuilder<AzureBankDbContext>().UseSqlServer(cs).Options);
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
