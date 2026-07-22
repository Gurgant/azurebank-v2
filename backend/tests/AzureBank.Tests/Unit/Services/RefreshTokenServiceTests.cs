using System.Security.Cryptography;
using System.Text;
using AzureBank.Api.Services.Implementations;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Exceptions;
using AzureBank.Shared.Options;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace AzureBank.Tests.Unit.Services;

/// <summary>
/// Unit tests for RefreshTokenService: issuance (hash-at-rest), rotate-on-use with the
/// ReplacedByTokenId chain, reuse-detection that revokes the whole active set, expiry, and
/// bulk revoke. Runs on the EF InMemory provider (exercises the non-relational fallbacks).
/// </summary>
public class RefreshTokenServiceTests : IDisposable
{
    private readonly AzureBankDbContext _context;
    private readonly RefreshTokenService _sut;

    public RefreshTokenServiceTests()
    {
        var options = new DbContextOptionsBuilder<AzureBankDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            // The RowVersion concurrency token is SQL-Server-generated; the InMemory provider
            // can't produce it, so downgrade byte[] concurrency tokens exactly as the
            // integration fixture does (concurrency itself is proved on the SQL-gated path).
            .ReplaceService<IModelCustomizer, InMemoryTestModelCustomizer>()
            .Options;
        _context = new AzureBankDbContext(options);

        _sut = BuildService(new JwtOptions { RefreshTokenExpirationDays = 7 });
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private RefreshTokenService BuildService(JwtOptions jwtOptions)
    {
        var httpContextAccessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        httpContextAccessor.HttpContext!.Request.Headers.UserAgent = "xunit/1.0";
        return new RefreshTokenService(
            _context,
            httpContextAccessor,
            Options.Create(jwtOptions),
            new Mock<ILogger<RefreshTokenService>>().Object);
    }

    private ApplicationUser SeedUser()
    {
        var id = Guid.CreateVersion7();
        var user = new ApplicationUser
        {
            Id = id,
            Email = $"rt{id:N}@example.com",
            UserName = id.ToString(),
            AzureTag = $"rt_{id:N}"[..12],
            FirstName = "Refresh",
            LastName = "Token",
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow
        };
        _context.Users.Add(user);
        _context.SaveChanges();
        return user;
    }

    [Fact]
    public async Task IssueAsync_PersistsHashedToken_AndReturnsPlaintext()
    {
        var user = SeedUser();

        var plaintext = await _sut.IssueAsync(user);

        plaintext.Should().NotBeNullOrEmpty();
        var stored = await _context.RefreshTokens.SingleAsync();
        stored.UserId.Should().Be(user.Id);
        stored.TokenHash.Should().NotBe(plaintext, "only the SHA-256 hash may be stored, never the plaintext");
        stored.TokenHash.Should().Be(
            Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext))),
            "the stored value must be exactly SHA-256(plaintext) in base64 — pins the hash-at-rest contract");
        stored.RevokedAt.Should().BeNull();
        stored.IsActive.Should().BeTrue();
        stored.ExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddDays(7), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task RotateAsync_ValidToken_RevokesOld_ChainsSuccessor_ReturnsNewPlaintext()
    {
        var user = SeedUser();
        var oldPlaintext = await _sut.IssueAsync(user);
        var oldId = (await _context.RefreshTokens.SingleAsync()).Id;

        var result = await _sut.RotateAsync(oldPlaintext);

        result.User.Id.Should().Be(user.Id);
        result.RefreshToken.Should().NotBe(oldPlaintext, "rotation issues a fresh successor token");

        _context.ChangeTracker.Clear();
        var tokens = await _context.RefreshTokens.ToListAsync();
        tokens.Should().HaveCount(2);
        var oldToken = tokens.Single(t => t.Id == oldId);
        var newToken = tokens.Single(t => t.Id != oldId);
        oldToken.RevokedAt.Should().NotBeNull("the presented token is revoked on rotation");
        oldToken.ReplacedByTokenId.Should().Be(newToken.Id, "the rotation chain must be linked");
        newToken.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task RotateAsync_UnknownToken_ThrowsInvalid()
    {
        var act = () => _sut.RotateAsync("does-not-exist");

        (await act.Should().ThrowAsync<AuthenticationException>())
            .Which.ErrorCode.Should().Be(ErrorCodes.RefreshTokenInvalid);
    }

    [Fact]
    public async Task RotateAsync_ReuseAfterGraceWindow_RevokesEntireActiveFamily()
    {
        var user = SeedUser();
        var first = await _sut.IssueAsync(user);

        // Rotate once: `first` is now revoked and a successor is active.
        var successor = (await _sut.RotateAsync(first)).RefreshToken;

        // Age the revocation past the grace window so the replay reads as genuine theft, not a
        // benign just-rotated retry.
        await AgeRevocationsBeyondGraceAsync();

        // Replay the OLD (revoked) token → reuse detection → uniform 401.
        var reuse = () => _sut.RotateAsync(first);
        (await reuse.Should().ThrowAsync<AuthenticationException>())
            .Which.ErrorCode.Should().Be(ErrorCodes.RefreshTokenInvalid);

        // The theft response revoked the whole active set, so the previously-valid successor
        // can no longer rotate either.
        await ((Func<Task>)(() => _sut.RotateAsync(successor)))
            .Should().ThrowAsync<AuthenticationException>();

        _context.ChangeTracker.Clear();
        (await _context.RefreshTokens.ToListAsync())
            .Should().OnlyContain(t => t.RevokedAt != null, "reuse revokes every active token for the user");
    }

    [Fact]
    public async Task RotateAsync_ReuseWithinGraceWindow_IsBenign_DoesNotRevokeFamily()
    {
        var user = SeedUser();
        var first = await _sut.IssueAsync(user);
        var successor = (await _sut.RotateAsync(first)).RefreshToken; // `first` revoked just now

        // Immediately replaying the just-rotated token is a benign lost-response retry — 401,
        // but WITHOUT revoking the family (it was rotated < grace window ago).
        (await ((Func<Task>)(() => _sut.RotateAsync(first))).Should().ThrowAsync<AuthenticationException>())
            .Which.ErrorCode.Should().Be(ErrorCodes.RefreshTokenInvalid);

        // Proof the family was NOT revoked: the successor is untouched and still rotates.
        (await _sut.RotateAsync(successor)).RefreshToken.Should().NotBeNullOrEmpty();
    }

    /// <summary>Ages every revoked token past the grace window so a replay reads as theft.</summary>
    private async Task AgeRevocationsBeyondGraceAsync()
    {
        _context.ChangeTracker.Clear();
        var revoked = await _context.RefreshTokens.Where(t => t.RevokedAt != null).ToListAsync();
        foreach (var token in revoked)
        {
            token.RevokedAt = DateTime.UtcNow - TimeSpan.FromMinutes(1);
        }
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    [Fact]
    public async Task RotateAsync_ExpiredToken_ThrowsInvalid()
    {
        var user = SeedUser();
        // Issue via a service whose config makes the token born-expired (negative lifetime).
        var expired = await BuildService(new JwtOptions { RefreshTokenExpirationDays = -1 }).IssueAsync(user);

        var act = () => _sut.RotateAsync(expired);

        (await act.Should().ThrowAsync<AuthenticationException>())
            .Which.ErrorCode.Should().Be(ErrorCodes.RefreshTokenInvalid);
    }

    [Fact]
    public async Task RevokeAllForUserAsync_RevokesEveryActiveTokenForThatUser_ButNotOthers()
    {
        var user = SeedUser();
        var other = SeedUser();
        await _sut.IssueAsync(user);
        await _sut.IssueAsync(user);
        var othersPlaintext = await _sut.IssueAsync(other);

        await _sut.RevokeAllForUserAsync(user.Id);

        _context.ChangeTracker.Clear();
        (await _context.RefreshTokens.Where(t => t.UserId == user.Id).ToListAsync())
            .Should().OnlyContain(t => t.RevokedAt != null);
        // A different user's token is untouched and still usable.
        (await _sut.RotateAsync(othersPlaintext)).User.Id.Should().Be(other.Id);
    }
}
