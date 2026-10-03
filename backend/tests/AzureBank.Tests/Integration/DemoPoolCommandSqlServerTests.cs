extern alias seeder;

using System.Data.Common;
using System.Security.Cryptography;
using AzureBank.Shared.Entities;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using RecycleCommand = seeder::AzureBank.Seeder.Commands.RecycleCommand;
using ResetCommand = seeder::AzureBank.Seeder.Commands.ResetCommand;
using SeedCommand = seeder::AzureBank.Seeder.Commands.SeedCommand;
using SeedPoolCommand = seeder::AzureBank.Seeder.Commands.SeedPoolCommand;

namespace AzureBank.Tests.Integration;

/// <summary>
/// <c>seed-pool</c> and <c>recycle</c> as the tool runs them, on a real SQL Server: the builder and
/// the recycler behind the command's own refusals, ending in the run's exit code and its one line.
/// And <c>seed</c>, which refuses a database that holds the pool.
/// </summary>
/// <remarks>
/// <para>
/// What the builder and the recycler do is <c>DemoPoolSeedSqlServerTests</c>' and
/// <c>DemoPoolRecycleSqlServerTests</c>'. Here: what the command adds to them, which is the code it
/// returns, the line it logs, the token Identity is handed, and the rights a run needs.
/// </para>
/// <para>
/// Each test runs the command on the Seeder's own container (<see cref="DemoPoolDatabase"/>), on a
/// database of its own.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class DemoPoolCommandSqlServerTests
{
    private static Task<int> Run(string command, IServiceProvider provider, CancellationToken token = default) =>
        command switch
        {
            "seed-pool" => SeedPoolCommand.RunAsync(provider, copies: null, token),
            "recycle" => RecycleCommand.RunAsync(provider, token),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, "not a pool command"),
        };

    private static List<(LogLevel Level, string Message)> PoolLines(RecordingLoggerProvider log) =>
        [.. log.Lines.Where(line => line.Message.StartsWith("pool: ", StringComparison.Ordinal))];

    private static string Output(RecordingLoggerProvider log) =>
        "the run logged:" + Environment.NewLine
        + string.Join(Environment.NewLine, log.Lines.Select(line => $"[{line.Level}] {line.Message}")).Replace("{", "{{").Replace("}", "}}");

    [SqlServerFact]
    public async Task SeedPool_BuildsTheCopiesItIsAskedFor_LogsOneLine_AndExitsZero()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var log = new RecordingLoggerProvider();
        database.Log = log;

        var exitCode = await SeedPoolCommand.RunAsync(database.Seeder(), copies: 3, CancellationToken.None);

        using var all = new AssertionScope();
        exitCode.Should().Be(0, Output(log));
        (await database.CopiesAsync()).Should().HaveCount(3, "the number on the command line wins over the configured target of 2");
        PoolLines(log).Should().ContainSingle().Which.Should().Be((
            LogLevel.Information,
            "pool: free=3 was=0 claimed=0 claims24h=0 clientsAtCap=0 seeded=3 "
            + "deleted(expired=0 hardStop=0 staleFree=0 failed=0) swept(idempotency=0 grants=0) "
            + "tombstones=0 foreignUsers=0 ceiling=no result=PoolOk"));
    }

    [SqlServerTheory]
    [InlineData("seed-pool")]
    [InlineData("recycle")]
    public async Task OnADatabaseWithUsersAndNoPool_ThePoolCommandsExitThirteen_WriteNothing_AndLogTheirLineAsAWarning(string command)
    {
        // The signature of the wrong database. The run writes nothing; its line says why, and at
        // Warning, since a code other than 0 is something to read.
        await using var database = await DemoPoolDatabase.CreateAsync();
        await AddAUserOutsideEveryCopyAsync(database);
        var log = new RecordingLoggerProvider();
        database.Log = log;

        var exitCode = await Run(command, database.Seeder());

        using var all = new AssertionScope();
        exitCode.Should().Be(13, Output(log));
        PoolLines(log).Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
        PoolLines(log).Should().ContainSingle().Which.Message.Should()
            .Contain(" seeded=0 ").And.Contain(" foreignUsers=1 ").And.EndWith(" result=ForeignUsers");
        (await database.CopiesAsync()).Should().BeEmpty();
        await using var db = database.NewContext();
        (await db.Users.CountAsync()).Should().Be(1, "the one user that was there, and no other");
    }

    [SqlServerFact]
    public async Task BesideAPool_AUserOutsideEveryCopy_DoesNotStopSeedPool_AndRecycleReportsIt()
    {
        // A registration made through the app under compose.demo.yaml leaves such a user. seed-pool
        // is the one-shot the API waits for, so an exit 13 there kept the API down until the volume
        // was removed. It fills the pool and names the user on its line; recycle, the job that runs
        // on a schedule, exits 13 for it, every run.
        await using var database = await DemoPoolDatabase.CreateAsync();
        await database.BuildCopiesAsync(1);
        await AddAUserOutsideEveryCopyAsync(database);
        var log = new RecordingLoggerProvider();
        database.Log = log;

        var seeded = await SeedPoolCommand.RunAsync(database.Seeder(), copies: 2, CancellationToken.None);

        using (new AssertionScope())
        {
            seeded.Should().Be(0, Output(log));
            PoolLines(log).Should().ContainSingle().Which.Should().Be((
                LogLevel.Information,
                "pool: free=2 was=1 claimed=0 claims24h=0 clientsAtCap=0 seeded=1 "
                + "deleted(expired=0 hardStop=0 staleFree=0 failed=0) swept(idempotency=0 grants=0) "
                + "tombstones=0 foreignUsers=1 ceiling=no result=PoolOk"));
        }

        // TWIN: the same database, the scheduled command.
        var recycled = await RecycleCommand.RunAsync(database.Seeder(), CancellationToken.None);

        using var all = new AssertionScope();
        recycled.Should().Be(13, Output(log));
        PoolLines(log).Last().Should().Be((
            LogLevel.Warning,
            "pool: free=2 was=2 claimed=0 claims24h=0 clientsAtCap=0 seeded=0 "
            + "deleted(expired=0 hardStop=0 staleFree=0 failed=0) swept(idempotency=0 grants=0) "
            + "tombstones=0 foreignUsers=1 ceiling=no result=ForeignUsers"));
        (await UsersAsync(database)).Should().Be(7, "the two copies' six users and the one outside them: nothing of it was deleted");
    }

    [SqlServerTheory]
    [InlineData("seed-pool")]
    [InlineData("recycle")]
    public async Task APoolRunStoppedWhileIdentityWorks_IsStoppedAtIdentitysOwnStatement(string command)
    {
        // Identity's managers take no token. The two the tool registers read the run's from the
        // scope's RunCancellation, which is "none" until whoever starts work in the scope sets it.
        // Unset, a stop that arrived while a pool run was inside Identity reached nothing: with the
        // server away, the run went on through EF's whole retry budget, as seed did before.
        await using var database = await DemoPoolDatabase.CreateAsync();
        var log = new RecordingLoggerProvider();
        database.Log = log;
        using var run = new CancellationTokenSource();
        var probe = new CancelAtIdentitysFirstStatement(run);

        var exitCode = await Run(command, database.Seeder(interceptors: probe), run.Token);

        using var all = new AssertionScope();
        probe.Seen.Should().Equal([true], "the statement in flight carried the run's token, and Identity sent no other after it");
        exitCode.Should().Be(1, Output(log));
        log.Lines.Should().Contain(line => line.Message.Contains($"{command} was cancelled"));
        PoolLines(log).Should().BeEmpty("a run that was stopped did not finish, and has no line");
        (await database.CopiesAsync()).Should().BeEmpty();
    }

    [SqlServerTheory]
    [InlineData("a free copy", 2)]
    [InlineData("only the record of a deleted copy", 2)]
    [InlineData("no pool row", 0)]
    public async Task Seed_OnADatabaseThatHoldsThePool_IsRefused_AndWritesNothing(string holding, int exitCode)
    {
        // seed's four users have a password and a PIN that are in the repository. In the demo's
        // database they would be users outside every copy, whom anyone can sign in as. The flag is
        // off here, as on a job that lost its environment: the rows are what say whose database it is.
        await using var database = await DemoPoolDatabase.CreateAsync();
        switch (holding)
        {
            case "a free copy":
                await database.BuildCopiesAsync(1);
                break;
            case "only the record of a deleted copy":
                await AddTheRecordOfADeletedCopyAsync(database);
                break;
        }

        var usersBefore = await UsersAsync(database);
        var log = new RecordingLoggerProvider();
        database.Log = log;

        var actual = await SeedCommand.RunAsync(database.Seeder(new() { ["Demo:Enabled"] = "false" }), CancellationToken.None);

        using var all = new AssertionScope();
        actual.Should().Be(exitCode, Output(log));
        var errors = log.Lines.Where(line => line.Level == LogLevel.Error).Select(line => line.Message).ToList();
        if (exitCode == 2)
        {
            errors.Should().ContainSingle()
                .Which.Should().Contain("seed refused: the database holds the demo pool's rows")
                .And.Contain("Nothing was written");
            (await UsersAsync(database)).Should().Be(usersBefore, "seed wrote no user");
        }
        else
        {
            // CONTROL: the same command, the same container, a database with no pool row: seeded.
            errors.Should().BeEmpty();
            (await UsersAsync(database)).Should().Be(4, "the fixed demo's four users");
        }
    }

    [SqlServerTheory]
    [InlineData("a free copy", 2)]
    [InlineData("only the record of a deleted copy", 2)]
    [InlineData("no pool row", 0)]
    [InlineData("no database at all", 0)]
    [InlineData("a schema from before the pool's table", 0)]
    public async Task Reset_OnADatabaseThatHoldsThePool_IsRefused_AndDropsNothing(string holding, int exitCode)
    {
        // reset drops the database. The flag is off here, as on a job that lost its environment:
        // what says the database is the demo's is its pool rows, and reset with the flag off would
        // drop the copies visitors hold and put back four users whose password is public. The last
        // three rows are CONTROLS: reset is the repair tool, so a database with no pool row, none at
        // all, or one migrated before the pool's table existed is reset as before, and none of them
        // costs a retry.
        await using var database = await DemoPoolDatabase.CreateAsync();
        switch (holding)
        {
            case "a free copy":
                await database.BuildCopiesAsync(1);
                break;
            case "only the record of a deleted copy":
                await AddTheRecordOfADeletedCopyAsync(database);
                break;
            case "no database at all":
                await using (var db = database.NewContext())
                {
                    SqlConnection.ClearAllPools();
                    (await db.Database.EnsureDeletedAsync()).Should().BeTrue("ARRANGE: the database is gone");
                }

                break;
            case "a schema from before the pool's table":
                await using (var db = database.NewContext())
                {
                    await db.GetService<IMigrator>().MigrateAsync("20260928192931_AddUserSessionStamp");
                    (await db.Database.GetAppliedMigrationsAsync()).Should().NotContain(
                        id => id.EndsWith("_AddDemoCopies", StringComparison.Ordinal), "ARRANGE: the pool's migration is taken back");
                }

                break;
        }

        var refused = exitCode == 2;
        var createdBefore = await CreatedAtAsync(database);
        var poolRowsBefore = refused ? (await database.CopiesAsync()).Count : 0;
        var usersBefore = refused ? await UsersAsync(database) : 0;
        var log = new RecordingLoggerProvider();
        database.Log = log;

        var actual = await ResetCommand.RunAsync(
            database.Seeder(new() { ["Demo:Enabled"] = "false" }), confirm: true, CancellationToken.None);

        using var all = new AssertionScope();
        actual.Should().Be(exitCode, Output(log));
        var errors = log.Lines.Where(line => line.Level == LogLevel.Error).Select(line => line.Message).ToList();
        log.Lines.Should().NotContain(
            line => line.Message.Contains("transient exception", StringComparison.OrdinalIgnoreCase),
            "a database that is missing is answered by one open, not by EF's retries");
        if (refused)
        {
            errors.Should().ContainSingle()
                .Which.Should().Contain("reset refused: the database holds the demo pool's rows")
                .And.Contain("Nothing was written");
            log.Lines.Should().NotContain(line => line.Message.Contains("Deleting database"), "reset refused before the drop");
            createdBefore.Should().NotBeNull("ARRANGE: the database exists");
            (await CreatedAtAsync(database)).Should().Be(createdBefore, "the database was not dropped and created again");
            (await database.CopiesAsync()).Should().HaveCount(poolRowsBefore, "the pool's rows are as they were");
            (await UsersAsync(database)).Should().Be(usersBefore, "and so are its users");
        }
        else
        {
            errors.Should().BeEmpty();
            log.Lines.Should().Contain(line => line.Message.Contains("Database reset and reseeded"));
            (await UsersAsync(database)).Should().Be(4, "the fixed demo's four users");
            (await database.CopiesAsync()).Should().BeEmpty();
        }
    }

    [SqlServerFact]
    public async Task ThePoolCommands_NeedNoRightButToReadAndWriteRows()
    {
        // A deployment gives its app a database user that reads and writes rows (db_datareader,
        // db_datawriter) and its migration one that may also change the schema (db_ddladmin). The
        // pool's commands are to run as the first. This is such a user, on a login of the test's
        // own, through a whole cycle: the roles and copies of a first fill, then a run that tops up,
        // deletes a claimed copy whose time is over and a free copy too old to hand out, and sweeps.
        await using var database = await DemoPoolDatabase.CreateAsync();
        await using var rowsOnly = await RowsOnlyLogin.CreateAsync(database);
        var logins = new LoginRecordingInterceptor();
        var asTheApp = new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = rowsOnly.ConnectionString };
        var log = new RecordingLoggerProvider();
        database.Log = log;

        // CONTROL: the login is what it says it is. A user with db_ddladmin would create the table.
        var refused = await rowsOnly.CreateATableAsync();
        refused.Should().Be(262, "CREATE TABLE permission denied: the login cannot change the schema");

        var seeded = await SeedPoolCommand.RunAsync(database.Seeder(asTheApp, logins), copies: 3, CancellationToken.None);
        seeded.Should().Be(0, Output(log));

        var copies = await database.CopiesAsync();
        copies.Should().HaveCount(3, "ARRANGE: the first fill");
        await database.ClaimForAVisitorAsync(copies[0], DateTime.UtcNow.AddHours(-30));
        await database.BackdateSeedAsync(copies[1].Id, DateTime.UtcNow.AddHours(-50));

        var recycled = await RecycleCommand.RunAsync(database.Seeder(asTheApp, logins), CancellationToken.None);

        using var all = new AssertionScope();
        recycled.Should().Be(0, Output(log));
        PoolLines(log).Last().Message.Should()
            .Contain("pool: free=2 was=2 ").And.Contain(" seeded=1 ")
            .And.Contain("deleted(expired=1 hardStop=0 staleFree=1 failed=0)");
        logins.Users.Should().NotBeEmpty().And.OnlyContain(user => user == rowsOnly.Name, "every connection the two runs opened was the login's");
    }

    private static async Task<int> UsersAsync(DemoPoolDatabase database)
    {
        await using var db = database.NewContext();
        return await db.Users.CountAsync();
    }

    /// <summary>When the server created the test's database, or null when it holds none by that name.</summary>
    private static async Task<DateTime?> CreatedAtAsync(DemoPoolDatabase database)
    {
        var name = new SqlConnectionStringBuilder(database.ConnectionString).InitialCatalog;
        await using var master = new SqlConnection(
            new SqlConnectionStringBuilder(database.ConnectionString) { InitialCatalog = "master" }.ConnectionString);
        await master.OpenAsync();
        await using var command = master.CreateCommand();
        command.CommandText = "SELECT create_date FROM sys.databases WHERE name = @name;";
        command.Parameters.AddWithValue("@name", name);
        return await command.ExecuteScalarAsync() as DateTime?;
    }

    /// <summary>One user with no copy, as registration would leave it.</summary>
    private static async Task AddAUserOutsideEveryCopyAsync(DemoPoolDatabase database)
    {
        using var scope = database.Seeder().CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var id = Guid.CreateVersion7();
        var created = await users.CreateAsync(new ApplicationUser
        {
            Id = id,
            UserName = id.ToString(),
            Email = "resident@example.test",
            AzureTag = "resident",
            FirstName = "Outside",
            LastName = "Anycopy",
        });
        created.Succeeded.Should().BeTrue("ARRANGE: a user outside every copy ({0})", string.Join("; ", created.Errors.Select(e => e.Code)));
    }

    /// <summary>The pool row a deleted claimed copy leaves, and nothing else.</summary>
    private static async Task AddTheRecordOfADeletedCopyAsync(DemoPoolDatabase database)
    {
        await using var db = database.NewContext();
        var claimedAt = DateTime.UtcNow.AddDays(-3);
        db.Set<DemoCopy>().Add(new DemoCopy
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = Guid.CreateVersion7(),
            CreatedAt = claimedAt.AddHours(-1),
            ClaimedAt = claimedAt,
            ClaimId = Guid.CreateVersion7(),
            DeletedAt = claimedAt.AddDays(1),
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Cancels the run as the first statement to Identity's roles table is about to run, and records
    /// for each such statement whether the token it was sent with saw the cancellation.
    /// </summary>
    private sealed class CancelAtIdentitysFirstStatement(CancellationTokenSource run) : DbCommandInterceptor
    {
        private readonly List<bool> _seen = [];

        public IReadOnlyList<bool> Seen
        {
            get
            {
                lock (_seen)
                {
                    return _seen.ToArray();
                }
            }
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await SeeAsync(command, cancellationToken);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await SeeAsync(command, cancellationToken);
            return result;
        }

        private async Task SeeAsync(DbCommand command, CancellationToken cancellationToken)
        {
            if (!command.CommandText.Contains("[AspNetRoles]", StringComparison.Ordinal))
            {
                return;
            }

            await run.CancelAsync();
            lock (_seen)
            {
                _seen.Add(cancellationToken.IsCancellationRequested);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>Records the login of every connection EF opens.</summary>
    private sealed class LoginRecordingInterceptor : DbConnectionInterceptor
    {
        private readonly List<string> _users = [];

        public IReadOnlyList<string> Users
        {
            get
            {
                lock (_users)
                {
                    return _users.ToArray();
                }
            }
        }

        public override Task ConnectionOpenedAsync(
            DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            lock (_users)
            {
                _users.Add(new SqlConnectionStringBuilder(connection.ConnectionString).UserID);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A SQL login of the test's own, with a user in the test's database that holds db_datareader and
    /// db_datawriter and nothing else. Its password is drawn here and lives only in this object; the
    /// login is dropped when the test ends.
    /// </summary>
    private sealed class RowsOnlyLogin : IAsyncDisposable
    {
        private readonly string _admin;

        private RowsOnlyLogin(string admin, string name, string connectionString)
        {
            _admin = admin;
            Name = name;
            ConnectionString = connectionString;
        }

        public string Name { get; }

        public string ConnectionString { get; }

        public static async Task<RowsOnlyLogin> CreateAsync(DemoPoolDatabase database)
        {
            var name = $"pool_rows_{Guid.NewGuid():N}";
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) + "a!";
            var admin = database.ConnectionString;

            await using (var master = new SqlConnection(new SqlConnectionStringBuilder(admin) { InitialCatalog = "master" }.ConnectionString))
            {
                await master.OpenAsync();
                await using var create = master.CreateCommand();
                create.CommandText = $"CREATE LOGIN [{name}] WITH PASSWORD = N'{password}', CHECK_POLICY = OFF;";
                await create.ExecuteNonQueryAsync();
            }

            await using (var db = new SqlConnection(admin))
            {
                await db.OpenAsync();
                await using var grant = db.CreateCommand();
                grant.CommandText =
                    $"CREATE USER [{name}] FOR LOGIN [{name}]; "
                    + $"ALTER ROLE [db_datareader] ADD MEMBER [{name}]; "
                    + $"ALTER ROLE [db_datawriter] ADD MEMBER [{name}];";
                await grant.ExecuteNonQueryAsync();
            }

            var connectionString = new SqlConnectionStringBuilder(admin)
            {
                IntegratedSecurity = false,
                UserID = name,
                Password = password,
            }.ConnectionString;
            return new RowsOnlyLogin(admin, name, connectionString);
        }

        /// <summary>Tries to create a table as this login; SQL Server's error number, or 0 if it was created.</summary>
        public async Task<int> CreateATableAsync()
        {
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE [dbo].[PoolRightsProbe] ([Id] int NOT NULL);";
            try
            {
                await command.ExecuteNonQueryAsync();
                return 0;
            }
            catch (SqlException e)
            {
                return e.Number;
            }
        }

        public async ValueTask DisposeAsync()
        {
            // A login with a session open cannot be dropped: the pooled ones go first.
            SqlConnection.ClearAllPools();
            await using var master = new SqlConnection(new SqlConnectionStringBuilder(_admin) { InitialCatalog = "master" }.ConnectionString);
            await master.OpenAsync();
            await using var drop = master.CreateCommand();
            drop.CommandText = $"IF SUSER_ID(N'{Name}') IS NOT NULL DROP LOGIN [{Name}];";
            await drop.ExecuteNonQueryAsync();
        }
    }
}
