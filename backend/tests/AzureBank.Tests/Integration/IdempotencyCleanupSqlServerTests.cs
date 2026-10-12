using System.Data.Common;
using AzureBank.Api.Services;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AzureBank.Tests.Integration;

/// <summary>
/// What <see cref="IdempotencyCleanupService"/> deletes and what it keeps (ADR-0009) on real SQL
/// Server: the set-based delete, which is the branch every deployment runs. The load-and-remove
/// branch of the in-memory provider is <c>IdempotencyCleanupServiceTests</c>.
/// </summary>
/// <remarks>
/// <para>
/// A SCRATCH DATABASE FOR EACH TEST, migrated and then dropped, not the shared proofs one: the
/// sweep deletes every expired record of the table and logs how many, so a row another test left
/// behind would change what these read.
/// </para>
/// <para>
/// THE INSTANT OF A SWEEP IS READ OFF THE WIRE. The service takes "now" from the machine's clock,
/// so a test cannot choose it; a command interceptor sees it as the parameter of the sweep's first
/// statement, before that statement runs. That is where the boundary test writes a record that
/// expires at exactly that instant, on a context that carries no interceptor.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class IdempotencyCleanupSqlServerTests
{
    [SqlServerFact]
    public async Task ExpiredRecordsGo_LiveOnesStay_AndOnlyAnExpiredExecutedOneIsReported()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await using var rig = database.Rig();

        await IdempotencyCleanupProofs.ExpiredRecordsGo_LiveOnesStay_AndOnlyAnExpiredExecutedOneIsReported(rig);
    }

    [SqlServerFact]
    public async Task ADeleteThatFails_ComesAfterTheWarning_IsLoggedAsAnError_AndTheNextSweepRemoves()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        var refusing = FailingCommandInterceptor.OnTurnsOfText(turn => turn == 1, "DELETE", "[IdempotencyRecords]");
        await using var rig = database.Rig(refusing);

        await IdempotencyCleanupProofs.ADeleteThatFails_ComesAfterTheWarning_IsLoggedAsAnError_AndTheNextSweepRemoves(
            rig, () => refusing.Failures, FailingCommandInterceptor.Message);
    }

    [SqlServerFact]
    public async Task ARecordThatExpiresAtTheInstantOfTheSweep_IsReportedAndRemoved_AndOneTickLaterIsKept()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        // Both Executed, so the same two rows pin the comparison of the warning's query and of the
        // delete. One tick is the resolution of the column (datetime2(7)) and of the clock.
        IdempotencyRecord? atTheInstant = null;
        IdempotencyRecord? oneTickLater = null;
        var boundary = new BeforeTheFirst("SELECT", async command =>
        {
            var now = (DateTime)command.Parameters.Cast<DbParameter>().Single(p => p.Value is DateTime).Value!;
            atTheInstant = IdempotencyCleanupRig.Record(IdempotencyStatus.Executed, now);
            oneTickLater = IdempotencyCleanupRig.Record(IdempotencyStatus.Executed, now.AddTicks(1));
            await using var db = database.NewContext();
            db.IdempotencyRecords.AddRange(atTheInstant, oneTickLater);
            await db.SaveChangesAsync();
        });
        await using var rig = database.Rig(boundary);
        // The next sweep, some milliseconds on, would rightly take the second record too.
        rig.StopAfter(sweeps: 1);

        await rig.StartAsync();
        var loop = await rig.EndedAsync();

        loop.Status.Should().Be(TaskStatus.RanToCompletion);
        boundary.Fired.Should().BeTrue("the proof needs the two records written inside the sweep");
        (await rig.RecordsAsync()).Should().ContainSingle(
            "expiry is inclusive: the window is over at ExpiresAt, and not one tick before it")
            .Which.Should().BeEquivalentTo(oneTickLater);
        rig.Lines(LogLevel.Warning).Should().ContainSingle()
            .Which.Should().StartWith(IdempotencyCleanupRig.WarningFor(atTheInstant!));
        rig.Lines(LogLevel.Information).Should().Equal("Idempotency cleanup removed 1 expired records");
        rig.Lines(LogLevel.Error).Should().BeEmpty();
    }

    [SqlServerFact]
    public async Task ARecordClaimedAgainJustBeforeTheDelete_KeepsItsNewClaim_AndTheRestOfTheBatchGoes()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        // The takeover of ADR-0009 lands between the sweep's read and its delete. The set-based
        // delete carries no ClaimId; its predicate on the expiry is what must spare the new claim.
        var raced = IdempotencyCleanupRig.Record(IdempotencyStatus.Processing, DateTime.UtcNow.AddMinutes(-1));
        var expired = IdempotencyCleanupRig.Record(IdempotencyStatus.Completed, DateTime.UtcNow.AddMinutes(-1));
        var newClaim = Guid.NewGuid();
        var takeover = new BeforeTheFirst("DELETE", async _ =>
        {
            await using var db = database.NewContext();
            var row = await db.IdempotencyRecords.SingleAsync(r => r.Key == raced.Key);
            row.ClaimId = newClaim;
            row.CreatedAt = DateTime.UtcNow;
            row.ExpiresAt = DateTime.UtcNow.AddHours(24);
            await db.SaveChangesAsync();
        });
        await using var rig = database.Rig(takeover);
        await rig.SeedAsync(raced, expired);
        rig.StopAfter(sweeps: 1);

        await rig.StartAsync();
        var loop = await rig.EndedAsync();

        loop.Status.Should().Be(TaskStatus.RanToCompletion);
        takeover.Fired.Should().BeTrue("the proof needs the claim to land inside the sweep");
        (await rig.RecordsAsync()).Should().ContainSingle("the new claim is inside its window")
            .Which.ClaimId.Should().Be(newClaim);
        rig.Lines(LogLevel.Information).Should().Equal(
            ["Idempotency cleanup removed 1 expired records"],
            "one statement removed the other record: a raced row does not fail the batch");
        rig.Log.Lines.Should().NotContain(line => line.Level >= LogLevel.Warning || line.Level == LogLevel.Debug);
    }

    /// <summary>
    /// Runs once, just before the first statement on <c>[IdempotencyRecords]</c> whose text holds
    /// <paramref name="verb"/> reaches the server.
    /// </summary>
    private sealed class BeforeTheFirst(string verb, Func<DbCommand, Task> act) : DbCommandInterceptor
    {
        private int _fired;

        public bool Fired => Volatile.Read(ref _fired) == 1;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await ActIfChosenAsync(command);
            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await ActIfChosenAsync(command);
            return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private async Task ActIfChosenAsync(DbCommand command)
        {
            if (command.CommandText.Contains(verb, StringComparison.Ordinal)
                && command.CommandText.Contains("[IdempotencyRecords]", StringComparison.Ordinal)
                && Interlocked.CompareExchange(ref _fired, 1, 0) == 0)
            {
                await act(command);
            }
        }
    }

    private sealed class ScratchDatabase : IAsyncDisposable
    {
        private readonly string _connectionString;

        private ScratchDatabase(string connectionString) => _connectionString = connectionString;

        public static async Task<ScratchDatabase> CreateAsync()
        {
            var database = new ScratchDatabase(
                new SqlConnectionStringBuilder(SqlServerFactAttribute.ConnectionString!)
                {
                    InitialCatalog = $"AzureBankTests_Cleanup_{Guid.NewGuid():N}",
                    // No pooled connection may outlive the test: it would hold the database open
                    // against the drop.
                    Pooling = false,
                }.ConnectionString);
            await using var db = database.NewContext();
            await db.Database.MigrateAsync();
            return database;
        }

        /// <summary>A plain context on this database: no interceptor, nothing tracked across uses.</summary>
        public AzureBankDbContext NewContext() =>
            new(new DbContextOptionsBuilder<AzureBankDbContext>().UseSqlServer(_connectionString).Options);

        public IdempotencyCleanupRig Rig(params IInterceptor[] interceptors) =>
            new(options => options.UseSqlServer(_connectionString).AddInterceptors(interceptors));

        public async ValueTask DisposeAsync()
        {
            await using var db = NewContext();
            await db.Database.EnsureDeletedAsync();
        }
    }
}
