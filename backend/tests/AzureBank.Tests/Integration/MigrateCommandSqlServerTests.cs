extern alias seeder;

using AzureBank.Infrastructure.Data;
using AzureBank.Infrastructure.Extensions;
using AzureBank.Shared.Options;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MigrateCommand = seeder::AzureBank.Seeder.Commands.MigrateCommand;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The Seeder's <c>migrate</c> command on a real SQL Server, through the tool's own composition
/// root and its committed settings: it creates the database a local server does not hold, applies
/// every migration, changes nothing the second time, and refuses a database that holds a migration
/// this build does not know.
/// </summary>
/// <remarks>
/// <para>
/// One database of its own for the three acts (<c>AzureBankOneShot_…</c> on the configured server,
/// dropped afterwards), because the claims are about a server that starts without it. The real
/// wait runs first each time: on a server without the database it reads <c>master</c>, finds no
/// row and lets EF create it.
/// </para>
/// <para>
/// THE THIRD ACT IS THE ONE EF ALONE GETS WRONG. With one row added to the history table,
/// <c>MigrateAsync</c> reports "already up to date" and the run exits 0 (measured 2026-10-01: 17
/// applied, 16 known). A deployment of an older build would then move the app onto a schema it
/// does not know.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class MigrateCommandSqlServerTests : IDisposable
{
    private const string FromANewerBuild = "99990101000000_FromANewerBuild";

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

        using var db = new AzureBankDbContext(
            new DbContextOptionsBuilder<AzureBankDbContext>().UseSqlServer(_connectionString).Options);
        db.Database.EnsureDeleted();
    }

    [SqlServerFact]
    public async Task Migrate_CreatesAndMigrates_ThenChangesNothing_ThenRefusesADatabaseAheadOfTheBuild()
    {
        // The limits the tool opens with: its committed settings set the pool to 5, and a keyword
        // the test's own string sets wins, as it would anywhere.
        var expected = new SqlConnectionStringBuilder(
            SqlConnectionDefaults.Apply(_connectionString, new DatabaseOptions { MaxPoolSize = 5 }));

        // Act 1: a server without the database.
        var first = new RecordingLoggerProvider();
        string[] known;
        await using (var provider = Provider(first))
        {
            (await MigrateCommand.RunAsync(provider, TimeSpan.FromSeconds(30), CancellationToken.None))
                .Should().Be(0, Output(first));

            using var scope = provider.CreateScope();
            known = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>().Database.GetMigrations().ToArray();
        }

        known.Should().NotBeEmpty();
        var messages = Messages(first);
        using (new AssertionScope())
        {
            (await HistoryAsync()).Should().Equal(known, "every migration the build knows is in the history table");
            messages.Should().Contain(
                $"Database limits: connect timeout {expected.ConnectTimeout} s, connect retries {expected.ConnectRetryCount}, "
                + $"pool {expected.MaxPoolSize}, pool blocking {expected.PoolBlockingPeriod}; "
                + "EF retries 4, back-off capped at 00:00:10");
            messages.Should().Contain($"Pending migrations: {known.Length}, from {known[0]} to {known[^1]}");
            messages.Should().Contain(m => m.StartsWith(
                $"The database is at {known[^1]}: {known.Length} of {known.Length} migrations. migrate took ", StringComparison.Ordinal));
            messages.Should().Contain(m => m.StartsWith("Applying migration '", StringComparison.Ordinal))
                .And.NotContain(m => m.StartsWith("Waiting for the database", StringComparison.Ordinal),
                    "a server that answers and does not hold the database is not waited for");
        }

        // Act 2: again, on the migrated database.
        var second = new RecordingLoggerProvider();
        await using (var provider = Provider(second))
        {
            (await MigrateCommand.RunAsync(provider, TimeSpan.FromSeconds(30), CancellationToken.None))
                .Should().Be(0, Output(second));
        }

        using (new AssertionScope())
        {
            Messages(second).Should().Contain("Pending migrations: 0");
            (await HistoryAsync()).Should().Equal(known);
        }

        // Act 3: a migration this build does not know, as a newer build would have left.
        await ExecuteAsync(
            $"INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion]) VALUES ('{FromANewerBuild}', '10.0.1')");
        var third = new RecordingLoggerProvider();
        await using (var provider = Provider(third))
        {
            (await MigrateCommand.RunAsync(provider, TimeSpan.FromSeconds(30), CancellationToken.None))
                .Should().Be(1, Output(third));
        }

        using (new AssertionScope())
        {
            third.Lines.Where(line => line.Level == LogLevel.Error).Select(line => line.Message)
                .Should().ContainSingle()
                .Which.Should().Contain("holds 1 migration(s) this build does not know")
                .And.Contain(FromANewerBuild)
                .And.Contain("Nothing was changed");
            (await HistoryAsync()).Should().Equal(known.Append(FromANewerBuild), "the refusal changes nothing");
        }
    }

    private ServiceProvider Provider(RecordingLoggerProvider log) =>
        SeederHost.Build(
            log,
            interceptor: null,
            onCommittedSettings: true,
            ("ConnectionStrings:DefaultConnection", _connectionString));

    private static List<string> Messages(RecordingLoggerProvider log) =>
        log.Lines.Select(line => line.Message).ToList();

    private static string Output(RecordingLoggerProvider log) =>
        "the run logged:" + Environment.NewLine
        + string.Join(Environment.NewLine, log.Lines.Select(line => $"[{line.Level}] {line.Message}")).Replace("{", "{{").Replace("}", "}}");

    private async Task<List<string>> HistoryAsync()
    {
        var rows = new List<string>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT [MigrationId] FROM [__EFMigrationsHistory] ORDER BY [MigrationId]";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
