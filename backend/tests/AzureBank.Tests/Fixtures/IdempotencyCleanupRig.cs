using AzureBank.Api.Services;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Options;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// <see cref="IdempotencyCleanupService"/> on a database a test chooses, with its log recorded and
/// its sweeps counted (ADR-0009).
/// </summary>
/// <remarks>
/// <para>
/// THE SERVICE HAS NO CLOCK TO MOVE. Its timer and its "now" are the machine's, so a test gives it a
/// short interval and waits for a state with <see cref="EventuallyAsync"/>, which gives up after
/// <see cref="Patience"/> or as soon as the loop has ended. Nothing here asserts after a fixed sleep.
/// </para>
/// <para>
/// A SWEEP IS COUNTED WHERE IT STARTS. The first thing a sweep does is ask for a scope, and the loop
/// runs one sweep at a time, so the scope factory handed to the service is where a test counts
/// sweeps, learns that the previous one is over, or steps in before one begins.
/// </para>
/// <para>
/// THE LOG HOLDS THE SERVICE'S LINES ONLY. The container registers no logging, so EF writes
/// nowhere, and a test can count lines without filtering them by category.
/// </para>
/// </remarks>
internal sealed class IdempotencyCleanupRig : IAsyncDisposable
{
    /// <summary>Short enough that a test sees several sweeps in a second.</summary>
    public static readonly TimeSpan ShortInterval = TimeSpan.FromMilliseconds(50);

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly ServiceProvider _provider;
    private readonly CountingScopeFactory _scopes;
    private readonly ILoggerFactory _loggers;
    private readonly CancellationTokenSource _stop = new();
    private IdempotencyCleanupService? _service;

