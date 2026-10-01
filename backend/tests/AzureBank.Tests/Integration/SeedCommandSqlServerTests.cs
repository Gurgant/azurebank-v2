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
/// empty database with the four demo users and their 26-row ledger, and changes nothing the second
/// time.
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
/// One migrated database of its own (<c>AzureBankOneShot_…</c>, dropped afterwards): the seeders
/// skip a database that already holds rows, so the shared proofs database would make every act
/// pass on nothing.
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
    public async Task Seed_FailsWhenTheDemoDataIsIncomplete_ThenFillsAnEmptyDatabase_ThenChangesNothing()
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
                .Should().Contain(message => message.Contains("The demo data is incomplete: 0 of 4 demo users, 0 ledger rows"));
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
