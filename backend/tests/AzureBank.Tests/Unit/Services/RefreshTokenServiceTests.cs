using AzureBank.Api.Services;
using AzureBank.Api.Services.Interfaces;
using System.Security.Cryptography;
using System.Text;
using AzureBank.Api.Services.Implementations;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
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
/// Unit tests for RefreshTokenService, the grant of 06 §4: issuance (hash-at-rest, a fixed 60-minute
/// life), renewal that writes nothing, every row of 06 §4.3's state table, and the two revokes. Runs
/// on the EF InMemory provider (exercises the non-relational fallbacks); what reaches SQL Server is
/// proved in RefreshTokenRotationSqlServerTests.
/// </summary>
public class RefreshTokenServiceTests : IDisposable
{
    private readonly AzureBankDbContext _context;
    private readonly RefreshTokenService _sut;
    private readonly Mock<IAuditService> _audit = new();
    private readonly Mock<ILogger<RefreshTokenService>> _logger = new();

    // What every detector built below logs (06 §6, anomaly 2), readable as lines.
    private readonly RecordingLoggerProvider _detectorLogs = new();
    private readonly LoggerFactory _detectorLoggerFactory;

    // Both held so a second context can join the SAME InMemory database (the fault-injection tests).
    private readonly string _databaseName = Guid.NewGuid().ToString();

    /*
      The name alone is NOT enough to guarantee shared storage. EF caches an internal service
      provider keyed on the options extensions, and the second context differs (it adds an
      interceptor) — so it can land on a different provider, hence a different store, hence an
      EMPTY database. A test on the faulty context would then take the unknown-grant branch and
      pass for the wrong reason.

      An explicit root makes the sharing a property of the fixture instead of a property of EF's
      provider caching, which is internal and free to change.
    */
    private readonly InMemoryDatabaseRoot _databaseRoot = new();

    public RefreshTokenServiceTests()
    {
        _detectorLoggerFactory = new LoggerFactory([_detectorLogs]);
        _context = new AzureBankDbContext(DbOptions());
        _sut = BuildService(_context, new JwtOptions());
    }

    public void Dispose()
    {
        _context.Dispose();
        _detectorLoggerFactory.Dispose();
        GC.SuppressFinalize(this);
    }

    private DbContextOptions<AzureBankDbContext> DbOptions(Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<AzureBankDbContext>()
            .UseInMemoryDatabase(_databaseName, _databaseRoot)
            // The RowVersion concurrency token is SQL-Server-generated; the InMemory provider
            // can't produce it, so downgrade byte[] concurrency tokens exactly as the
            // integration fixture does.
            .ReplaceService<IModelCustomizer, InMemoryTestModelCustomizer>();
        if (interceptor is not null)
        {
            builder.AddInterceptors(interceptor);
        }

        return builder.Options;
    }

    private RefreshTokenService BuildService(AzureBankDbContext context, JwtOptions jwtOptions)
    {
        var httpContextAccessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        httpContextAccessor.HttpContext!.Request.Headers.UserAgent = "xunit/1.0";
        var options = Microsoft.Extensions.Options.Options.Create(jwtOptions);
        return new RefreshTokenService(
            context,
            httpContextAccessor,
            options,
            _logger.Object,
            _audit.Object,
            new RefreshRenewalRateDetector(options, _detectorLoggerFactory.CreateLogger<RefreshRenewalRateDetector>()));
    }

