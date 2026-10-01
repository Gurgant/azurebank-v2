extern alias seeder;

using AzureBank.Infrastructure.Data;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SeedCommand = seeder::AzureBank.Seeder.Commands.SeedCommand;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The Seeder's <c>seed</c> command on a real SQL Server, through the tool's own composition root
/// and its committed settings: it exits 1 when the demo data is not all there afterwards, fills an
/// empty database with the four demo users and their 26-row ledger, changes nothing the second
/// time, and still exits 0 on a seeded database its users have since changed.
/// </summary>
/// <remarks>
/// <para>
/// THE FIRST ACT IS THE ONE THAT USED TO PASS. A user that fails to be created is logged and
/// skipped by <c>UserSeeder</c>; <c>AccountSeeder</c> then finds no users and skips, and
/// <c>TransactionSeeder</c> finds no accounts and skips. So <c>seed</c> printed "Database seeded
/// successfully!" and exited 0 with nothing seeded. A password the identity rules refuse makes
/// that happen here; a container that then started the API on it would serve a demo nobody can
/// sign in to.
/// </para>
/// <para>
/// THE FOURTH ACT IS WHAT COMPOSE DOES ON EVERY <c>up</c>: <c>seed</c> runs again, on a database
/// people have used. A demo user may rename their handle and close an account with nothing in it
/// (the row stays, marked deleted). The final check used to look the four users up by handle, so
/// one rename ended every later run with "3 of 4 demo users" and exit 1, and compose then never
/// started the API (measured 2026-10-01 on the compose stack). It goes by the five demo account
/// numbers now, which nothing in the app changes.
/// </para>
/// <para>
/// One migrated database of its own for each test (<c>AzureBankOneShot_…</c>, dropped afterwards):
/// the seeders skip a database that already holds rows, so the shared proofs database would make
/// every act pass on nothing.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class SeedCommandSqlServerTests : IDisposable
{
    private readonly string _connectionString = new SqlConnectionStringBuilder(SqlServerFactAttribute.ConnectionString ?? "Server=unset")
    {
        InitialCatalog = $"AzureBankOneShot_{Guid.NewGuid():N}",
    }.ConnectionString;

    public void Dispose()
    {
        if (string.IsNullOrWhiteSpace(SqlServerFactAttribute.ConnectionString))
        {
            return;
        }

        using var db = Context();
        db.Database.EnsureDeleted();
    }

    [SqlServerFact]
    public async Task Seed_FailsWhenTheDemoDataIsIncomplete_FillsAnEmptyDatabase_ThenLeavesASeededOneAlone()
    {
        await using (var db = Context())
        {
            await db.Database.MigrateAsync();
        }

        // Act 1: a password the identity rules refuse, so no user is created.
        var first = new RecordingLoggerProvider();
        await using (var provider = Provider(first, ("SeedData:DefaultPassword", "weak")))
        {
            (await SeedCommand.RunAsync(provider, CancellationToken.None)).Should().Be(1, Output(first));
        }

        using (new AssertionScope())
        {
            first.Lines.Where(line => line.Level == LogLevel.Error).Select(line => line.Message)
                .Should().Contain(message => message.Contains("The demo data is incomplete: 0 of 5 demo accounts, 0 ledger rows"));
            (await CountsAsync()).Should().Be(new Counts(Users: 0, Accounts: 0, Transactions: 0, OnJohnsAccounts: 0));
        }

        // Act 2: the committed settings, on a database that still has no user.
        var second = new RecordingLoggerProvider();
        await using (var provider = Provider(second))
        {
            (await SeedCommand.RunAsync(provider, CancellationToken.None)).Should().Be(0, Output(second));
        }

        var seeded = new Counts(Users: 4, Accounts: 5, Transactions: 26, OnJohnsAccounts: 22);
        (await CountsAsync()).Should().Be(seeded);

        // Act 3: again.
        var third = new RecordingLoggerProvider();
        await using (var provider = Provider(third))
        {
            (await SeedCommand.RunAsync(provider, CancellationToken.None)).Should().Be(0, Output(third));
        }

        (await CountsAsync()).Should().Be(seeded, "seed fills an empty database only");

        // Act 4: the demo was used. John renamed his handle and closed his second account, as the
        // API writes both: one column, and a soft delete.
        await using (var db = Context())
        {
            (await db.Users.Where(u => u.AzureTag == "johnsmith")
                .ExecuteUpdateAsync(set => set.SetProperty(u => u.AzureTag, "johnrenamed"))).Should().Be(1);
            (await db.Accounts.Where(a => a.AccountNumber == "AB-1234-5678-91")
                .ExecuteUpdateAsync(set => set
                    .SetProperty(a => a.IsDeleted, true)
                    .SetProperty(a => a.DeletedAt, DateTime.UtcNow))).Should().Be(1);
        }

        var fourth = new RecordingLoggerProvider();
        await using (var provider = Provider(fourth))
        {
            (await SeedCommand.RunAsync(provider, CancellationToken.None)).Should().Be(0, Output(fourth));
        }

        using (new AssertionScope())
        {
            fourth.Lines.Should().NotContain(line => line.Level == LogLevel.Error, Output(fourth));
            await using var db = Context();
            (await db.Users.CountAsync()).Should().Be(4);
            (await db.Accounts.IgnoreQueryFilters().CountAsync()).Should().Be(5);
            (await db.Transactions.CountAsync()).Should().Be(26);
        }
    }

    [SqlServerFact]
    public async Task Seed_FailsWhenOneDemoUserIsMissing_EvenWithALedger()
    {
        // The admin is the last user created and no ledger row hangs off its account, so a seed
        // that loses only the admin still writes all 26 rows. "Some ledger rows" alone would call
        // that complete; the fifth account is what says it is not.
        await using (var db = Context())
        {
            await db.Database.MigrateAsync();
        }

        var log = new RecordingLoggerProvider();
        await using (var provider = Provider(log, ("SeedData:AdminEmail", "not-an-address")))
        {
            (await SeedCommand.RunAsync(provider, CancellationToken.None)).Should().Be(1, Output(log));
        }

        using var all = new AssertionScope();
        log.Lines.Where(line => line.Level == LogLevel.Error).Select(line => line.Message)
            .Should().Contain(message => message.Contains("The demo data is incomplete: 4 of 5 demo accounts, 26 ledger rows"));
        (await CountsAsync()).Should().Be(new Counts(Users: 3, Accounts: 4, Transactions: 26, OnJohnsAccounts: 22));
    }

    private sealed record Counts(int Users, int Accounts, int Transactions, int OnJohnsAccounts);

    private async Task<Counts> CountsAsync()
    {
        await using var db = Context();
        return new Counts(
            await db.Users.CountAsync(),
            await db.Accounts.CountAsync(),
            await db.Transactions.CountAsync(),
            await db.Transactions.CountAsync(t => t.Account.User.AzureTag == "johnsmith"));
    }

    private ServiceProvider Provider(RecordingLoggerProvider log, params (string Key, string? Value)[] settings) =>
        SeederHost.Build(
            log,
            interceptor: null,
            onCommittedSettings: true,
            [("ConnectionStrings:DefaultConnection", _connectionString), ("Security:PinPepper", SeederHost.Pepper), .. settings]);

    private AzureBankDbContext Context() =>
        new(new DbContextOptionsBuilder<AzureBankDbContext>().UseSqlServer(_connectionString).Options);

    private static string Output(RecordingLoggerProvider log) =>
        "the run logged:" + Environment.NewLine
        + string.Join(Environment.NewLine, log.Lines.Select(line => $"[{line.Level}] {line.Message}")).Replace("{", "{{").Replace("}", "}}");
}
