using AzureBank.Api.Services;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Options;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AzureBank.Tests.Unit.Services;

/// <summary>
/// The refresh-token sweep when a run of it fails, and when a row it keeps points at a row it
/// deletes (the outcome only: the second test says what this provider cannot hold).
/// </summary>
/// <remarks>
/// On InMemory and a <see cref="FakeTimeProvider"/>, with the loop run directly, for the reasons
/// <c>RefreshTokenCleanupServiceTests</c> gives. The statements a real database runs are held by
/// <c>RefreshTokenCleanupSqlServerTests</c>.
/// </remarks>
public sealed class RefreshTokenCleanupFailureAndLinkTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2099, 1, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly FakeTimeProvider _clock = new(Start);
    private readonly RecordingLoggerProvider _log = new();
    private readonly ILoggerFactory _loggers;
    private readonly ServiceProvider _provider;

    public RefreshTokenCleanupFailureAndLinkTests()
    {
        _loggers = LoggerFactory.Create(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(_log));
        var services = new ServiceCollection();
        var database = Guid.NewGuid().ToString();
        services.AddDbContext<AzureBankDbContext>(o => o.UseInMemoryDatabase(database));
        _provider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _loggers.Dispose();
    }

    /// <summary>Exposes the loop itself, so the timer exists before the test moves the clock.</summary>
    private sealed class Probe(IServiceScopeFactory scopes, ILogger<RefreshTokenCleanupService> logger, TimeProvider clock)
        : RefreshTokenCleanupService(
            scopes,
            logger,
            Microsoft.Extensions.Options.Options.Create(new JwtOptions { RefreshTokenCleanupInterval = Interval }),
            clock)
    {
        public Task RunAsync(CancellationToken stoppingToken) => ExecuteAsync(stoppingToken);
    }

    /// <summary>The real scope factory, refusing the first <paramref name="failures"/> scopes it is asked for.</summary>
    private sealed class FailsFirst(IServiceScopeFactory inner, int failures) : IServiceScopeFactory
    {
        private int _asked;

        public int Asked => Volatile.Read(ref _asked);

        public IServiceScope CreateScope() =>
            Interlocked.Increment(ref _asked) <= failures
                ? throw new InvalidOperationException("Injected failure: the store was not there for this sweep.")
                : inner.CreateScope();
    }

    private Probe Sweep(IServiceScopeFactory scopes) =>
        new(scopes, _loggers.CreateLogger<RefreshTokenCleanupService>(), _clock);

    private static RefreshToken Token(string name, DateTimeOffset expiresAt) => new()
    {
        Id = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        TokenHash = name,
        ExpiresAt = expiresAt.UtcDateTime,
        CreatedAt = Start.UtcDateTime,
        IpAddress = "127.0.0.1",
        UserAgent = "test",
        RowVersion = [1],
    };

    private async Task SeedAsync(params RefreshToken[] tokens)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        db.RefreshTokens.AddRange(tokens);
        await db.SaveChangesAsync();
    }

    private async Task<List<RefreshToken>> RemainingAsync()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return await db.RefreshTokens.AsNoTracking().OrderBy(t => t.TokenHash).ToListAsync();
    }

    private static async Task<bool> EventuallyAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!await condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(25);
        }

        return true;
    }

    [Fact]
    public async Task ASweepThatFails_IsLogged_AndTheNextIntervalSweepsAgain()
    {
        // The sweep is a hosted service: an exception that left its loop would end it for the life
        // of the process, with nothing said, and the table would grow by a row for each sign-in.
        await SeedAsync(Token("expired-before-start", Start.AddMinutes(-1)), Token("lives-a-week", Start.AddDays(7)));
        var scopes = new FailsFirst(_provider.GetRequiredService<IServiceScopeFactory>(), failures: 1);
        using var stop = new CancellationTokenSource();
        var loop = Sweep(scopes).RunAsync(stop.Token);

        // The first interval: the sweep runs and fails.
        _clock.Advance(Interval);
        var failed = await EventuallyAsync(() => Task.FromResult(scopes.Asked >= 1 && _log.Lines.Count > 0));
        var afterTheFailure = (await RemainingAsync()).Select(t => t.TokenHash).ToList();
        var said = _log.Lines.ToList();

        // The second: it runs again, and this time the store answers.
        _clock.Advance(Interval);
        var swept = await EventuallyAsync(async () => (await RemainingAsync()).Count == 1);

        await stop.CancelAsync();
        await loop;

        using var all = new AssertionScope();
        failed.Should().BeTrue("ARRANGE: the first sweep was attempted and said something");
        afterTheFailure.Should().Equal(["expired-before-start", "lives-a-week"], "a failed sweep removed nothing");
        said.Should().ContainSingle(line => line.Level == LogLevel.Error)
            .Which.Message.Should().StartWith("Refresh-token cleanup sweep failed; will retry next interval")
            .And.Contain("Injected failure");
        swept.Should().BeTrue("the loop outlived the failure");
        (await RemainingAsync()).Select(t => t.TokenHash).Should().Equal(["lives-a-week"]);
    }

    [Fact]
    public async Task ALiveTokenThatPointsAtAnExpiredOne_IsKept_AndNamesNoRowThatIsGone()
    {
        // A row of the old rotation chain: the predecessor still lives and names a successor that
        // has expired, which is what a shortened lifetime leaves behind. The sweep deletes by the
        // row's own expiry, never by what points at it.
        //
        // WHAT THIS HOLDS, AND WHAT IT DOES NOT. On this provider: that the sweep's in-memory
        // branch reads the rows that point at an expiring one, live ones included, keeps the live
        // row, ends with no link to a row that is gone and says nothing at Warning. It does NOT
        // hold the assignment that clears the link: the referrers are tracked when the expired
        // rows are removed, and EF then sets the key of a tracked dependent to null by itself.
        // Measured 2026-10-12: green with that assignment taken out.
        // The statement that clears the link in a real database, where nothing does it for the
        // sweep, is held by RefreshTokenCleanupSqlServerTests.
        var expired = Token("expired-successor", Start.AddMinutes(-1));
        var live = Token("live-predecessor", Start.AddDays(7));
        live.ReplacedByTokenId = expired.Id;
        await SeedAsync(expired, live);
        using var stop = new CancellationTokenSource();
        var loop = Sweep(_provider.GetRequiredService<IServiceScopeFactory>()).RunAsync(stop.Token);

        _clock.Advance(Interval);
        var swept = await EventuallyAsync(async () => (await RemainingAsync()).Count == 1);

        await stop.CancelAsync();
        await loop;

        var left = await RemainingAsync();
        using var all = new AssertionScope();
        swept.Should().BeTrue();
        left.Should().ContainSingle().Which.TokenHash.Should().Be("live-predecessor");
        left[0].ReplacedByTokenId.Should().BeNull("a kept row does not name a row that is gone");
        _log.Lines.Should().NotContain(line => line.Level >= LogLevel.Warning);
    }
}
