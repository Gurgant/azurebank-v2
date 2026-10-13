using System.Diagnostics;
using AzureBank.Api.Services;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Enums;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AzureBank.Tests.Unit.Services;

/// <summary>
/// What <see cref="IdempotencyCleanupService"/> deletes and what it keeps (ADR-0009), on the EF
/// in-memory provider: the load-and-remove branch every in-memory test host runs. The set-based
/// branch of a relational database is <c>IdempotencyCleanupSqlServerTests</c>.
/// </summary>
/// <remarks>
/// A key refuses a second payment for 24 hours, so the sweep may only take a record whose window
/// is over. The record that expires at the very instant of the sweep is pinned on SQL Server only:
/// the service reads the machine's clock, and on this provider nothing shows a test which instant
/// a sweep read.
/// </remarks>
public sealed class IdempotencyCleanupServiceTests
{
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly InMemoryDatabaseRoot _root = new();

    private IdempotencyCleanupRig Rig(params IInterceptor[] interceptors) =>
        new(options => options.UseInMemoryDatabase(_database, _root).AddInterceptors(interceptors));

    /// <summary>A context on the same store that carries none of the rig's interceptors.</summary>
    private AzureBankDbContext Outside() =>
        new(new DbContextOptionsBuilder<AzureBankDbContext>().UseInMemoryDatabase(_database, _root).Options);

    [Fact]
    public async Task ExpiredRecordsGo_LiveOnesStay_AndOnlyAnExpiredExecutedOneIsReported()
    {
        await using var rig = Rig();

        await IdempotencyCleanupProofs.ExpiredRecordsGo_LiveOnesStay_AndOnlyAnExpiredExecutedOneIsReported(rig);
    }

    [Fact]
    public async Task ADeleteThatFails_ComesAfterTheWarning_IsLoggedAsAnError_AndTheNextSweepRemoves()
    {
        var refusing = new RefusesTheFirstDelete();
        await using var rig = Rig(refusing);

        await IdempotencyCleanupProofs.ADeleteThatFails_ComesAfterTheWarning_IsLoggedAsAnError_AndTheNextSweepRemoves(
            rig, () => refusing.Refusals, RefusesTheFirstDelete.Message);
    }

    [Fact]
    public async Task ARecordClaimedAgainWhileTheSweepHoldsIt_IsLeftToItsNewClaim()
    {
        // The takeover of ADR-0009 lands between the sweep's read and its delete: the key now
        // belongs to a new claim with a new ClaimId and 24 hours ahead of it.
        var raced = IdempotencyCleanupRig.Record(IdempotencyStatus.Processing, DateTime.UtcNow.AddMinutes(-1));
        var expired = IdempotencyCleanupRig.Record(IdempotencyStatus.Completed, DateTime.UtcNow.AddMinutes(-1));
        var newClaim = Guid.NewGuid();
        var takeover = new BeforeTheFirstDelete(async () =>
        {
            await using var db = Outside();
            var row = await db.IdempotencyRecords.SingleAsync(r => r.Key == raced.Key);
            row.ClaimId = newClaim;
            row.CreatedAt = DateTime.UtcNow;
            row.ExpiresAt = DateTime.UtcNow.AddHours(24);
            await db.SaveChangesAsync();
        });
        await using var rig = Rig(takeover);
        await rig.SeedAsync(raced, expired);

        await rig.StartAsync();
        await rig.EventuallyAsync(
            async () => !(await rig.KeysAsync()).Contains(expired.Key),
            "the other expired record was removed, by the raced sweep or by the next");
        await rig.MoreSweepsAsync(2);
        var loop = await rig.StopAsync();

        takeover.Fired.Should().BeTrue("the proof needs the claim to land inside a sweep");
        (await rig.RecordsAsync()).Should().ContainSingle("the new claim is inside its window")
            .Which.ClaimId.Should().Be(newClaim);
        rig.Lines(LogLevel.Debug).Should().Equal(
            "Idempotency cleanup raced a concurrent claim; deferring to next sweep");
        rig.Lines(LogLevel.Error).Should().BeEmpty("losing a row to a claim is not a failed sweep");
        loop.Status.Should().Be(TaskStatus.RanToCompletion);
    }

