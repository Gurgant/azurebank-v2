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
using Microsoft.EntityFrameworkCore.Storage;
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
    // Both held so a second context can join the SAME InMemory database (the fault-injection test).
    private readonly string _databaseName = Guid.NewGuid().ToString();

    /*
      The name alone is NOT enough to guarantee shared storage. EF caches an internal service
      provider keyed on the options extensions, and the second context differs (it adds an
      interceptor) — so it can land on a different provider, hence a different store, hence an
      EMPTY database. The fault-injection test would then take the unknown-token branch, throw the
      very AuthenticationException it asserts, and pass without the family revoke ever running:
      green for the wrong reason, and blind to the regression it exists to catch.

      An explicit root makes the sharing a property of the fixture instead of a property of EF's
      provider caching, which is internal and free to change.
    */
    private readonly InMemoryDatabaseRoot _databaseRoot = new();

    public RefreshTokenServiceTests()
    {
        var options = new DbContextOptionsBuilder<AzureBankDbContext>()
            .UseInMemoryDatabase(_databaseName, _databaseRoot)
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
    public async Task RotateAsync_ReuseSurvivesAFailingFamilyRevoke_StillRejectsWith401()
    {
        /*
          The 401 is the CONTRACT; the family revoke is a MITIGATION.

          The reuse branch used to await that revoke unguarded, so a transient failure on its one
          write replaced the rejection with a 500 — the wrong answer, and an invitation to RETRY a
          token that had just been detected as stolen. It also broke the uniformity this endpoint
          keeps everywhere else: the concurrency-loss branch returns 401 precisely so a race cannot
          be told apart from a rejection.

          Be honest about the provenance: this was found by READING the path, not by catching it in
          the act. The suspected trigger is contention — the set-based revoke writes the same index
          that concurrent rotations are writing, so a deadlock victim or a command timeout lands
          exactly there — but that timing did NOT reproduce locally (12 rounds x 17 concurrent
          requests, with READ_COMMITTED_SNAPSHOT off to match a fresh CI database), and no CI run
          has been seen failing this way either.

          So the invariant is pinned by FAULT INJECTION rather than by racing. That is the better
          test regardless of whether the race is reproducible: it names the failure mode ("the
          revoke threw") instead of hoping to hit one instance of it, and it stays meaningful for
          every other way that write can fail.
        */
        var user = SeedUser();
        var first = await _sut.IssueAsync(user);
        await _sut.RotateAsync(first);      // `first` is now revoked, with an active successor
        await AgeRevocationsBeyondGraceAsync();  // ...and old enough to read as genuine theft

        // The SAME InMemory database, so the reuse lookup finds the revoked token — but on a
        // context whose every SaveChanges throws, which is the revoke's only write on this path.
        var faultyOptions = new DbContextOptionsBuilder<AzureBankDbContext>()
            .UseInMemoryDatabase(_databaseName, _databaseRoot)
            .ReplaceService<IModelCustomizer, InMemoryTestModelCustomizer>()
            .AddInterceptors(new ThrowingSaveChangesInterceptor())
            .Options;
        await using var faultyContext = new AzureBankDbContext(faultyOptions);

        // The precondition the whole test rests on, asserted rather than assumed: the faulty
        // context can SEE the revoked token. Without this the unknown-token branch would throw the
        // same AuthenticationException with the same ErrorCode and the assertion below would pass
        // having never reached the family revoke — the exact wrong-reason pass that an empty
        // database produces.
        (await faultyContext.RefreshTokens.CountAsync(t => t.UserId == user.Id))
            .Should().Be(2, "the faulty context must share the seeded database, not open an empty one");

        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        accessor.HttpContext!.Request.Headers.UserAgent = "xunit/1.0";
        var logger = new Mock<ILogger<RefreshTokenService>>();
        var faultySut = new RefreshTokenService(
            faultyContext,
            accessor,
            Options.Create(new JwtOptions { RefreshTokenExpirationDays = 7 }),
            logger.Object);

        // The injected failure must not reach the caller: still the uniform rejection the exception
        // handler renders as 401, never the 500 the raw exception would have produced.
        (await ((Func<Task>)(() => faultySut.RotateAsync(first))).Should()
            .ThrowAsync<AuthenticationException>())
            .Which.ErrorCode.Should().Be(ErrorCodes.RefreshTokenInvalid);

        /*
          And the half this test was missing until a reviewer asked whether the fault reaches the
          revoke at all. It does — but nothing here PROVED it, because the reuse branch throws this
          exact exception whether the revoke fails or succeeds. Disabling the interceptor entirely
          left this test green, which is the definition of passing for the wrong reason.

          The error log only happens inside the catch, so it is the one observable that separates
          "the revoke failed and was contained" from "the revoke quietly worked".
        */
        logger.Verify(l => l.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) =>
                state.ToString()!.Contains("RefreshTokenReuseRevokeFailed")),
            It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
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


    /// <summary>
    /// Builds a service over the SAME seeded database but on a context whose every SaveChanges
    /// throws, which is the family revoke's only write on the reuse path.
    /// </summary>
    private (RefreshTokenService Sut, Mock<ILogger<RefreshTokenService>> Logger) FaultyRevokeSut(
        Func<Exception>? fault = null)
    {
        var options = new DbContextOptionsBuilder<AzureBankDbContext>()
            .UseInMemoryDatabase(_databaseName, _databaseRoot)
            .ReplaceService<IModelCustomizer, InMemoryTestModelCustomizer>()
            .AddInterceptors(
                fault is null
                    ? new ThrowingSaveChangesInterceptor()
                    : new ThrowingSaveChangesInterceptor(fault))
            .Options;

        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        accessor.HttpContext!.Request.Headers.UserAgent = "xunit/1.0";
        var logger = new Mock<ILogger<RefreshTokenService>>();

        return (
            new RefreshTokenService(
                new AzureBankDbContext(options),
                accessor,
                Options.Create(new JwtOptions { RefreshTokenExpirationDays = 7 }),
                logger.Object),
            logger);
    }

    [Fact]
    public async Task RotateAsync_WhenTheFamilyRevokeFails_LogsTheSecurityEventAtError()
    {
        /*
          ADR-0034 accepts the failed-revoke residual on the strength of DETECTION: the family stays
          active, so the only thing standing between that and an invisible compromise is a log line
          loud enough to notice.

          It was previously asserted by a comment and by nothing else. The sibling fault-injection
          test passes a Mock<ILogger> and never verifies it, so deleting the LogError call would not
          have failed a single test — the decision would have rested on a claim no gate held.

          This is the gate. If the marker, the level or the user id goes, this fails.
        */
        var user = SeedUser();
        var first = await _sut.IssueAsync(user);
        await _sut.RotateAsync(first);
        await AgeRevocationsBeyondGraceAsync();

        var (faultySut, logger) = FaultyRevokeSut();

        (await ((Func<Task>)(() => faultySut.RotateAsync(first))).Should()
            .ThrowAsync<AuthenticationException>())
            .Which.ErrorCode.Should().Be(ErrorCodes.RefreshTokenInvalid);

        logger.Verify(l => l.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) =>
                state.ToString()!.Contains("RefreshTokenReuseRevokeFailed") &&
                state.ToString()!.Contains(user.Id.ToString())),
            It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    [Fact]
    public async Task RotateAsync_WhenTheCallerDisconnects_PropagatesAndIsNotASecurityEvent()
    {
        /*
          The deliberate carve-out: `catch (Exception ex) when (ex is not OperationCanceledException)`.
          A caller who hung up is not a failed mitigation, and counting it as one would poison the
          very signal ADR-0034 relies on — a dashboard full of cancellations is a dashboard nobody
          reads when the real event arrives.

          Two halves, and the second is the one that would rot quietly: cancellation must escape
          AS cancellation (not be laundered into the uniform 401), and it must NOT be logged under
          the security marker. Widening the filter to `catch (Exception)` breaks both.
        */
        var user = SeedUser();
        var first = await _sut.IssueAsync(user);
        await _sut.RotateAsync(first);
        await AgeRevocationsBeyondGraceAsync();

        var (faultySut, logger) = FaultyRevokeSut(() => new OperationCanceledException());

        await ((Func<Task>)(() => faultySut.RotateAsync(first))).Should()
            .ThrowAsync<OperationCanceledException>();

        logger.Verify(l => l.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) =>
                state.ToString()!.Contains("RefreshTokenReuseRevokeFailed")),
            It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Never);
    }

    [Fact]
    public async Task RotateAsync_WhenTheFamilyRevokeFails_LeavesTheStolenFamilyActive()
    {
        /*
          The residual itself, asserted instead of described. ADR-0021's amendment states it in
          prose — "the attacker's successor stays active until logout or the 7-day expiry" — and
          prose does not fail when it stops being true.

          The direction matters. This is NOT a test that wants the family to stay active; it is a
          test that pins what today's code actually does, so that the day someone makes the revoke
          converge anyway, this goes red and points at the ADR that has to be revisited. A residual
          nobody notices getting fixed is how a document starts lying.
        */
        var user = SeedUser();
        var first = await _sut.IssueAsync(user);
        var successor = (await _sut.RotateAsync(first)).RefreshToken;
        await AgeRevocationsBeyondGraceAsync();

        var (faultySut, _) = FaultyRevokeSut();
        await ((Func<Task>)(() => faultySut.RotateAsync(first))).Should()
            .ThrowAsync<AuthenticationException>();

        // The successor — the token an attacker would be holding — still rotates. That is the
        // exposure the ADR accepts, bounded by logout or expiry and by nothing else.
        _context.ChangeTracker.Clear();
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