    /// <summary>A service over the SAME database whose every SaveChanges throws.</summary>
    private RefreshTokenService SutOverAContextThatCannotSave(out AzureBankDbContext context)
    {
        context = new AzureBankDbContext(DbOptions(new ThrowingSaveChangesInterceptor()));
        return BuildService(context, new JwtOptions());
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

    /// <summary>Marks the user's single grant revoked, as a revoke would, then forgets the tracking.</summary>
    private async Task RevokeTheGrantAsync(RefreshTokenRevokedReason? reason, DateTime revokedAt)
    {
        _context.ChangeTracker.Clear();
        var grant = await _context.RefreshTokens.SingleAsync();
        grant.RevokedAt = revokedAt;
        grant.RevokedReason = reason;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private void VerifyTheTripwireRowWritten(Times times) =>
        _audit.Verify(a => a.RecordRefusalAsync(
            SecurityEvents.RefreshTokenReuse, AuditOutcome.Refused,
            It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), times);

    private void VerifyLogged(LogLevel level, string fragment, Times times) =>
        _logger.Verify(l => l.Log(
            level,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains(fragment)),
            It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), times);

    // ── Issue ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task IssueAsync_PersistsHashedToken_AndReturnsPlaintext_WithASixtyMinuteLife()
    {
        var user = SeedUser();

        var issued = await _sut.IssueAsync(user);

        issued.RefreshToken.Should().NotBeNullOrEmpty();
        var stored = await _context.RefreshTokens.SingleAsync();
        stored.UserId.Should().Be(user.Id);
        stored.TokenHash.Should().NotBe(issued.RefreshToken, "only the SHA-256 hash may be stored, never the plaintext");
        stored.TokenHash.Should().Be(
            Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(issued.RefreshToken))),
            "the stored value must be exactly SHA-256(plaintext) in base64 — pins the hash-at-rest contract");
        stored.RevokedAt.Should().BeNull();
        stored.RevokedReason.Should().BeNull();
        stored.IsActive.Should().BeTrue();
        stored.ExpiresAt.Should().Be(stored.CreatedAt.AddMinutes(60),
            "the default grant lives 60 minutes from issue, the BFF's absolute cap (06 §4.1)");
        issued.ExpiresAt.Should().Be(stored.ExpiresAt, "login and register hand back the stored expiry, not a recomputed one");
    }

    [Fact]
    public async Task IssueAsync_HonoursTheConfiguredLifetime()
    {
        var user = SeedUser();

        var issued = await BuildService(_context, new JwtOptions { RefreshTokenLifetimeMinutes = 15 }).IssueAsync(user);

        var stored = await _context.RefreshTokens.SingleAsync();
        stored.ExpiresAt.Should().Be(stored.CreatedAt.AddMinutes(15));
        issued.ExpiresAt.Should().Be(stored.ExpiresAt);
    }

    // ── Renew: the active row ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RenewAsync_AnActiveGrant_ReturnsItsUserAndExpiry_AndWritesNothing()
    {
        /*
          06 §4.3's last row: "Active — 200 — Writes: none". Proved by running the renewal on a
          context whose every SaveChanges throws: it could not have succeeded had it tried to write.
          The row itself is then read back unchanged, and the same grant renews a second time.
        */
        var user = SeedUser();
        var issued = await _sut.IssueAsync(user);
        var before = await _context.RefreshTokens.AsNoTracking().SingleAsync();

        var faulty = SutOverAContextThatCannotSave(out var faultyContext);
        await using (faultyContext)
        {
            var first = await faulty.RenewAsync(issued.RefreshToken, DateTime.UtcNow);
            var second = await faulty.RenewAsync(issued.RefreshToken, DateTime.UtcNow);

            first.User.Id.Should().Be(user.Id);
            first.GrantExpiresAt.Should().Be(before.ExpiresAt, "the caller caps the access token at the grant's own expiry");
            second.User.Id.Should().Be(user.Id, "the same grant renews again: a renewal consumes nothing");
            faultyContext.ChangeTracker.Entries().Should().BeEmpty("a renewal tracks nothing it could later save");
        }

        _context.ChangeTracker.Clear();
        var after = await _context.RefreshTokens.AsNoTracking().ToListAsync();
        after.Should().ContainSingle("a renewal mints no successor");
        after[0].RevokedAt.Should().BeNull();
        after[0].ReplacedByTokenId.Should().BeNull();
        after[0].ExpiresAt.Should().Be(before.ExpiresAt, "the grant's expiry is fixed at issue and never extended");
        _audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RenewAsync_FourRenewalsOfOneGrantWithinOneTokenLifetime_AllSucceed_RaiseOneRateEvent_AndWriteNothing()
    {
        /*
          06 §10 O2k through the service, on a context whose every SaveChanges throws: four renewals
          of one grant within one access-token lifetime all succeed, and the fourth raises one
          RefreshRenewalRateHigh (06 §6, anomaly 2). None could have written, and no audit row is
          asked for: the event is a log line only.
        */
        var user = SeedUser();
        var issued = await _sut.IssueAsync(user);
        var grantId = (await _context.RefreshTokens.AsNoTracking().SingleAsync()).Id;
        var receivedAt = DateTime.UtcNow;

        var faulty = SutOverAContextThatCannotSave(out var faultyContext);
        await using (faultyContext)
        {
            for (var i = 0; i < 4; i++)
            {
                (await faulty.RenewAsync(issued.RefreshToken, receivedAt.AddSeconds(i))).User.Id.Should().Be(user.Id);
                if (i == 1)
                {
                    RateEvents().Should().BeEmpty("two renewals are within what the BFF itself sends");
                }
            }

            faultyContext.ChangeTracker.Entries().Should().BeEmpty("a renewal tracks nothing it could later save");
        }

        RateEvents().Should().ContainSingle().Which.Should().Contain(grantId.ToString(),
            "the line names the grant's row id, never the grant");
        RateEvents()[0].Should().NotContain(issued.RefreshToken, "a grant is never logged (06 O2h)");
        _audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RenewAsync_AnEndedSessionsGrantPresentedFourTimes_IsNotCounted()
    {
        // 06 §6 counts 200 answers only. Four tripwire refusals of one grant, within one token
        // lifetime, are four 401s: the tripwire writes its rows, and the detector counts none of them.
        var user = SeedUser();
        var ended = await _sut.IssueAsync(user);
        var revokedAt = DateTime.UtcNow;
        (await _sut.RevokeAsync([ended.RefreshToken], revokedAt)).Should().Be(1);

        for (var i = 1; i <= 4; i++)
        {
            await ((Func<Task>)(() => _sut.RenewAsync(ended.RefreshToken, revokedAt.AddSeconds(i))))
                .Should().ThrowAsync<AuthenticationException>();
        }

        VerifyTheTripwireRowWritten(Times.Exactly(4));
        RateEvents().Should().BeEmpty("only a renewal the grant check accepted is counted (06 §6)");
    }

    [Fact]
    public async Task RenewAsync_AnExpiredGrantPresentedFourTimes_IsNotCounted()
    {
        // The expiry check comes after the revoked one, so counting between the two would count
        // these; 06 F11's floor on the lifetime means the row is backdated instead.
        var user = SeedUser();
        var issued = await _sut.IssueAsync(user);
        _context.ChangeTracker.Clear();
        var grant = await _context.RefreshTokens.SingleAsync();
        grant.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        var receivedAt = DateTime.UtcNow;

        for (var i = 0; i < 4; i++)
        {
            await ((Func<Task>)(() => _sut.RenewAsync(issued.RefreshToken, receivedAt.AddSeconds(i))))
                .Should().ThrowAsync<AuthenticationException>();
        }

        RateEvents().Should().BeEmpty("only a renewal the grant check accepted is counted (06 §6)");
    }

    private List<string> RateEvents() =>
        _detectorLogs.Lines
            .Where(l => l.Level == LogLevel.Warning && l.Message.Contains(SecurityEvents.RefreshRenewalRateHigh))
            .Select(l => l.Message)
            .ToList();

    // ── Renew: the refusals ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RenewAsync_UnknownToken_ThrowsInvalid_AndWritesTheRefusal()
    {
        var act = () => _sut.RenewAsync("does-not-exist", DateTime.UtcNow);

        (await act.Should().ThrowAsync<AuthenticationException>())
            .Which.ErrorCode.Should().Be(ErrorCodes.RefreshTokenInvalid);
        _audit.Verify(a => a.RecordRefusalAsync(
            SecurityEvents.RefreshTokenUnknown, AuditOutcome.Refused,
            null, null, null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RenewAsync_AnEndedSessionsGrant_ReceivedAfterTheRevoke_IsTheTripwire_AndRevokesNothing()
    {
        /*
          06 §4.3 row 2, and F3. The grant of an ENDED session, presented in a request received
          after the revoke: 401, one RefreshTokenReuse log line and audit row — and nothing else.
          Until PR-1 this revoked every token of the user; the second session below is what shows it
          no longer does.
        */
        var user = SeedUser();
        var ended = await _sut.IssueAsync(user);
        var otherSession = await _sut.IssueAsync(user);
        var revokedAt = DateTime.UtcNow;
        (await _sut.RevokeAsync([ended.RefreshToken], revokedAt)).Should().Be(1);

        var replay = () => _sut.RenewAsync(ended.RefreshToken, revokedAt.AddSeconds(1));

        (await replay.Should().ThrowAsync<AuthenticationException>())
            .Which.ErrorCode.Should().Be(ErrorCodes.RefreshTokenInvalid, "the tripwire answers the same uniform 401");
        VerifyTheTripwireRowWritten(Times.Once());
        VerifyLogged(LogLevel.Warning, SecurityEvents.RefreshTokenReuse, Times.Once());

        _context.ChangeTracker.Clear();
        (await _context.RefreshTokens.CountAsync(t => t.UserId == user.Id && t.RevokedAt == null))
            .Should().Be(1, "the tripwire records; it revokes nothing (06 F3)");
        (await _sut.RenewAsync(otherSession.RefreshToken, DateTime.UtcNow)).User.Id
            .Should().Be(user.Id, "the user's other session keeps renewing");
    }

    [Fact]
    public async Task RenewAsync_TheTripwiresRow_IsWrittenEvenWhenTheCallerHangsUp()
    {
        /*
          Replaces the old disconnect case. The row is the evidence, so the tripwire writes it with
          CancellationToken.None: a caller that hangs up must not be able to take it back (06 §4.3).
          The caller's token here is live and cancellable; the write must not have been given it.
        */
        var user = SeedUser();
        var ended = await _sut.IssueAsync(user);
        var revokedAt = DateTime.UtcNow;
        await _sut.RevokeAsync([ended.RefreshToken], revokedAt);
        using var caller = new CancellationTokenSource();

        await ((Func<Task>)(() => _sut.RenewAsync(ended.RefreshToken, revokedAt.AddSeconds(1), caller.Token)))
            .Should().ThrowAsync<AuthenticationException>();

        _audit.Verify(a => a.RecordRefusalAsync(
            SecurityEvents.RefreshTokenReuse, AuditOutcome.Refused,
            user.Id, "RefreshToken", It.IsAny<Guid?>(), null,
            It.Is<CancellationToken>(t => !t.CanBeCanceled)), Times.Once);
    }

    [Fact]
    public async Task RenewAsync_WhenTheTripwiresAuditWriteFails_TheFailureSurfaces_AndNothingIsRevoked()
    {
        /*
          06 §4.3: a failed tripwire audit write answers as the unknown-grant refusal does — the
          exception surfaces and GlobalExceptionHandler turns it into a 500 (ADR-0044's loud
          failure). Here: the exception is the audit's own, not the uniform 401; the log line was
          written before it; and the user's other grant is still active.
        */
        var user = SeedUser();
        var ended = await _sut.IssueAsync(user);
        await _sut.IssueAsync(user);
        var revokedAt = DateTime.UtcNow;
        await _sut.RevokeAsync([ended.RefreshToken], revokedAt);
        _audit.Setup(a => a.RecordRefusalAsync(
                SecurityEvents.RefreshTokenReuse, It.IsAny<AuditOutcome>(),
                It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("audit table unwritable"));

        await ((Func<Task>)(() => _sut.RenewAsync(ended.RefreshToken, revokedAt.AddSeconds(1))))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("audit table unwritable");

        VerifyLogged(LogLevel.Warning, SecurityEvents.RefreshTokenReuse, Times.Once());
        _context.ChangeTracker.Clear();
        (await _context.RefreshTokens.CountAsync(t => t.UserId == user.Id && t.RevokedAt == null))
            .Should().Be(1);
    }

    [Theory]
    [InlineData(0)]    // the same instant: not AFTER the revoke
    [InlineData(-1)]   // received before the revoke, read after it
    public async Task RenewAsync_AnEndedSessionsGrant_ReceivedBeforeTheRevoke_WasInFlight_AndWritesNothing(int secondsAfter)
    {
        var user = SeedUser();
        var ended = await _sut.IssueAsync(user);
        var revokedAt = DateTime.UtcNow;
        await _sut.RevokeAsync([ended.RefreshToken], revokedAt);

        (await ((Func<Task>)(() => _sut.RenewAsync(ended.RefreshToken, revokedAt.AddSeconds(secondsAfter))))
            .Should().ThrowAsync<AuthenticationException>())
            .Which.ErrorCode.Should().Be(ErrorCodes.RefreshTokenInvalid);

        VerifyTheTripwireRowWritten(Times.Never());
        VerifyLogged(LogLevel.Warning, SecurityEvents.RefreshTokenReuse, Times.Never());
        VerifyLogged(LogLevel.Information, "while its renewal was in flight", Times.Once());
    }

    [Theory]
    [InlineData(RefreshTokenRevokedReason.SignOutEverywhere, LogLevel.Information)]
    [InlineData(RefreshTokenRevokedReason.Deployment, LogLevel.Information)]
    [InlineData(RefreshTokenRevokedReason.Incident, LogLevel.Warning)]
    [InlineData(RefreshTokenRevokedReason.ReuseContainment, LogLevel.Warning)]
    [InlineData(null, LogLevel.Information)] // a legacy row, revoked before the column existed
    public async Task RenewAsync_AGrantRevokedForAnyOtherReason_IsRefused_WithoutTheTripwire(
        RefreshTokenRevokedReason? reason, LogLevel level)
    {
        // A lever pulled from outside the session: a live session can learn of it late, innocently,
        // so even a request received long after the revoke is not the tripwire (06 §4.3 row 4).
        var user = SeedUser();
        var issued = await _sut.IssueAsync(user);
        var revokedAt = DateTime.UtcNow.AddMinutes(-5);
        await RevokeTheGrantAsync(reason, revokedAt);

        (await ((Func<Task>)(() => _sut.RenewAsync(issued.RefreshToken, DateTime.UtcNow)))
            .Should().ThrowAsync<AuthenticationException>())
            .Which.ErrorCode.Should().Be(ErrorCodes.RefreshTokenInvalid);

        VerifyTheTripwireRowWritten(Times.Never());
        VerifyLogged(level, $"is revoked ({reason?.ToString() ?? "legacy"})", Times.Once());
    }

    [Fact]
    public async Task RenewAsync_ExpiredGrant_ThrowsInvalid_AndWritesNothing()
    {
        // 06 F11: the option refuses a lifetime under 15 minutes, so the row is backdated instead.
        var user = SeedUser();
        var issued = await _sut.IssueAsync(user);
        _context.ChangeTracker.Clear();
        var grant = await _context.RefreshTokens.SingleAsync();
        grant.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        (await ((Func<Task>)(() => _sut.RenewAsync(issued.RefreshToken, DateTime.UtcNow)))
            .Should().ThrowAsync<AuthenticationException>())
            .Which.ErrorCode.Should().Be(ErrorCodes.RefreshTokenInvalid);
        _audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RenewAsync_AGrantInItsLastSecond_IsRefusedAsExpired_AndWritesNothing()
    {
        /*
          The access token is capped at the grant and its exp is whole seconds, truncated: half a
          second before the grant ends, exp would fall on the second already begun, and a 200 would
          carry a token dead on arrival. So a grant with under a second left is refused like an
          expired one: the uniform 401, no audit row.
        */
        var user = SeedUser();
        var issued = await _sut.IssueAsync(user);
        _context.ChangeTracker.Clear();
        var grant = await _context.RefreshTokens.SingleAsync();
        grant.ExpiresAt = DateTime.UtcNow.AddMilliseconds(500);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        (await ((Func<Task>)(() => _sut.RenewAsync(issued.RefreshToken, DateTime.UtcNow)))
            .Should().ThrowAsync<AuthenticationException>())
            .Which.ErrorCode.Should().Be(ErrorCodes.RefreshTokenInvalid);
        _audit.VerifyNoOtherCalls();
        VerifyLogged(LogLevel.Information, "is expired", Times.Once());

        // The other side of the margin: a few seconds left still renews, so the refusal above is
        // the last second's, not a wider cut off the grant's life.
        grant.ExpiresAt = DateTime.UtcNow.AddSeconds(5);
        _context.RefreshTokens.Update(grant);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        (await _sut.RenewAsync(issued.RefreshToken, DateTime.UtcNow)).User.Id.Should().Be(user.Id);
    }

    [Fact]
    public async Task RenewAsync_AnEndedSessionsGrant_PastItsExpiry_IsStillTheTripwire()
    {
        // Revoked is checked BEFORE expired, as it always was: an expiry check first would hide the
        // tripwire behind a routine refusal once the grant's hour is up.
        var user = SeedUser();
        var ended = await _sut.IssueAsync(user);
        var revokedAt = DateTime.UtcNow.AddMinutes(-2);
        await RevokeTheGrantAsync(RefreshTokenRevokedReason.SessionEnded, revokedAt);
        var grant = await _context.RefreshTokens.SingleAsync();
        grant.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await ((Func<Task>)(() => _sut.RenewAsync(ended.RefreshToken, DateTime.UtcNow)))
            .Should().ThrowAsync<AuthenticationException>();

        VerifyTheTripwireRowWritten(Times.Once());
    }

    // ── Revoke one session's grant ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task RevokeAsync_RevokesOnlyThePresentedGrants_AsSessionEnded_StampedWithTheirReceivedAt()
    {
        var user = SeedUser();
        var a = await _sut.IssueAsync(user);
        var b = await _sut.IssueAsync(user);
        var receivedAt = DateTime.UtcNow.AddSeconds(-3);

        var revoked = await _sut.RevokeAsync([a.RefreshToken, "not-a-grant", ""], receivedAt);

        revoked.Should().Be(1, "an unknown or empty entry revokes nothing and is not an error");
        _context.ChangeTracker.Clear();
        var rows = await _context.RefreshTokens.AsNoTracking().ToListAsync();
        var rowA = rows.Single(r => r.RevokedAt != null);
        rowA.RevokedAt.Should().Be(receivedAt, "RevokedAt is the revoking request's ReceivedAt, never the commit time");
        rowA.RevokedReason.Should().Be(RefreshTokenRevokedReason.SessionEnded);
        rows.Count(r => r.RevokedAt == null).Should().Be(1, "the user's other session is not touched");
        (await _sut.RenewAsync(b.RefreshToken, DateTime.UtcNow)).User.Id.Should().Be(user.Id);
    }

    [Fact]
    public async Task RevokeAsync_Repeated_ChangesNothing()
    {
        // RevokedAt IS NULL in the WHERE: a second revoke of the same grant (an EF retry, a BFF retry
        // after a lost answer) does not move RevokedAt, so the tripwire still compares against the
        // first revoke's arrival.
        var user = SeedUser();
        var a = await _sut.IssueAsync(user);
        var first = DateTime.UtcNow.AddSeconds(-10);
        await _sut.RevokeAsync([a.RefreshToken], first);

        (await _sut.RevokeAsync([a.RefreshToken, a.RefreshToken], DateTime.UtcNow)).Should().Be(0);

        _context.ChangeTracker.Clear();
        var row = await _context.RefreshTokens.AsNoTracking().SingleAsync();
        row.RevokedAt.Should().Be(first);
        row.RevokedReason.Should().Be(RefreshTokenRevokedReason.SessionEnded);
    }

    [Fact]
    public async Task RevokeAsync_OfAGrantAnotherLeverRevoked_KeepsThatLeversReasonAndInstant()
    {
        // The same WHERE, against a different lever: the BFF ending a session whose grant a sign-out
        // everywhere already revoked must not relabel it SessionEnded. That label is what makes a
        // later replay the tripwire, and a grant another lever revoked can innocently come back.
        var user = SeedUser();
        var a = await _sut.IssueAsync(user);
        var signedOutEverywhereAt = DateTime.UtcNow.AddSeconds(-10);
        (await _sut.RevokeAllForUserAsync(user.Id, RefreshTokenRevokedReason.SignOutEverywhere, signedOutEverywhereAt))
            .Should().Be(1);

        (await _sut.RevokeAsync([a.RefreshToken], DateTime.UtcNow)).Should().Be(0);

        _context.ChangeTracker.Clear();
        var row = await _context.RefreshTokens.AsNoTracking().SingleAsync();
        row.RevokedAt.Should().Be(signedOutEverywhereAt);
        row.RevokedReason.Should().Be(RefreshTokenRevokedReason.SignOutEverywhere);
    }

    [Fact]
    public async Task RevokeAsync_WhenTheWriteFails_AnswersServiceUnavailable_SoTheCallerRetries()
    {
        // 06 §4.4, F12: 503, never 500, so the BFF's revoker keeps the grant queued and tries again.
        var user = SeedUser();
        var a = await _sut.IssueAsync(user);
        var faulty = SutOverAContextThatCannotSave(out var faultyContext);
        await using var disposeFaulty = faultyContext;

        var failure = (await ((Func<Task>)(() => faulty.RevokeAsync([a.RefreshToken], DateTime.UtcNow)))
            .Should().ThrowAsync<ServiceUnavailableException>()).Which;

        failure.StatusCode.Should().Be(503);
        failure.ErrorCode.Should().Be(ErrorCodes.ServiceUnavailable);
        failure.Details.Should().ContainKey("retryAfterSeconds");
        _context.ChangeTracker.Clear();
        (await _context.RefreshTokens.AsNoTracking().SingleAsync()).RevokedAt
            .Should().BeNull("the fault fired on the one write the revoke makes");
    }

    // ── Revoke every grant of a user ───────────────────────────────────────────────────────────

    [Fact]
    public async Task RevokeAllForUserAsync_RevokesEveryActiveGrantOfThatUser_WithTheReason_ButNotOthers()
    {
        var user = SeedUser();
        var other = SeedUser();
        await _sut.IssueAsync(user);
        await _sut.IssueAsync(user);
        var othersGrant = await _sut.IssueAsync(other);
        var revokedAt = DateTime.UtcNow.AddSeconds(-1);

        var revoked = await _sut.RevokeAllForUserAsync(user.Id, RefreshTokenRevokedReason.SignOutEverywhere, revokedAt);

        revoked.Should().Be(2);
        _context.ChangeTracker.Clear();
        (await _context.RefreshTokens.Where(t => t.UserId == user.Id).ToListAsync())
            .Should().OnlyContain(t => t.RevokedAt == revokedAt
                && t.RevokedReason == RefreshTokenRevokedReason.SignOutEverywhere);
        // A different user's grant is untouched and still renews.
        (await _sut.RenewAsync(othersGrant.RefreshToken, DateTime.UtcNow)).User.Id.Should().Be(other.Id);
    }

    [Fact]
    public async Task RevokeAllForUserAsync_RaisesThatUsersSessionStampBy1_AndNoOneElses()
    {
        // 06 §5.3: a per-user sign-out raises the stamp with its revoke, which is what ends the user's
        // BFF sessions within one poll. Each sign-out raises it again, even with no grant left to
        // revoke: the stamp, not the revoke, is what reaches a session that has not renewed yet.
        var user = SeedUser();
        var other = SeedUser();
        await _sut.IssueAsync(user);

        await _sut.RevokeAllForUserAsync(user.Id, RefreshTokenRevokedReason.SignOutEverywhere, DateTime.UtcNow);
        await _sut.RevokeAllForUserAsync(user.Id, RefreshTokenRevokedReason.ReuseContainment, DateTime.UtcNow);

        _context.ChangeTracker.Clear();
        (await _context.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id)).SessionStamp.Should().Be(2);
        (await _context.Users.AsNoTracking().SingleAsync(u => u.Id == other.Id)).SessionStamp.Should().Be(0);
    }

    [Fact]
    public async Task RevokeAsync_OfOneSessionsGrant_LeavesTheSessionStampAlone()
    {
        // "Esci" ends one session (06 §4.6). A raised stamp would end every other session of the user.
        var user = SeedUser();
        var grant = await _sut.IssueAsync(user);

        (await _sut.RevokeAsync([grant.RefreshToken], DateTime.UtcNow)).Should().Be(1, "the revoke did happen");

        _context.ChangeTracker.Clear();
        (await _context.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id)).SessionStamp.Should().Be(0);
    }
}