    [Fact]
    public async Task TheFirstSweep_RunsOneIntervalAfterStart_NotAtStart()
    {
        var interval = TimeSpan.FromSeconds(1);
        await using var rig = Rig();
        // Seeding also builds EF's model, so a sweep at start would have nothing slow in its way.
        var expired = IdempotencyCleanupRig.Record(IdempotencyStatus.Completed, DateTime.UtcNow.AddMinutes(-1));
        await rig.SeedAsync(expired);
        TimeSpan? firstSweepAt = null;
        var sinceStart = Stopwatch.StartNew();
        rig.BeforeSweep = sweep => firstSweepAt ??= sinceStart.Elapsed;

        await rig.StartAsync(interval);
        await rig.EventuallyAsync(async () => (await rig.KeysAsync()).Count == 0, "the first sweep removed the record");

        // Half the interval: the timer counts on a coarser clock than the stopwatch and may be a
        // few milliseconds early, and a sweep at start would begin within a few milliseconds of it.
        firstSweepAt.Should().BeGreaterThan(
            interval / 2, "the first sweep waits for the first tick of the configured interval");
    }

    [Fact]
    public async Task StoppingBetweenSweeps_EndsTheLoopWithoutALine()
    {
        await using var rig = Rig();
        await rig.SeedAsync(IdempotencyCleanupRig.Record(IdempotencyStatus.Executed, DateTime.UtcNow.AddHours(1)));

        await rig.StartAsync();
        await rig.MoreSweepsAsync(1);
        var loop = await rig.StopAsync();

        loop.Status.Should().Be(TaskStatus.RanToCompletion, "a host's shutdown is not a fault of the service");
        rig.Log.Lines.Should().BeEmpty("nothing had expired, and stopping is not worth a line");
    }

    [Fact]
    public async Task StoppingInsideASweep_IsNotLoggedAsAFailedSweep()
    {
        await using var rig = Rig();
        var expired = IdempotencyCleanupRig.Record(IdempotencyStatus.Executed, DateTime.UtcNow.AddMinutes(-1));
        await rig.SeedAsync(expired);
        rig.BeforeSweep = _ => rig.RequestStop();

        await rig.StartAsync();
        var loop = await rig.EndedAsync();

        loop.Status.Should().Be(TaskStatus.RanToCompletion);
        rig.SweepsStarted.Should().Be(1);
        (await rig.KeysAsync()).Should().Equal(
            [expired.Key], "the sweep was cancelled at its first read, which is what makes this a stop inside one");
        rig.Log.Lines.Should().BeEmpty("a cancelled sweep is the shutdown, not an error to retry");
    }

    [Fact]
    public async Task AFailureThatArrivesAfterTheStopWasAsked_EndsTheLoop_AndLogsNoError()
    {
        await using var rig = Rig();
        rig.BeforeSweep = _ =>
        {
            rig.RequestStop();
            throw new InvalidOperationException("Injected failure: the store went away as the host stopped.");
        };

        await rig.StartAsync();
        var loop = await rig.EndedAsync();

        loop.Status.Should().Be(TaskStatus.RanToCompletion);
        rig.SweepsStarted.Should().Be(1);
        rig.Lines(LogLevel.Error).Should().BeEmpty(
            "once the stop is asked, a sweep that failed cannot be told from one the stop interrupted, "
            + "and this process retries neither");
        rig.Lines(LogLevel.Debug).Should().ContainSingle()
            .Which.Should().StartWith("Idempotency cleanup sweep interrupted by the host's shutdown")
            .And.Contain("Injected failure", "the exception stays readable for whoever asks for Debug");
    }

    private static bool Removes(DbContextEventData save) =>
        save.Context!.ChangeTracker.Entries().Any(entry => entry.State == EntityState.Deleted);

    /// <summary>
    /// Throws from the first SaveChanges that removes a row, as a database that refuses one
    /// delete. The saves that seed the table pass.
    /// </summary>
    private sealed class RefusesTheFirstDelete : SaveChangesInterceptor
    {
        public const string Message = "Injected failure: the store refused this save.";
        private int _refusals;

        public int Refusals => Volatile.Read(ref _refusals);

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            Removes(eventData) && Interlocked.CompareExchange(ref _refusals, 1, 0) == 0
                ? throw new InvalidOperationException(Message)
                : base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    /// <summary>
    /// Runs once, just before the first SaveChanges that removes a row: after the sweep has loaded
    /// what it will remove, and before it removes it.
    /// </summary>
    private sealed class BeforeTheFirstDelete(Func<Task> act) : SaveChangesInterceptor
    {
        private int _fired;

        public bool Fired => Volatile.Read(ref _fired) == 1;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Removes(eventData) && Interlocked.CompareExchange(ref _fired, 1, 0) == 0)
            {
                await act();
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