    public IdempotencyCleanupRig(Action<DbContextOptionsBuilder> database)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AzureBankDbContext>(database);
        _provider = services.BuildServiceProvider();
        _scopes = new CountingScopeFactory(_provider.GetRequiredService<IServiceScopeFactory>());
        _loggers = LoggerFactory.Create(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(Log));
    }

    public RecordingLoggerProvider Log { get; } = new();

    /// <summary>Sweeps that have started. Sweep n is over once this reads n + 1.</summary>
    public int SweepsStarted => _scopes.Started;

    /// <summary>
    /// Called on the loop's own thread just before sweep n (from 1) asks for its scope. What it
    /// throws, the sweep throws.
    /// </summary>
    public Action<int>? BeforeSweep
    {
        get => _scopes.BeforeSweep;
        set => _scopes.BeforeSweep = value;
    }

    /// <summary>The loop itself: completed when the service has ended, faulted if it ended badly.</summary>
    public Task Loop => _service?.ExecuteTask ?? Task.CompletedTask;

    /// <summary>A record as a claim writes it, expiring when the test says.</summary>
    public static IdempotencyRecord Record(
        IdempotencyStatus status, DateTime expiresAt, string endpoint = "POST api/deposits") => new()
        {
            UserId = Guid.NewGuid(),
            Endpoint = endpoint,
            Key = Guid.NewGuid(),
            ClaimId = Guid.NewGuid(),
            RequestHash = new string('a', 64),
            Status = status,
            CreatedAt = expiresAt.AddHours(-24),
            ExpiresAt = expiresAt,
        };

    /// <summary>The Warning the sweep owes an expired Executed record, up to the user.</summary>
    public static string WarningFor(IdempotencyRecord record) =>
        $"Cleaning up EXECUTED idempotency record {record.Endpoint}/{record.Key} (user {record.UserId})";

    public async Task SeedAsync(params IdempotencyRecord[] records)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        db.IdempotencyRecords.AddRange(records);
        await db.SaveChangesAsync();
    }

    /// <summary>Every record the table holds now, read on a context of its own.</summary>
    public async Task<List<IdempotencyRecord>> RecordsAsync()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return await db.IdempotencyRecords.AsNoTracking().ToListAsync();
    }

    public async Task<List<Guid>> KeysAsync() => [.. (await RecordsAsync()).Select(r => r.Key)];

    public IReadOnlyList<string> Lines(LogLevel level) =>
        [.. Log.Lines.Where(line => line.Level == level).Select(line => line.Message)];

    public async Task StartAsync(TimeSpan? interval = null)
    {
        _service = new IdempotencyCleanupService(
            _scopes,
            Microsoft.Extensions.Options.Options.Create(
                new IdempotencyOptions { CleanupInterval = interval ?? ShortInterval }),
            _loggers.CreateLogger<IdempotencyCleanupService>());
        await _service.StartAsync(_stop.Token);
    }

    /// <summary>
    /// Ends the loop when sweep <paramref name="sweeps"/> + 1 would begin, the way a host's
    /// shutdown does, so a test reads the table as exactly that many sweeps left it.
    /// </summary>
    public void StopAfter(int sweeps) =>
        BeforeSweep = sweep =>
        {
            if (sweep > sweeps)
            {
                _stop.Cancel();
                _stop.Token.ThrowIfCancellationRequested();
            }
        };

    /// <summary>Cancels the token the host stops the service with, and does not wait.</summary>
    public void RequestStop() => _stop.Cancel();

    /// <summary>Stops the service as a host does and hands back its loop for the test to judge.</summary>
    public async Task<Task> StopAsync()
    {
        await _service!.StopAsync(CancellationToken.None);
        return Loop;
    }

    /// <summary>Waits until the loop has ended, without stopping it.</summary>
    public async Task<Task> EndedAsync()
    {
        await Task.WhenAny(Loop, Task.Delay(Patience));
        Loop.IsCompleted.Should().BeTrue($"the loop was to end within {Patience.TotalSeconds:0} s");
        return Loop;
    }

    /// <summary>Waits until <paramref name="sweeps"/> more sweeps have run from start to end.</summary>
    public Task MoreSweepsAsync(int sweeps)
    {
        // One more than asked: the sweep in flight now may have read the table before the test
        // changed it, and the last one counted is over only when the next has started.
        var target = SweepsStarted + sweeps + 2;
        return EventuallyAsync(() => Task.FromResult(SweepsStarted >= target), $"{sweeps} more sweeps had run");
    }

    /// <summary>
    /// Polls until <paramref name="condition"/> holds. Fails with <paramref name="what"/> and the
    /// last lines of the log after <see cref="Patience"/>, or at once when the loop has ended without it.
    /// </summary>
    public async Task EventuallyAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            var ended = Loop.IsCompleted && _service is not null;
            if (await condition())
            {
                return;
            }

            if (ended || DateTime.UtcNow >= deadline)
            {
                var why = ended
                    ? $"the loop ended ({Loop.Status}{(Loop.Exception is { } e ? ": " + e.GetBaseException().Message : "")})"
                    : $"{Patience.TotalSeconds:0} s passed";
                Assert.Fail(
                    $"Expected that {what}, but {why} after {SweepsStarted} sweeps started. Last lines of the log:\n"
                    + string.Join("\n", Log.Lines.TakeLast(10).Select(line => $"  {line.Level}: {line.Message}")));
            }

            await Task.Delay(20);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await Loop;
        }
        catch (Exception)
        {
            // A test that made the loop fail has already judged it.
        }

        _stop.Dispose();
        _loggers.Dispose();
        await _provider.DisposeAsync();
    }

    private sealed class CountingScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory
    {
        private int _started;

        public int Started => Volatile.Read(ref _started);

        public Action<int>? BeforeSweep { get; set; }

        public IServiceScope CreateScope()
        {
            var sweep = Interlocked.Increment(ref _started);
            BeforeSweep?.Invoke(sweep);
            return inner.CreateScope();
        }
    }
}

