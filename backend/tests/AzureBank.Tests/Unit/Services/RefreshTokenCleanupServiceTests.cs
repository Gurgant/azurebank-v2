using AzureBank.Api.Services;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Options;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AzureBank.Tests.Unit.Services;

/// <summary>
/// The refresh-token sweep runs every <c>Jwt:RefreshTokenCleanupInterval</c>, the first time one
/// interval after start, and deletes what has expired by ITS clock (ADR-0021).
/// </summary>
/// <remarks>
/// On InMemory, so this is the sweep's load-and-remove branch; the relational branch did not change.
/// The clock is a <see cref="FakeTimeProvider"/> set years ahead of the real one, so a token that has
/// expired by the fake clock has not by the wall clock: a sweep reading <c>DateTime.UtcNow</c> would
/// keep it. The loop is run through <see cref="Probe.RunAsync"/> rather than <c>StartAsync</c>, which
/// since .NET 10 returns before <c>ExecuteAsync</c> has created its timer: a timer created after the
/// first <c>Advance</c> would start counting from the advanced time.
/// </remarks>
public sealed class RefreshTokenCleanupServiceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2031, 1, 6, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _clock = new(Start);
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly ServiceProvider _provider;

    public RefreshTokenCleanupServiceTests()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AzureBankDbContext>(o => o.UseInMemoryDatabase(_database));
        _provider = services.BuildServiceProvider();
    }

    public void Dispose() => _provider.Dispose();

    /// <summary>Exposes the loop itself, so the timer exists before the test moves the clock.</summary>
    private sealed class Probe(IServiceScopeFactory scopes, JwtOptions options, TimeProvider clock)
        : RefreshTokenCleanupService(
            scopes,
            NullLogger<RefreshTokenCleanupService>.Instance,
            Microsoft.Extensions.Options.Options.Create(options),
            clock)
    {
        public Task RunAsync(CancellationToken stoppingToken) => ExecuteAsync(stoppingToken);
    }

    private async Task Seed(params (string Name, DateTimeOffset ExpiresAt)[] tokens)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        foreach (var (name, expiresAt) in tokens)
        {
            db.RefreshTokens.Add(new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserId = Guid.NewGuid(),
                TokenHash = name,
                ExpiresAt = expiresAt.UtcDateTime,
                CreatedAt = Start.UtcDateTime,
                IpAddress = "127.0.0.1",
                UserAgent = "test",
                RowVersion = [1],
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task<List<string>> Remaining()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return await db.RefreshTokens.Select(t => t.TokenHash).OrderBy(n => n).ToListAsync();
    }

    [Fact]
    public async Task TheSweep_RunsOnTheConfiguredInterval_AndDeletesWhatHasExpiredByItsClock()
    {
        await Seed(("expires-in-30-minutes", Start.AddMinutes(30)), ("lives-a-week", Start.AddDays(7)));
        var sweep = new Probe(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            new JwtOptions { RefreshTokenCleanupInterval = TimeSpan.FromHours(1) },
            _clock);
        using var stop = new CancellationTokenSource();
        var loop = sweep.RunAsync(stop.Token);

        // A minute short of the first interval: the first token has expired, and nothing has run.
        _clock.Advance(TimeSpan.FromMinutes(59));
        await Task.Delay(200);
        (await Remaining()).Should().Equal(["expires-in-30-minutes", "lives-a-week"]);

        // The interval: the sweep runs, and what has expired by the fake clock goes.
        _clock.Advance(TimeSpan.FromMinutes(1));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && (await Remaining()).Count > 1)
        {
            await Task.Delay(25);
        }

        (await Remaining()).Should().Equal(["lives-a-week"],
            "one hour after start the sweep ran, with the configured interval rather than six hours, and "
            + "judged expiry by its own clock, which the wall clock is years behind");

        await stop.CancelAsync();
        await loop;
    }
}
