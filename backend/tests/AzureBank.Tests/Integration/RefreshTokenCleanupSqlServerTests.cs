using System.Net.Http.Json;
using AzureBank.Api.Services;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Options;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The refresh-token sweep on a real database: the two statements only a relational provider
/// runs, and the foreign key they have to get past.
/// </summary>
/// <remarks>
/// <para>
/// <c>RefreshTokens</c> references itself (<c>ReplacedByTokenId</c>, no cascade). A DELETE of the
/// expired rows fails as a whole while one row that stays still names one that goes, and then the
/// sweep removes nothing, on every interval, for as long as that row lives. So the sweep clears
/// those links first, by the expiry of the row they point AT. The in-memory provider has no
/// foreign key and runs another branch of the sweep: only this can show the two statements work.
/// </para>
/// <para>
/// The clock is a <see cref="FakeTimeProvider"/> started at the real time and moved by the
/// shortest interval the API accepts, one minute.
/// </para>
/// <para>
/// ITS OWN DATABASE, created for the test and dropped when it ends. The sweep deletes every
/// expired row of the database it is pointed at: on the one the SQL Server proofs share it would
/// take other proofs' rows with it, and this test would leave its user and its live grants there.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class RefreshTokenCleanupSqlServerTests
{
    /// <summary>Exposes the loop itself, so the timer exists before the test moves the clock.</summary>
    private sealed class Probe(IServiceScopeFactory scopes, ILogger<RefreshTokenCleanupService> logger, TimeProvider clock)
        : RefreshTokenCleanupService(
            scopes,
            logger,
            Microsoft.Extensions.Options.Options.Create(
                new JwtOptions { RefreshTokenCleanupInterval = JwtOptions.ShortestCleanupInterval }),
            clock)
    {
        public Task RunAsync(CancellationToken stoppingToken) => ExecuteAsync(stoppingToken);
    }

    [SqlServerFact]
    public async Task TheSweep_DeletesWhatHasExpired_KeepsWhatLives_AndClearsALinkToARowItDeletes()
    {
        // Measured 2026-10-12: the two expired rows gone, the three live ones kept, the link of the
        // live predecessor null, and nothing logged at Error.
        await using var database = await DemoPoolDatabase.CreateAsync();
        var factory = database.Api();
        factory.CaptureLog();
        var client = factory.CreateClient();

        // A real user, for the foreign key to AspNetUsers; registration also leaves one live grant.
        var unique = Guid.NewGuid().ToString("N")[..8];
        var registered = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"swp_{unique}",
            Email = $"sweep{unique}@example.com",
            Password = "SecurePass123!",
            FirstName = "Sweep",
            LastName = "Proof",
        });
        registered.EnsureSuccessStatusCode();

        var now = DateTime.UtcNow;
        Guid userId;
        Guid registrationGrant;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
            userId = await db.Users.Where(u => u.Email == $"sweep{unique}@example.com").Select(u => u.Id).SingleAsync();
            registrationGrant = await db.RefreshTokens.Where(t => t.UserId == userId).Select(t => t.Id).SingleAsync();

            RefreshToken Token(string name, DateTime expiresAt) => new()
            {
                Id = Guid.CreateVersion7(),
                UserId = userId,
                TokenHash = $"{name}-{unique}",
                ExpiresAt = expiresAt,
                CreatedAt = now.AddDays(-1),
                IpAddress = "127.0.0.1",
                UserAgent = "sweep-proof",
            };

            var expired = Token("expired", now.AddMinutes(-5));
            var expiredSuccessor = Token("expired-successor", now.AddMinutes(-5));
            var livePredecessor = Token("live-predecessor", now.AddDays(7));
            var live = Token("live", now.AddDays(7));
            db.RefreshTokens.AddRange(expired, expiredSuccessor, livePredecessor, live);
            await db.SaveChangesAsync();

            // The link is set once both rows exist: the predecessor lives and names a successor
            // that has expired, which is what a shortened token lifetime leaves behind.
            livePredecessor.ReplacedByTokenId = expiredSuccessor.Id;

            // And one link between two rows that both live: a sweep that cleared every link
            // would pass without it.
            live.ReplacedByTokenId = registrationGrant;
            await db.SaveChangesAsync();
        }

        var clock = new FakeTimeProvider(new DateTimeOffset(now, TimeSpan.Zero));
        var sweep = new Probe(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            factory.Services.GetRequiredService<ILogger<RefreshTokenCleanupService>>(),
            clock);
        using var stop = new CancellationTokenSource();
        var loop = sweep.RunAsync(stop.Token);

        clock.Advance(JwtOptions.ShortestCleanupInterval);

        async Task<List<RefreshToken>> OfThisUserAsync()
        {
            using var scope = factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<AzureBankDbContext>().RefreshTokens
                .AsNoTracking().Where(t => t.UserId == userId).OrderBy(t => t.TokenHash).ToListAsync();
        }

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && (await OfThisUserAsync()).Count > 3)
        {
            await Task.Delay(50);
        }

        await stop.CancelAsync();
        await loop;

        var left = await OfThisUserAsync();
        using var all = new AssertionScope();
        left.Where(t => t.Id != registrationGrant).Select(t => t.TokenHash).Should().Equal(
            [$"live-{unique}", $"live-predecessor-{unique}"], "what has expired goes, and what lives stays");
        left.Should().Contain(t => t.Id == registrationGrant, "the grant the registration issued is an hour from its expiry");
        left.Single(t => t.TokenHash == $"live-predecessor-{unique}").ReplacedByTokenId
            .Should().BeNull("the row that stays no longer names the row that went");
        left.Single(t => t.TokenHash == $"live-{unique}").ReplacedByTokenId
            .Should().Be(registrationGrant, "a link to a row that lives is left alone");
        factory.CapturedLog.Should().NotContain(
            line => line.Contains("Refresh-token cleanup sweep failed", StringComparison.Ordinal),
            "the foreign key did not refuse the delete");
    }
}