/// <summary>
/// What <see cref="IdempotencyCleanupService"/> owes on either of its two branches, the set-based
/// delete of a relational database and the load-and-remove of the in-memory provider (ADR-0009).
/// Each test class builds the rig on its own database and runs these.
/// </summary>
internal static class IdempotencyCleanupProofs
{
    /// <summary>
    /// A record past its window goes, whatever its status; one inside it stays, whatever its
    /// status; and only the expired Executed one is reported, once, with its endpoint, key and user.
    /// </summary>
    public static async Task ExpiredRecordsGo_LiveOnesStay_AndOnlyAnExpiredExecutedOneIsReported(
        IdempotencyCleanupRig rig)
    {
        var past = DateTime.UtcNow.AddMinutes(-1);
        var future = DateTime.UtcNow.AddHours(1);
        var expiredExecuted = IdempotencyCleanupRig.Record(IdempotencyStatus.Executed, past, "POST api/transfers/external");
        var expired = new[]
        {
            IdempotencyCleanupRig.Record(IdempotencyStatus.Processing, past),
            expiredExecuted,
            IdempotencyCleanupRig.Record(IdempotencyStatus.Completed, past),
        };
        var live = new[]
        {
            IdempotencyCleanupRig.Record(IdempotencyStatus.Processing, future),
            IdempotencyCleanupRig.Record(IdempotencyStatus.Executed, future, "POST api/transfers/internal"),
            IdempotencyCleanupRig.Record(IdempotencyStatus.Completed, future),
        };
        await rig.SeedAsync([.. expired, .. live]);

        await rig.StartAsync();
        await rig.EventuallyAsync(
            async () => !(await rig.KeysAsync()).Intersect(expired.Select(r => r.Key)).Any(),
            "the three expired records were removed");
        await rig.MoreSweepsAsync(2);
        var loop = await rig.StopAsync();

        (await rig.KeysAsync()).Should().BeEquivalentTo(
            live.Select(r => r.Key),
            "a key inside its 24 hours is what refuses a second payment (ADR-0009): the sweeps since "
            + "must have left all three, the Executed one included");
        rig.Lines(LogLevel.Warning).Should().ContainSingle(
            "the expired Executed record is the one that committed without a stored answer, and it is "
            + "gone after the sweep that reported it")
            .Which.Should().StartWith(IdempotencyCleanupRig.WarningFor(expiredExecuted));
        rig.Lines(LogLevel.Information).Should().Equal("Idempotency cleanup removed 3 expired records");
        rig.Lines(LogLevel.Error).Should().BeEmpty();
        loop.Status.Should().Be(TaskStatus.RanToCompletion);
    }

    /// <summary>
    /// The delete fails once. The record was reported before the delete was tried, the failure is
    /// logged as an error, the record is still there, and the next sweep reports and removes it.
    /// </summary>
    /// <param name="rig">A rig whose database refuses the first delete.</param>
    /// <param name="refusals">How many deletes the database has refused so far.</param>
    /// <param name="refusal">The sentence the refused delete throws.</param>
    public static async Task ADeleteThatFails_ComesAfterTheWarning_IsLoggedAsAnError_AndTheNextSweepRemoves(
        IdempotencyCleanupRig rig, Func<int> refusals, string refusal)
    {
        var executed = IdempotencyCleanupRig.Record(IdempotencyStatus.Executed, DateTime.UtcNow.AddMinutes(-1));
        var live = IdempotencyCleanupRig.Record(IdempotencyStatus.Completed, DateTime.UtcNow.AddHours(1));
        await rig.SeedAsync(executed, live);

        await rig.StartAsync();
        await rig.EventuallyAsync(
            async () => !(await rig.KeysAsync()).Contains(executed.Key),
            "a sweep after the failed one removed the expired record");
        var loop = await rig.StopAsync();

        refusals().Should().Be(1, "the proof needs exactly one refused delete");
        var lines = rig.Log.Lines;
        var error = lines.Should().ContainSingle(line => line.Level == LogLevel.Error).Which;
        error.Message.Should().StartWith("Idempotency cleanup sweep failed; will retry next interval")
            .And.Contain(refusal);
        lines.TakeWhile(line => line.Level != LogLevel.Error)
            .Should().ContainSingle(line => line.Level == LogLevel.Warning, "the record is reported before its delete is tried")
            .Which.Message.Should().StartWith(IdempotencyCleanupRig.WarningFor(executed));
        rig.Lines(LogLevel.Warning).Should().HaveCount(
            2, "the record outlived the failed sweep, so the sweep that removed it reported it again");
        (await rig.KeysAsync()).Should().Equal(live.Key);
        loop.Status.Should().Be(TaskStatus.RanToCompletion);
    }
}
