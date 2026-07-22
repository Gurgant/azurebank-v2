using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Proves refresh-token rotation against REAL SQL Server (ADR-0021). The rotation writes a
/// self-referencing FK (old.ReplacedByTokenId -> successor.Id, DeleteBehavior.Restrict): EF
/// must order the successor INSERT before the old-token UPDATE, which InMemory does not
/// enforce. Reuse-detection revokes the whole active set via a set-based ExecuteUpdate — the
/// relational path the InMemory tests can only approximate via a load+save fallback.
/// </summary>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class RefreshTokenRotationSqlServerTests : IDisposable
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private const string Password = "SecurePass123!";
    private CustomWebApplicationFactory? _factory;

    [SqlServerFact]
    public async Task Refresh_RotatesChain_AndReuseRevokesFamily_OnSqlServer()
    {
        var client = CreateSqlClient();
        var (userId, _, refresh) = await RegisterAsync(client);

        // Rotate: the self-referencing FK write must succeed on real SQL (successor inserted
        // before the old-token update).
        var rotated = await client.PostAsJsonAsync("/api/auth/refresh",
            new RefreshRequest { RefreshToken = refresh }, Json);
        rotated.StatusCode.Should().Be(HttpStatusCode.OK);
        var successor = (await rotated.Content
            .ReadFromJsonAsync<ApiResponse<RefreshResponse>>(Json))!.Data!.RefreshToken;
        successor.Should().NotBe(refresh);

        // The persisted chain: the presented token is revoked and links to its successor.
        var (revokedAt, replacedBy, successorCount) = await ReadChainAsync(userId);
        revokedAt.Should().NotBeNull("the presented token must be revoked on rotation");
        replacedBy.Should().NotBeNull("the rotation chain (ReplacedByTokenId) must be linked on SQL Server");
        successorCount.Should().Be(1, "exactly one active successor exists after a single rotation");

        // Age the revocation past the grace window so the replay reads as genuine theft (an
        // immediate replay would be treated as a benign lost-response retry).
        await AgeRevocationsBeyondGraceAsync();

        // Reuse the ORIGINAL (now revoked) token → 401 + set-based family revocation.
        var reuse = await client.PostAsJsonAsync("/api/auth/refresh",
            new RefreshRequest { RefreshToken = refresh }, Json);
        reuse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // The successor is revoked too (ExecuteUpdate over the active set), so it cannot rotate.
        var successorReuse = await client.PostAsJsonAsync("/api/auth/refresh",
            new RefreshRequest { RefreshToken = successor }, Json);
        successorReuse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await CountActiveAsync(userId)).Should().Be(0,
            "reuse-detection must revoke EVERY active refresh token for the user on SQL Server");
    }

    [SqlServerFact]
    public async Task ConcurrentRotationsOfSameToken_ProduceExactlyOneSuccessor_NoForkNoFalseReuse()
    {
        var client = CreateSqlClient();
        var (userId, _, refresh) = await RegisterAsync(client);

        // Fire N parallel /refresh calls with the SAME token. The rowversion concurrency guard
        // admits exactly ONE rotation; every loser is rejected (401) as a benign race — NOT a
        // family-revoking reuse — so the rotation chain can never fork.
        const int parallel = 8;
        var responses = await Task.WhenAll(Enumerable.Range(0, parallel)
            .Select(_ => client.PostAsJsonAsync("/api/auth/refresh",
                new RefreshRequest { RefreshToken = refresh }, Json)));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK)
            .Should().Be(1, "exactly one concurrent rotation may win — the chain must not fork");
        responses.Count(r => r.StatusCode == HttpStatusCode.Unauthorized)
            .Should().Be(parallel - 1, "every losing racer is rejected, not forked");

        // No FALSE reuse-detection: exactly one active successor remains (the winner's). A
        // family-revoke triggered by a mis-classified race would zero this.
        (await CountActiveAsync(userId)).Should().Be(1,
            "the winning successor stays active; a false reuse-revoke would have zeroed it");

        // And the surviving successor still rotates.
        var winner = responses.Single(r => r.StatusCode == HttpStatusCode.OK);
        var successor = (await winner.Content.ReadFromJsonAsync<ApiResponse<RefreshResponse>>(Json))!.Data!.RefreshToken;
        (await client.PostAsJsonAsync("/api/auth/refresh",
            new RefreshRequest { RefreshToken = successor }, Json)).StatusCode
            .Should().Be(HttpStatusCode.OK, "the surviving successor must remain usable");
    }

    [SqlServerFact]
    public async Task LoginAfterFailedAttempt_IssuesRefreshToken_NoDuplicateUserInsert()
    {
        var client = CreateSqlClient();
        var (_, email, _) = await RegisterAsync(client);

        // One wrong password bumps AccessFailedCount to 1, so the next CORRECT login takes the
        // lockout-RESET branch — which detaches the tracked user on SQL Server.
        (await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest { Email = email, Password = "WrongPass999!" }, Json)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);

        // The correct login must succeed and issue a refresh token: issuance sets only the
        // UserId FK, so the detached principal is NOT re-INSERTed (which would be a dup-key 500).
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest { Email = email, Password = Password }, Json);
        login.StatusCode.Should().Be(HttpStatusCode.OK,
            "issuing a refresh token after a lockout reset must not re-insert the detached user");
        var body = await login.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>(Json);
        body!.Data!.RefreshToken.Should().NotBeNullOrEmpty();
    }

    private async Task<(DateTime? RevokedAt, Guid? ReplacedBy, int SuccessorCount)> ReadChainAsync(Guid userId)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var tokens = await db.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == userId).ToListAsync();
        var presented = tokens.Single(t => t.ReplacedByTokenId != null || t.RevokedAt != null);
        var successors = tokens.Count(t => t.RevokedAt == null);
        return (presented.RevokedAt, presented.ReplacedByTokenId, successors);
    }

    /// <summary>Ages every revoked token past the grace window so a replay reads as theft.</summary>
    private async Task AgeRevocationsBeyondGraceAsync()
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var revoked = await db.RefreshTokens.Where(t => t.RevokedAt != null).ToListAsync();
        foreach (var token in revoked)
        {
            token.RevokedAt = DateTime.UtcNow.AddMinutes(-1);
        }
        await db.SaveChangesAsync();
    }

    private async Task<int> CountActiveAsync(Guid userId)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var now = DateTime.UtcNow;
        return await db.RefreshTokens.AsNoTracking()
            .CountAsync(t => t.UserId == userId && t.RevokedAt == null && t.ExpiresAt > now);
    }

    private HttpClient CreateSqlClient()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        return _factory.CreateClient();
    }

    private static async Task<(Guid UserId, string Email, string Refresh)> RegisterAsync(HttpClient client)
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var email = $"rtrot{uniqueId}@example.com";
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"rtrot_{uniqueId}",
            Email = email,
            Password = Password,
            FirstName = "Rot",
            LastName = "Ate"
        }, Json);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json);
        // The API always populates RefreshToken on a successful register (nullable only so a
        // cross-boundary consumer degrades gracefully) — assert non-null for the test tuple.
        return (result!.Data!.User.Id, email, result.Data.Token.RefreshToken!);
    }

    public void Dispose() => _factory?.Dispose();
}
