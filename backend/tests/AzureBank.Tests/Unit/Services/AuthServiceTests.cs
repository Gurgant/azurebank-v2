using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using AzureBank.Api.Mappers;
using AzureBank.Api.Observability;
using AzureBank.Api.Security;
using AzureBank.Api.Services;
using AzureBank.Api.Services.Implementations;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Exceptions;
using AzureBank.Shared.Options;
using AzureBank.Shared.Services.Interfaces;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Identity;
using AzureBank.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Compliance.Classification;
using Microsoft.Extensions.Compliance.Redaction;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace AzureBank.Tests.Unit.Services;

/// <summary>
/// Unit tests for AuthService.
/// Tests login, registration, PIN operations, and user retrieval.
/// </summary>
public class AuthServiceTests : IDisposable
{
    private readonly AzureBankDbContext _context;
    private readonly Mock<UserManager<ApplicationUser>> _userManagerMock;
    private readonly Mock<IJwtService> _jwtServiceMock;
    private readonly Mock<IRefreshTokenService> _refreshTokenServiceMock;
    private readonly Mock<IPasswordHasher> _passwordHasherMock;
    private readonly UserMapper _userMapper;
    private readonly AccountMapper _accountMapper;
    private readonly Mock<ILogger<AuthService>> _loggerMock;
    private readonly Mock<IPinVerifier> _pinVerifierMock;
    private readonly Mock<ILoginTimingEqualizer> _timingEqualizerMock;
    private readonly Mock<IRedactorProvider> _redactorProviderMock;
    private readonly AuthService _sut;

    /// <summary>The expiry the mocked grant carries, so a test can see it reach the response.</summary>
    private static readonly DateTime GrantExpiresAt = new(2026, 9, 28, 13, 0, 0, DateTimeKind.Utc);

    public AuthServiceTests()
    {
        var options = new DbContextOptionsBuilder<AzureBankDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ReplaceService<IModelCustomizer, InMemoryTestModelCustomizer>()
            /*
              RegisterAsync now wraps its writes in a transaction, and InMemory escalates
              TransactionIgnoredWarning to an exception, so without this every registration test
              fails on "Transactions are not supported by the in-memory store" rather than on
              anything it is testing.

              Suppressed HERE rather than guarded with IsRelational() in AuthService — which this
              same file already does twice for ExecuteUpdate — deliberately: a provider branch would
              mean these tests exercise a DIFFERENT path from the one that ships. Suppressed, they
              run the real path and BeginTransactionAsync is simply a no-op.

              Which is exactly what they can and cannot prove. These tests pin the neutral-409 logic;
              they say NOTHING about rollback, because there is no transaction to roll back.
              Atomicity is proved on real SQL Server by RegistrationAtomicitySqlServerTests.
            */
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _context = new AzureBankDbContext(options);

        // Mock UserManager - requires special setup
        var userStoreMock = new Mock<IUserStore<ApplicationUser>>();
        _userManagerMock = new Mock<UserManager<ApplicationUser>>(
            userStoreMock.Object, null!, null!, null!, null!, null!, null!, null!, null!);

        _jwtServiceMock = new Mock<IJwtService>();
        _refreshTokenServiceMock = new Mock<IRefreshTokenService>();
        // Login/register issue a grant; default the mock so LoginResponse/TokenResponse get a
        // non-null value (the grant itself is covered in RefreshTokenServiceTests).
        _refreshTokenServiceMock
            .Setup(x => x.IssueAsync(It.IsAny<ApplicationUser>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssuedGrant("refresh-token-plaintext", GrantExpiresAt));
        _passwordHasherMock = new Mock<IPasswordHasher>();
        _userMapper = new UserMapper();
        _accountMapper = new AccountMapper();
        _loggerMock = new Mock<ILogger<AuthService>>();
        _pinVerifierMock = new Mock<IPinVerifier>();
        _timingEqualizerMock = new Mock<ILoginTimingEqualizer>();

        // The REAL email redactor behind a stub provider: log-content assertions below
        // then prove the exact masked shape that production logging emits. The setup is
        // NARROWED to the Pii classification on purpose — if AuthService ever drifts to a
        // different classification, the stub returns null and the test fails loudly instead
        // of silently keeping the masking green.
        _redactorProviderMock = new Mock<IRedactorProvider>();
        _redactorProviderMock
            .Setup(x => x.GetRedactor(new DataClassificationSet(DataClassifications.Pii)))
            .Returns(new EmailMaskingRedactor());

        // AddToRoleAsync is now RESULT-CHECKED in RegisterAsync (ADR-0037), and this is a loose
        // mock: without a setup it returns null and every happy-path registration test NREs on
        // roleResult.Succeeded rather than on anything it is testing.
        _userManagerMock
            .Setup(x => x.AddToRoleAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()))
            .ReturnsAsync(IdentityResult.Success);

        // The demo off and the system clock, as in every deployment that is not the demo. The
        // tests of the demo's sign-in gate build a second service of their own (ServiceFor).
        _sut = ServiceFor(new DemoOptions(), TimeProvider.System);
    }

    /// <summary>The service under test, on this class's mocks and context, with the demo's settings and the clock it is given.</summary>
    private AuthService ServiceFor(DemoOptions demo, TimeProvider clock) =>
        new(
            _userManagerMock.Object,
            _context,
            _jwtServiceMock.Object,
            _refreshTokenServiceMock.Object,
            _passwordHasherMock.Object,
            _pinVerifierMock.Object,
            _userMapper,
            _accountMapper,
            _timingEqualizerMock.Object,
            _loggerMock.Object,
            _redactorProviderMock.Object,
            new Mock<IAuditService>().Object,
            Options.Create(demo),
            clock);

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    #region Helper Methods

    private ApplicationUser CreateTestUser(string email = "test@example.com", string azureTag = "testuser")
    {
        var id = Guid.NewGuid();
        return new ApplicationUser
        {
            Id = id,
            Email = email,
            NormalizedEmail = email.ToUpper(),
            // Decouple (ADR-0015): Identity's UserName is the immutable user id, not the handle.
            UserName = id.ToString(),
            NormalizedUserName = id.ToString().ToUpperInvariant(),
            AzureTag = azureTag,
            FirstName = "Test",
            LastName = "User",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString(),
            CreatedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Seeds a user into the real InMemory context (so the atomic lockout writers'
    /// InMemory fallback can mutate + persist it) and wires FindByEmailAsync to it.
    /// </summary>
    private ApplicationUser SeedUserInContext(
        int failed = 0, DateTimeOffset? lockoutEnd = null, string email = "lock@example.com")
    {
        var user = CreateTestUser(email, "lockuser");
        user.AccessFailedCount = failed;
        user.LockoutEnd = lockoutEnd;
        user.LockoutEnabled = true; // registered users get this via AllowedForNewUsers
        _context.Users.Add(user);
        _context.SaveChanges();
        _userManagerMock.Setup(x => x.FindByEmailAsync(email)).ReturnsAsync(user);
        return user;
    }

    private async Task<ApplicationUser> ReloadAsync(Guid id)
    {
        _context.ChangeTracker.Clear();
        return await _context.Users.SingleAsync(u => u.Id == id);
    }

    #endregion

    #region Login lockout Tests (ADR-0012)

    [Fact]
    public async Task LoginAsync_WrongPassword_IncrementsFailureCount()
    {
        var user = SeedUserInContext(failed: 0);
        _userManagerMock.Setup(x => x.CheckPasswordAsync(user, "wrong")).ReturnsAsync(false);

        var act = () => _sut.LoginAsync(new LoginRequest { Email = user.Email!, Password = "wrong" });

        await act.Should().ThrowAsync<AuthenticationException>();
        (await ReloadAsync(user.Id)).AccessFailedCount.Should().Be(1);
    }

    [Fact]
    public async Task LoginAsync_WrongPasswordAtThreshold_LocksAccount()
    {
        var user = SeedUserInContext(failed: ValidationRules.MaxLoginAttempts - 1); // one away
        _userManagerMock.Setup(x => x.CheckPasswordAsync(user, "wrong")).ReturnsAsync(false);

        // Still the generic 401 (lock state is never leaked to a guesser)...
        await _sut.Invoking(s => s.LoginAsync(new LoginRequest { Email = user.Email!, Password = "wrong" }))
            .Should().ThrowAsync<AuthenticationException>();

        // ...but the account is now locked.
        var reloaded = await ReloadAsync(user.Id);
        reloaded.LockoutEnd.Should().NotBeNull();
        reloaded.LockoutEnd!.Value.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task LoginAsync_LockedAccount_CorrectPassword_ThrowsAccountLocked()
    {
        var user = SeedUserInContext(lockoutEnd: DateTimeOffset.UtcNow.AddMinutes(10));
        _userManagerMock.Setup(x => x.CheckPasswordAsync(user, "correct")).ReturnsAsync(true);

        var ex = (await _sut.Invoking(s => s.LoginAsync(new LoginRequest { Email = user.Email!, Password = "correct" }))
            .Should().ThrowAsync<AccountLockedException>()).Which;

        ex.StatusCode.Should().Be(429);
        ex.ErrorCode.Should().Be(ErrorCodes.AccountLocked);
        ((int)ex.Details!["retryAfterSeconds"]).Should().BePositive();
        _jwtServiceMock.Verify(x => x.GenerateToken(It.IsAny<ApplicationUser>(), It.IsAny<DateTime?>()), Times.Never);
    }

    [Fact]
    public async Task LoginAsync_LockedAccount_WrongPassword_ReturnsGeneric401_WithoutExtending()
    {
        var lockUntil = DateTimeOffset.UtcNow.AddMinutes(10);
        var user = SeedUserInContext(failed: 0, lockoutEnd: lockUntil);
        _userManagerMock.Setup(x => x.CheckPasswordAsync(user, "wrong")).ReturnsAsync(false);

        // Generic 401 (NOT AccountLockedException) — no enumeration signal for a guesser.
        await _sut.Invoking(s => s.LoginAsync(new LoginRequest { Email = user.Email!, Password = "wrong" }))
            .Should().ThrowAsync<AuthenticationException>();

        // The window is not extended and the counter is not touched while already locked.
        var reloaded = await ReloadAsync(user.Id);
        reloaded.AccessFailedCount.Should().Be(0);
        reloaded.LockoutEnd.Should().BeCloseTo(lockUntil, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task LoginAsync_CorrectPasswordWithPriorFailures_ResetsCounter()
    {
        var user = SeedUserInContext(failed: 3);
        _userManagerMock.Setup(x => x.CheckPasswordAsync(user, "correct")).ReturnsAsync(true);
        _jwtServiceMock.Setup(x => x.GenerateToken(user, null))
            .Returns(new TokenResult("jwt", DateTime.UtcNow.AddMinutes(15)));

        var result = await _sut.LoginAsync(new LoginRequest { Email = user.Email!, Password = "correct" });

        result.Token.AccessToken.Should().Be("jwt");
        (await ReloadAsync(user.Id)).AccessFailedCount.Should().Be(0);
    }

    [Fact]
    public async Task LoginAsync_ExpiredLock_WrongPassword_StartsFreshWindow()
    {
        var user = SeedUserInContext(failed: 0, lockoutEnd: DateTimeOffset.UtcNow.AddMinutes(-1)); // expired
        _userManagerMock.Setup(x => x.CheckPasswordAsync(user, "wrong")).ReturnsAsync(false);

        await _sut.Invoking(s => s.LoginAsync(new LoginRequest { Email = user.Email!, Password = "wrong" }))
            .Should().ThrowAsync<AuthenticationException>();

        var reloaded = await ReloadAsync(user.Id);
        reloaded.AccessFailedCount.Should().Be(1, "an expired lock restarts the window at 1");
        reloaded.LockoutEnd.Should().BeNull();
    }

    [Fact]
    public async Task LoginAsync_ExpiredLock_CorrectPassword_LogsInAndClearsLock()
    {
        var user = SeedUserInContext(failed: 3, lockoutEnd: DateTimeOffset.UtcNow.AddMinutes(-1)); // expired
        _userManagerMock.Setup(x => x.CheckPasswordAsync(user, "correct")).ReturnsAsync(true);
        _jwtServiceMock.Setup(x => x.GenerateToken(user, null))
            .Returns(new TokenResult("jwt", DateTime.UtcNow.AddMinutes(15)));

        var result = await _sut.LoginAsync(new LoginRequest { Email = user.Email!, Password = "correct" });

        result.Token.AccessToken.Should().Be("jwt");
        var reloaded = await ReloadAsync(user.Id);
        reloaded.AccessFailedCount.Should().Be(0);
        reloaded.LockoutEnd.Should().BeNull();
    }

    [Fact]
    public async Task LoginAsync_LockoutDisabledUser_CorrectPassword_LogsInDespiteLockoutEnd()
    {
        // An account exempt from lockout (LockoutEnabled=false) is never treated as
        // locked, even with a future LockoutEnd — matches Identity's IsLockedOutAsync.
        var user = SeedUserInContext(failed: 3, lockoutEnd: DateTimeOffset.UtcNow.AddMinutes(10));
        user.LockoutEnabled = false;
        _context.SaveChanges();
        _userManagerMock.Setup(x => x.CheckPasswordAsync(user, "correct")).ReturnsAsync(true);
        _jwtServiceMock.Setup(x => x.GenerateToken(user, null))
            .Returns(new TokenResult("jwt", DateTime.UtcNow.AddMinutes(15)));

        var result = await _sut.LoginAsync(new LoginRequest { Email = user.Email!, Password = "correct" });

        result.Token.AccessToken.Should().Be("jwt");
    }

    [Fact]
    public async Task LoginAsync_LockoutDisabledUser_WrongPassword_DoesNotLatchLock()
    {
        var user = SeedUserInContext(failed: ValidationRules.MaxLoginAttempts - 1); // one away
        user.LockoutEnabled = false;
        _context.SaveChanges();
        _userManagerMock.Setup(x => x.CheckPasswordAsync(user, "wrong")).ReturnsAsync(false);

        await _sut.Invoking(s => s.LoginAsync(new LoginRequest { Email = user.Email!, Password = "wrong" }))
            .Should().ThrowAsync<AuthenticationException>();

        // The writer skips an exempt account: no lock latched, no count churn.
        var reloaded = await ReloadAsync(user.Id);
        reloaded.LockoutEnd.Should().BeNull("an exempt account never latches a lock");
        reloaded.AccessFailedCount.Should().Be(ValidationRules.MaxLoginAttempts - 1);
    }

    #endregion

    #region LoginAsync Tests

    [Fact]
    public async Task LoginAsync_ValidCredentials_ReturnsLoginResponse()
    {
        // Arrange
        var user = CreateTestUser();
        var request = new LoginRequest
        {
            Email = "test@example.com",
            Password = "Password123!"
        };

        _userManagerMock
            .Setup(x => x.FindByEmailAsync(request.Email))
            .ReturnsAsync(user);

        _userManagerMock
            .Setup(x => x.CheckPasswordAsync(user, request.Password))
            .ReturnsAsync(true);

        var expiresAt = DateTime.UtcNow.AddMinutes(15);
        _jwtServiceMock
            .Setup(x => x.GenerateToken(user, null))
            .Returns(new TokenResult("test-jwt-token", expiresAt));

        // Act
        var result = await _sut.LoginAsync(request);

        // Assert — every field of the token object registration also answers, by value.
        result.Should().NotBeNull();
        result.Token.AccessToken.Should().Be("test-jwt-token");
        result.Token.RefreshToken.Should().Be("refresh-token-plaintext");
        result.Token.RefreshTokenExpiresAt.Should().Be(GrantExpiresAt,
            "the grant's own fixed expiry, which the BFF caps the session at (ADR-0057 §4.1)");
        result.Token.ExpiresAt.Should().Be(expiresAt, "the token's own exp, never recomputed");
        result.Token.TokenType.Should().Be("Bearer");
        result.Token.ExpiresIn.Should().BeInRange(890, 900, "what is left of a 15-minute token");
        result.User.Should().NotBeNull();
        result.User.Email.Should().Be(user.Email);
        result.User.AzureTag.Should().Be(user.AzureTag);
    }

    [Fact]
    public async Task LoginAsync_NonExistentUser_ThrowsAuthenticationException()
    {
        // Arrange
        var request = new LoginRequest
        {
            Email = "nonexistent@example.com",
            Password = "Password123!"
        };

        _userManagerMock
            .Setup(x => x.FindByEmailAsync(request.Email))
            .ReturnsAsync((ApplicationUser?)null);

        // Act
        var act = () => _sut.LoginAsync(request);

        // Assert
        await act.Should()
            .ThrowAsync<AuthenticationException>()
            .WithMessage("Invalid email or password.");
        // The unknown-email path must spend the equalizing verify cost (anti-enumeration,
        // ADR-0012) — this pins that the timing mitigation is actually performed.
        _timingEqualizerMock.Verify(x => x.SpendVerifyCost(request.Password), Times.Once);
    }

    [Fact]
    public async Task LoginAsync_InvalidPassword_ThrowsAuthenticationException()
    {
        // Arrange
        var user = CreateTestUser();
        var request = new LoginRequest
        {
            Email = "test@example.com",
            Password = "WrongPassword!"
        };

        _userManagerMock
            .Setup(x => x.FindByEmailAsync(request.Email))
            .ReturnsAsync(user);

        _userManagerMock
            .Setup(x => x.CheckPasswordAsync(user, request.Password))
            .ReturnsAsync(false);

        // Act
        var act = () => _sut.LoginAsync(request);

        // Assert
        await act.Should()
            .ThrowAsync<AuthenticationException>()
            .WithMessage("Invalid email or password.");
    }

    [Fact]
    public async Task LoginAsync_InvalidPassword_DoesNotRevealUserExists()
    {
        // Arrange - Same error message for non-existent user and wrong password
        var user = CreateTestUser();
        var wrongPasswordRequest = new LoginRequest { Email = "test@example.com", Password = "WrongPassword!" };
        var nonExistentRequest = new LoginRequest { Email = "nonexistent@example.com", Password = "Password123!" };

        _userManagerMock
            .Setup(x => x.FindByEmailAsync(wrongPasswordRequest.Email))
            .ReturnsAsync(user);
        _userManagerMock
            .Setup(x => x.CheckPasswordAsync(user, wrongPasswordRequest.Password))
            .ReturnsAsync(false);
        _userManagerMock
            .Setup(x => x.FindByEmailAsync(nonExistentRequest.Email))
            .ReturnsAsync((ApplicationUser?)null);

        // Act
        var wrongPasswordAct = () => _sut.LoginAsync(wrongPasswordRequest);
        var nonExistentAct = () => _sut.LoginAsync(nonExistentRequest);

        // Assert - Both should throw the same message (prevent user enumeration)
        var wrongPasswordEx = await wrongPasswordAct.Should().ThrowAsync<AuthenticationException>();
        var nonExistentEx = await nonExistentAct.Should().ThrowAsync<AuthenticationException>();

        wrongPasswordEx.Which.Message.Should().Be(nonExistentEx.Which.Message);
    }

    #endregion

    #region The demo's sign-in gate

    /*
      On the public demo, sign-in works for one kind of user: the owner of a copy a visitor has
      claimed, while the copy lives. Anybody else the database holds is answered as an email nobody
      has, before the password is looked at: the same exception, the same password cost spent, no
      failed attempt counted, no token and no grant.

      A second service, with the demo on and a clock the test moves, on this class's mocks and its
      InMemory context, where the pool's rows are. What a refusal looks like through the host, and
      that nothing is counted in the database, is shown on SQL Server (DemoClaimSqlServerTests).
    */

    /// <summary>A password that is right wherever the password is looked at: the mock says so.</summary>
    private const string GatePassword = "Kp7m-Xw2R-hd9G-tQ4n";

    /// <summary>
    /// The instant the gate's clock starts at. Years before the wall clock, so a gate that read the
    /// wall clock would find every copy below long ended.
    /// </summary>
    private static readonly DateTimeOffset GateNow = new(2021, 3, 14, 9, 26, 53, TimeSpan.Zero);

    private AuthService ServiceWithTheDemo(TimeProvider clock, int copyLifetimeHours = 24) =>
        ServiceFor(new DemoOptions { Enabled = true, CopyLifetimeHours = copyLifetimeHours }, clock);

    /// <summary>
    /// A user the context holds and sign-in finds by its email, with two failed attempts already
    /// counted, so that a count that moves either way is seen. Where the password is looked at, it
    /// is right, and a token is minted.
    /// </summary>
    private ApplicationUser SeedGateUser(string name, Guid? copyId, bool hasPassword = true)
    {
        var user = CreateTestUser($"{name}@example.com", name);
        user.DemoCopyId = copyId;
        user.PasswordHash = hasPassword ? "a-password-hash" : null;
        user.AccessFailedCount = 2;
        user.LockoutEnabled = true;
        _context.Users.Add(user);
        _context.SaveChanges();
        _userManagerMock.Setup(x => x.FindByEmailAsync(user.Email!)).ReturnsAsync(user);
        _userManagerMock.Setup(x => x.CheckPasswordAsync(user, GatePassword)).ReturnsAsync(true);
        _jwtServiceMock.Setup(x => x.GenerateToken(user, null))
            .Returns(new TokenResult($"jwt-of-{name}", DateTime.UtcNow.AddMinutes(15)));
        return user;
    }

    /// <summary>A pool row and its owner: free when <paramref name="claimedAt"/> is null.</summary>
    private ApplicationUser SeedCopyOwner(string name, DateTime? claimedAt, bool hasPassword = true)
    {
        var copyId = Guid.CreateVersion7();
        var owner = SeedGateUser(name, copyId, hasPassword);
        _context.DemoCopies.Add(new DemoCopy
        {
            Id = copyId,
            OwnerUserId = owner.Id,
            CreatedAt = GateNow.UtcDateTime.AddDays(-2),
            ClaimedAt = claimedAt,
            ClaimId = claimedAt is null ? null : Guid.CreateVersion7(),
        });
        _context.SaveChanges();
        return owner;
    }

    private static LoginRequest GateRequest(ApplicationUser user) => new() { Email = user.Email!, Password = GatePassword };

    /// <summary>Every line the service logged, as "Level: text".</summary>
    private List<string> LoggedLines() =>
        [.. _loggerMock.Invocations
            .Where(call => call.Method.Name == nameof(ILogger.Log))
            .Select(call => $"{call.Arguments[0]}: {call.Arguments[2]}")];

    private int PasswordChecks() =>
        _userManagerMock.Invocations.Count(call => call.Method.Name == nameof(UserManager<ApplicationUser>.CheckPasswordAsync));

    private int VerifyCostsSpentOn(string password) =>
        _timingEqualizerMock.Invocations.Count(call =>
            call.Method.Name == nameof(ILoginTimingEqualizer.SpendVerifyCost) && (string)call.Arguments[0] == password);

    /// <summary>
    /// Counts <c>azurebank.logins</c> on the API's meter, by outcome, for the sign-ins of the test
    /// that made it and for no others.
    /// </summary>
    /// <remarks>
    /// The counter is one for the process, and other classes sign in while this one runs. A
    /// measurement is heard inside the call that counts it, on the flow of the sign-in it belongs
    /// to, so a marker that flows from a test into its own calls tells its sign-ins from everybody
    /// else's: a test can say "one", where a count of the whole process could only say "at least
    /// one". <see cref="TheLoginsCounted_AreThoseOfTheTestThatListens_AndNoOtherFlows"/> holds it.
    /// </remarks>
    private sealed class LoginOutcomes : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentDictionary<string, long> _counts = new(StringComparer.Ordinal);
        private readonly AsyncLocal<bool> _countedHere = new();

        public LoginOutcomes()
        {
            _countedHere.Value = true;
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == ApiMetrics.MeterName && instrument.Name == "azurebank.logins")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                if (!_countedHere.Value)
                {
                    return;
                }

                foreach (var tag in tags)
                {
                    if (tag.Key == "azurebank.outcome" && tag.Value is string outcome)
                    {
                        _counts.AddOrUpdate(outcome, value, (_, sum) => sum + value);
                    }
                }
            });
            _listener.Start();
        }

        /// <summary>The sign-ins counted so far, by outcome. An outcome that was never counted is not there.</summary>
        public Dictionary<string, long> Counted => new(_counts, StringComparer.Ordinal);

        public void Dispose() => _listener.Dispose();
    }

    private static Dictionary<string, long> Outcomes(params (string Outcome, long Count)[] counted) =>
        counted.ToDictionary(c => c.Outcome, c => c.Count, StringComparer.Ordinal);

    /// <summary>
    /// Signs in as <paramref name="user"/> with the right password and asserts the refusal an
    /// unknown email gets, with nothing of what a known user's attempt leaves behind.
    /// </summary>
    private async Task ShouldBeRefusedAsAnUnknownEmailIsAsync(AuthService service, ApplicationUser user, string reason)
    {
        using var outcomes = new LoginOutcomes();
        var thrown = await Record.ExceptionAsync(() => service.LoginAsync(GateRequest(user)));

        var after = await ReloadAsync(user.Id);
        using (new AssertionScope())
        {
            thrown.Should().BeOfType<AuthenticationException>("the right password signs nobody in past the gate");
            (thrown?.Message).Should().Be("Invalid email or password.");
            ((thrown as AppException)?.ErrorCode).Should().Be(ErrorCodes.InvalidCredentials);

            VerifyCostsSpentOn(GatePassword).Should().Be(
                1, "the password cost a real check would spend is spent, so the refusal takes the time an unknown email's does");
            PasswordChecks().Should().Be(0, "the password is never looked at: the refusal is the same whatever it is");
            after.AccessFailedCount.Should().Be(2, "no failed attempt is counted, and none is cleared");
            after.LockoutEnd.Should().BeNull("and nothing is locked");

            _jwtServiceMock.Invocations.Should().BeEmpty("no token is minted");
            _refreshTokenServiceMock.Invocations.Should().BeEmpty("and no grant is issued");

            // What an operator reads: which user, and which of the four reasons. Never the address.
            LoggedLines().Should().Equal(
                [$"Information: Sign-in refused by the demo gate for user {user.Id} ({reason})"]);

            // And what the logins counter is told: one failed sign-in, as for an unknown email.
            outcomes.Counted.Should().Equal(
                Outcomes(("failed", 1)), "a refusal by the gate is counted as one failed sign-in, and as nothing else");
        }
    }

    [Fact]
    public async Task InDemoMode_AUserOutsideEveryCopy_WithTheCorrectPassword_Is401_LikeAnUnknownEmail()
    {
        var service = ServiceWithTheDemo(new FakeTimeProvider(GateNow));
        var outsider = SeedGateUser("outsider", copyId: null);

        await ShouldBeRefusedAsAnUnknownEmailIsAsync(service, outsider, "OutsideEveryCopy");

        // And beside it, an email nobody has: the two refusals are one.
        _userManagerMock.Setup(x => x.FindByEmailAsync("nobody@example.com")).ReturnsAsync((ApplicationUser?)null);
        using var outcomes = new LoginOutcomes();
        var unknown = await Record.ExceptionAsync(
            () => service.LoginAsync(new LoginRequest { Email = "nobody@example.com", Password = "Another-Pass-1!" }));
        var gated = await Record.ExceptionAsync(() => service.LoginAsync(GateRequest(outsider)));

        using (new AssertionScope())
        {
            unknown.Should().BeOfType<AuthenticationException>("CONTROL: an unknown email is refused");
            gated.Should().BeOfType<AuthenticationException>();
            (gated?.Message).Should().Be(unknown?.Message);
            ((gated as AppException)?.ErrorCode).Should().Be((unknown as AppException)?.ErrorCode);
            ((gated as AppException)?.StatusCode).Should().Be((unknown as AppException)?.StatusCode).And.Be(401);
            VerifyCostsSpentOn("Another-Pass-1!").Should().Be(1, "CONTROL: the unknown email's attempt spends the cost once too");
            outcomes.Counted.Should().Equal(
                Outcomes(("failed", 2)), "the counter is told the same of each: two failed sign-ins, the unknown email's and the gated user's");
        }
    }

    [Fact]
    public async Task InDemoMode_AFreeCopysOwner_Is401()
    {
        var service = ServiceWithTheDemo(new FakeTimeProvider(GateNow));

        // A free copy's owner has no password. This one was given one by something other than a
        // claim, so what refuses it here is that nobody claimed the copy.
        var owner = SeedCopyOwner("freeowner", claimedAt: null);

        await ShouldBeRefusedAsAnUnknownEmailIsAsync(service, owner, "CopyNotClaimed");
    }

    [Theory]
    [InlineData(24)]
    [InlineData(5)]
    public async Task InDemoMode_ACopyAtItsLifetimesEnd_Is401_AndOneSecondBeforeItSignsIn(int lifetimeHours)
    {
        var claimedAt = GateNow.UtcDateTime;
        var end = GateNow.AddHours(lifetimeHours);
        var clock = new FakeTimeProvider(end.AddSeconds(-1));
        var service = ServiceWithTheDemo(clock, lifetimeHours);
        var owner = SeedCopyOwner("liveowner", claimedAt);

        // One second before the copy's end: the owner signs in.
        using var outcomes = new LoginOutcomes();
        var signedIn = await service.LoginAsync(GateRequest(owner));

        using (new AssertionScope())
        {
            signedIn.Token.AccessToken.Should().Be("jwt-of-liveowner");
            signedIn.User.Id.Should().Be(owner.Id);
            PasswordChecks().Should().Be(1, "inside the copy's time the password decides, as on any deployment");
            VerifyCostsSpentOn(GatePassword).Should().Be(0);
            (await ReloadAsync(owner.Id)).AccessFailedCount.Should().Be(0, "a sign-in clears the failed attempts, as it always did");
            outcomes.Counted.Should().Equal(
                Outcomes(("succeeded", 1)), "CONTROL: a sign-in the gate lets through is counted as one that succeeded");
        }

        // At the end exactly, to the second: the copy is over.
        clock.Advance(TimeSpan.FromSeconds(1));
        clock.GetUtcNow().Should().Be(end, "ARRANGE: the clock stands at the claim plus the lifetime");
        var thrown = await Record.ExceptionAsync(() => service.LoginAsync(GateRequest(owner)));

        using (new AssertionScope())
        {
            thrown.Should().BeOfType<AuthenticationException>("a copy lives Demo:CopyLifetimeHours from its claim, and not a second more");
            (thrown?.Message).Should().Be("Invalid email or password.");
            VerifyCostsSpentOn(GatePassword).Should().Be(1);
            PasswordChecks().Should().Be(1, "the one check is the earlier sign-in's: the refusal looked at no password");
            _refreshTokenServiceMock.Invocations.Should().HaveCount(1, "the one grant is the earlier sign-in's");
            LoggedLines().Should().Contain(
                $"Information: Sign-in refused by the demo gate for user {owner.Id} (CopyEnded)");
            outcomes.Counted.Should().Equal(
                Outcomes(("succeeded", 1), ("failed", 1)),
                "the refusal at the copy's end is one failed sign-in, beside the earlier one that succeeded");
        }
    }

    [Fact]
    public async Task InDemoMode_AUserWithNoPassword_InALiveCopy_Is401_AndCountsNoFailedAttempt()
    {
        var service = ServiceWithTheDemo(new FakeTimeProvider(GateNow));

        // One of a claimed, living copy's two contacts: its copy is as live as its owner's, and it
        // has no password, so nobody can sign in as it. Without the gate its attempt would reach
        // the password check, fail there and be counted toward a lock.
        var copyId = Guid.CreateVersion7();
        var owner = SeedGateUser("claimedowner", copyId);
        var contact = SeedGateUser("contact", copyId, hasPassword: false);
        _userManagerMock.Setup(x => x.CheckPasswordAsync(contact, GatePassword)).ReturnsAsync(false);
        _context.DemoCopies.Add(new DemoCopy
        {
            Id = copyId,
            OwnerUserId = owner.Id,
            CreatedAt = GateNow.UtcDateTime.AddDays(-2),
            ClaimedAt = GateNow.UtcDateTime.AddHours(-1),
            ClaimId = Guid.CreateVersion7(),
        });
        _context.SaveChanges();

        await ShouldBeRefusedAsAnUnknownEmailIsAsync(service, contact, "NoPassword");

        // CONTROL: the same copy's owner, who has a password, signs in, so the copy is live and
        // what refused the contact is the missing password.
        var signedIn = await service.LoginAsync(GateRequest(owner));
        signedIn.User.Id.Should().Be(owner.Id);
    }

    // CONTROL of the listener the tests above count with, and not of the gate: green as written.
    // A sign-in counted on a flow that did not come from the test, as another class's is in a
    // whole run, is not this test's, and each listener hears its own.
    [Fact]
    public async Task TheLoginsCounted_AreThoseOfTheTestThatListens_AndNoOtherFlows()
    {
        static void Count(string outcome) =>
            ApiMetrics.Logins.Add(1, new KeyValuePair<string, object?>("azurebank.outcome", outcome));

        using var mine = new LoginOutcomes();

        // As another test would: with a listener of its own, on a flow that is not this one's.
        Task<Dictionary<string, long>> elsewhere;
        using (ExecutionContext.SuppressFlow())
        {
            elsewhere = Task.Run(() =>
            {
                using var theirs = new LoginOutcomes();
                Count("locked");
                return theirs.Counted;
            });
        }

        var theirsCounted = await elsewhere;
        using (new AssertionScope())
        {
            theirsCounted.Should().Equal(
                Outcomes(("locked", 1)), "CONTROL: the other flow's sign-in was counted, and its own listener heard it");
            mine.Counted.Should().BeEmpty("a sign-in counted on another flow is another test's");
        }

        // This test's own, before and after it yields: the marker follows the flow across an await.
        Count("failed");
        await Task.Yield();
        Count("failed");
        mine.Counted.Should().Equal(Outcomes(("failed", 2)));
    }

    // CONTROL: green before this change. With the demo off the gate reads nothing: the same four
    // users reach the password check, which is what decides, as on every deployment that is not the
    // demo. (The mock answers the check for the user without a password too; Identity itself would
    // refuse it there, and count the attempt.)
    [Fact]
    public async Task WithTheDemoOff_TheSameUsersSignIn()
    {
        var service = ServiceFor(new DemoOptions { Enabled = false }, new FakeTimeProvider(GateNow));
        var outsider = SeedGateUser("outsider", copyId: null);
        var freeOwner = SeedCopyOwner("freeowner", claimedAt: null);
        var endedOwner = SeedCopyOwner("endedowner", claimedAt: GateNow.UtcDateTime.AddHours(-24));
        var withoutPassword = SeedCopyOwner("nopassword", claimedAt: GateNow.UtcDateTime.AddHours(-1), hasPassword: false);

        foreach (var user in new[] { outsider, freeOwner, endedOwner, withoutPassword })
        {
            var signedIn = await service.LoginAsync(GateRequest(user));

            signedIn.User.Id.Should().Be(user.Id);
            signedIn.Token.AccessToken.Should().Be($"jwt-of-{user.AzureTag}");
        }

        using (new AssertionScope())
        {
            PasswordChecks().Should().Be(4, "each of the four reached the password check");
            VerifyCostsSpentOn(GatePassword).Should().Be(0, "and none was answered as an unknown email");
            LoggedLines().Should().NotContain(line => line.Contains("demo gate", StringComparison.Ordinal));
        }
    }

    #endregion

    #region RegisterAsync Tests

    [Fact]
    public async Task RegisterAsync_DuplicateEmail_ThrowsConflictException()
    {
        // Arrange
        var existingUser = CreateTestUser("existing@example.com");
        var request = new RegisterRequest
        {
            Email = "existing@example.com",
            Password = "Password123!",
            AzureTag = "newuser",
            FirstName = "New",
            LastName = "User"
        };

        _userManagerMock
            .Setup(x => x.FindByEmailAsync(request.Email))
            .ReturnsAsync(existingUser);

        // Act
        var act = () => _sut.RegisterAsync(request);

        // Assert
        // Enumeration-neutral: generic code, no field-specific message (ADR-0013).
        await act.Should()
            .ThrowAsync<ConflictException>()
            .Where(e => e.ErrorCode == ErrorCodes.RegistrationFailed);
    }

    [Fact]
    public async Task RegisterAsync_DuplicateAzureTag_ThrowsConflictException()
    {
        // Arrange
        var request = new RegisterRequest
        {
            Email = "new@example.com",
            Password = "Password123!",
            AzureTag = "existingtag",
            FirstName = "New",
            LastName = "User"
        };

        // Email doesn't exist
        _userManagerMock
            .Setup(x => x.FindByEmailAsync(request.Email))
            .ReturnsAsync((ApplicationUser?)null);

        // But AzureTag exists in context
        var existingUser = CreateTestUser("other@example.com", "existingtag");
        _context.Users.Add(existingUser);
        await _context.SaveChangesAsync();

        // Act
        var act = () => _sut.RegisterAsync(request);

        // Assert
        // Enumeration-neutral: generic code, no field-specific message (ADR-0013).
        await act.Should()
            .ThrowAsync<ConflictException>()
            .Where(e => e.ErrorCode == ErrorCodes.RegistrationFailed);
    }

    [Fact]
    public async Task RegisterAsync_AzureTagCaseInsensitive_ThrowsConflictException()
    {
        // Arrange
        var request = new RegisterRequest
        {
            Email = "new@example.com",
            Password = "Password123!",
            AzureTag = "EXISTINGTAG", // Uppercase
            FirstName = "New",
            LastName = "User"
        };

        _userManagerMock
            .Setup(x => x.FindByEmailAsync(request.Email))
            .ReturnsAsync((ApplicationUser?)null);

        // Existing tag is lowercase
        var existingUser = CreateTestUser("other@example.com", "existingtag");
        _context.Users.Add(existingUser);
        await _context.SaveChangesAsync();

        // Act
        var act = () => _sut.RegisterAsync(request);

        // Assert
        // Enumeration-neutral: generic code, no field-specific message (ADR-0013).
        await act.Should()
            .ThrowAsync<ConflictException>()
            .Where(e => e.ErrorCode == ErrorCodes.RegistrationFailed);
    }

    [Fact]
    public async Task RegisterAsync_UserManagerCreateFails_ThrowsBusinessRuleException()
    {
        // Arrange
        var request = new RegisterRequest
        {
            Email = "new@example.com",
            Password = "weak",
            AzureTag = "newuser",
            FirstName = "New",
            LastName = "User"
        };

        _userManagerMock
            .Setup(x => x.FindByEmailAsync(request.Email))
            .ReturnsAsync((ApplicationUser?)null);

        _userManagerMock
            .Setup(x => x.CreateAsync(It.IsAny<ApplicationUser>(), request.Password))
            .ReturnsAsync(IdentityResult.Failed(new IdentityError { Description = "Password too weak" }));

        // Act
        var act = () => _sut.RegisterAsync(request);

        // Assert - generic, non-echoing message; Identity's error descriptions are logged
        // server-side only, never returned to the client (ADR-0013).
        await act.Should()
            .ThrowAsync<BusinessRuleException>()
            .WithMessage("Registration could not be completed.");
    }

    [Fact]
    public async Task RegisterAsync_CreateAsyncReturnsDuplicate_ThrowsNeutralConflict()
    {
        // A duplicate that slips past the advisory pre-checks (a race) surfaces as a
        // Duplicate* IdentityResult; it must return the SAME neutral 409 as the pre-check
        // path, with no field-specific leak (ADR-0013).
        var request = new RegisterRequest
        {
            Email = "race@example.com",
            Password = "SecurePass123!",
            AzureTag = "raceuser",
            FirstName = "Race",
            LastName = "User"
        };

        _userManagerMock
            .Setup(x => x.FindByEmailAsync(request.Email))
            .ReturnsAsync((ApplicationUser?)null);
        _userManagerMock
            .Setup(x => x.CreateAsync(It.IsAny<ApplicationUser>(), request.Password))
            .ReturnsAsync(IdentityResult.Failed(new IdentityError
            {
                Code = "DuplicateEmail",
                Description = "Email 'race@example.com' is already taken."
            }));

        var thrown = await ((Func<Task>)(() => _sut.RegisterAsync(request)))
            .Should().ThrowAsync<ConflictException>();
        thrown.Which.ErrorCode.Should().Be(ErrorCodes.RegistrationFailed);
        thrown.Which.Message.Should().NotContainEquivalentOf("already taken");
    }

    [Fact]
    public async Task RegisterAsync_NonDuplicateDbUpdateException_Propagates()
    {
        /*
          INVERTED, and deliberately so. This test used to assert that ANY DbUpdateException from
          CreateAsync became the neutral 409, which is what the catch used to do — and that was the
          defect: DbUpdateException is EF's generic wrapper for anything failing in the update
          pipeline, so a deadlock, a lock or command timeout, a dropped connection or an unrelated
          constraint were all reported to the caller as "these details are taken". Since ADR-0037 the
          catch also sits inside the execution-strategy delegate, so swallowing a transient there
          suppresses the retry the strategy would otherwise perform.

          The exception below carries NO inner SqlException, so it is exactly the shape a
          non-duplicate failure has, and the narrowed predicate must let it through. No transient
          SqlException is built for it: the case is an exception with none inside. A test that
          needs one builds it with Fixtures/SqlErrors, as SqlClient builds its own, through its
          internal factory. (Until 2026-10-04 this said a SqlException has no public constructor
          and the repository avoids building one by reflection. The first half is true; two unit
          test classes built one that way all the same, and the fixture holds their code.)

          The DUPLICATE half of the contract — a real write-time race returning the neutral 409 — is
          proved where a real unique violation can actually be raised:
          RegistrationDuplicateSqlServerTests pins both index names against real SQL Server errors.
        */
        var request = new RegisterRequest
        {
            Email = "race2@example.com",
            Password = "SecurePass123!",
            AzureTag = "raceuser2",
            FirstName = "Race",
            LastName = "Two"
        };

        _userManagerMock
            .Setup(x => x.FindByEmailAsync(request.Email))
            .ReturnsAsync((ApplicationUser?)null);
        _userManagerMock
            .Setup(x => x.CreateAsync(It.IsAny<ApplicationUser>(), request.Password))
            .ThrowsAsync(new DbUpdateException("a deadlock or timeout, not a unique violation"));

        var act = () => _sut.RegisterAsync(request);

        await act.Should().ThrowAsync<DbUpdateException>(
            "a non-duplicate write failure must propagate so the strategy can retry it, rather than "
                + "being reported to the caller as a duplicate");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisterAsync_TheGrantsExpiry_IsNullExactlyWhenTheGrantIs(bool grantFails)
    {
        /*
          The registration's grant is best-effort: it is issued after the transaction commits, and a
          failure there still answers the registration. TokenResponse promises RefreshTokenExpiresAt
          is null exactly when RefreshToken is, and the BFF relies on the pair: a session with no
          grant keeps SessionCreated + 60 and the hard stop at token expiry (ADR-0057 §4.1, F15), so
          an expiry without a grant, or the reverse, would describe a grant that is not there. Both
          halves, so the null below is the failure's and not the path's.
        */
        var request = new RegisterRequest
        {
            Email = "grantless@example.com",
            Password = "SecurePass123!",
            AzureTag = "grantless",
            FirstName = "Grant",
            LastName = "Less"
        };
        _userManagerMock
            .Setup(x => x.FindByEmailAsync(request.Email))
            .ReturnsAsync((ApplicationUser?)null);
        _userManagerMock
            .Setup(x => x.CreateAsync(It.IsAny<ApplicationUser>(), request.Password))
            .ReturnsAsync(IdentityResult.Success);
        _jwtServiceMock
            .Setup(x => x.GenerateToken(It.IsAny<ApplicationUser>(), null))
            .Returns(new TokenResult("test-jwt-token", DateTime.UtcNow.AddMinutes(15)));
        if (grantFails)
        {
            _refreshTokenServiceMock
                .Setup(x => x.IssueAsync(It.IsAny<ApplicationUser>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("the grant's INSERT failed"));
        }

        var result = await _sut.RegisterAsync(request);

        _refreshTokenServiceMock.Verify(
            x => x.IssueAsync(It.IsAny<ApplicationUser>(), It.IsAny<CancellationToken>()), Times.Once,
            "the grant was asked for once, after the registration committed");
        result.Token.AccessToken.Should().Be("test-jwt-token", "the registration itself succeeded");
        if (grantFails)
        {
            result.Token.RefreshToken.Should().BeNull();
            result.Token.RefreshTokenExpiresAt.Should().BeNull("null exactly when the grant is");
        }
        else
        {
            result.Token.RefreshToken.Should().Be("refresh-token-plaintext");
            result.Token.RefreshTokenExpiresAt.Should().Be(GrantExpiresAt);
        }
    }

    #endregion

    #region PII redaction in logs

    // Logs are exported over OTLP (Loki), so a raw email in a log message leaves the
    // process. These tests pin the ONLY two sites that log an email: the value that
    // reaches ILogger must be the masked form and must NOT contain the raw address.

    [Fact]
    public async Task LoginAsync_NonExistentUser_LogsMaskedEmailNotRawPii()
    {
        // Arrange
        var request = new LoginRequest
        {
            Email = "nonexistent@example.com",
            Password = "Password123!"
        };

        _userManagerMock
            .Setup(x => x.FindByEmailAsync(request.Email))
            .ReturnsAsync((ApplicationUser?)null);

        // Act
        var act = () => _sut.LoginAsync(request);

        // Assert
        await act.Should().ThrowAsync<AuthenticationException>();
        VerifyWarningLogged(msg =>
            msg.Contains("n***@example.com") && !msg.Contains("nonexistent@example.com"));
        VerifyRawEmailNeverLogged("nonexistent@example.com");
    }

    [Fact]
    public async Task RegisterAsync_DuplicateEmail_LogsMaskedEmailNotRawPii()
    {
        // Arrange
        var existingUser = CreateTestUser("existing@example.com");
        var request = new RegisterRequest
        {
            Email = "existing@example.com",
            Password = "Password123!",
            AzureTag = "newuser",
            FirstName = "New",
            LastName = "User"
        };

        _userManagerMock
            .Setup(x => x.FindByEmailAsync(request.Email))
            .ReturnsAsync(existingUser);

        // Act
        var act = () => _sut.RegisterAsync(request);

        // Assert
        await act.Should().ThrowAsync<ConflictException>();
        VerifyWarningLogged(msg =>
            msg.Contains("e***@example.com") && !msg.Contains("existing@example.com"));
        VerifyRawEmailNeverLogged("existing@example.com");
    }

    /// <summary>
    /// Asserts exactly one Warning was logged whose FORMATTED message satisfies the
    /// predicate — formatting is where structured properties get rendered, so this is
    /// the exact string that would reach the exporter.
    /// </summary>
    private void VerifyWarningLogged(Func<string, bool> messagePredicate) =>
        _loggerMock.Verify(x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => messagePredicate(state.ToString()!)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

    /// <summary>
    /// Blanket guard: the raw address must not appear in ANY log call at ANY level — a
    /// per-message predicate can pass while a second, unasserted log line leaks the value.
    /// </summary>
    private void VerifyRawEmailNeverLogged(string rawEmail) =>
        _loggerMock.Verify(x => x.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains(rawEmail)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);

    #endregion

    #region GetCurrentUserAsync Tests

    [Fact]
    public async Task GetCurrentUserAsync_ExistingUser_ReturnsUserResponse()
    {
        // Arrange
        var user = CreateTestUser();

        _userManagerMock
            .Setup(x => x.FindByIdAsync(user.Id.ToString()))
            .ReturnsAsync(user);

        // Act
        var result = await _sut.GetCurrentUserAsync(user.Id);

        // Assert
        result.Should().NotBeNull();
        result.Email.Should().Be(user.Email);
        result.AzureTag.Should().Be(user.AzureTag);
        result.FirstName.Should().Be(user.FirstName);
        result.LastName.Should().Be(user.LastName);
    }

    [Fact]
    public async Task GetCurrentUserAsync_NonExistentUser_ThrowsNotFoundException()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();

        _userManagerMock
            .Setup(x => x.FindByIdAsync(nonExistentId.ToString()))
            .ReturnsAsync((ApplicationUser?)null);

        // Act
        var act = () => _sut.GetCurrentUserAsync(nonExistentId);

        // Assert
        await act.Should().ThrowAsync<NotFoundException>();
    }

    #endregion

    #region VerifyPinAsync Tests

    // AuthService.VerifyPinAsync delegates to IPinVerifier (PinService), which
    // owns the PIN verification + attempt-limiting logic and is covered in
    // PinServiceTests. This test only asserts the delegation.
    [Fact]
    public async Task VerifyPinAsync_DelegatesToPinVerifier()
    {
        var userId = Guid.NewGuid();
        _pinVerifierMock
            .Setup(x => x.VerifyPinAsync(userId, "123456"))
            .ReturnsAsync(true);

        var result = await _sut.VerifyPinAsync(userId, "123456");

        result.Should().BeTrue();
        _pinVerifierMock.Verify(x => x.VerifyPinAsync(userId, "123456"), Times.Once);
    }

    #endregion

    #region SetPinAsync Tests

    [Fact]
    public async Task SetPinAsync_ValidRequest_UpdatesUserPin()
    {
        // Arrange
        var user = CreateTestUser();
        // ENROLLING (the fixture's user has no PinHash), so the password is the required proof.
        var request = new SetPinRequest { Pin = "123456", Password = "TestPass123!" };
        var expectedHash = "hashed-new-pin";

        _userManagerMock
            .Setup(x => x.FindByIdAsync(user.Id.ToString()))
            .ReturnsAsync(user);

        _userManagerMock
            .Setup(x => x.CheckPasswordAsync(user, request.Password!))
            .ReturnsAsync(true);

        _passwordHasherMock
            .Setup(x => x.HashPin(request.Pin))
            .Returns(expectedHash);

        _userManagerMock
            .Setup(x => x.UpdateAsync(user))
            .ReturnsAsync(IdentityResult.Success);

        // Act
        await _sut.SetPinAsync(user.Id, request);

        // Assert
        user.PinHash.Should().Be(expectedHash);
        _userManagerMock.Verify(x => x.UpdateAsync(user), Times.Once);
    }

    [Fact]
    public async Task SetPinAsync_WhenEnrolling_TracksTheNoticeBeforeUpdateAsync()
    {
        /*
          THE ORDER, NOT THE ROW. UserManager.UpdateAsync is the save that persists the enrolment
          (Identity saves through this same context), so the owed-notice row must already be
          TRACKED when that call happens — a row added afterwards is discarded with the scope, which
          is exactly how the PinEnrolled audit row was once lost while every mock stayed green.

          This proves the ordering with a callback reading the change tracker at call time. It does
          NOT prove that a row reaches the table: that is SubscriberNoticePersistenceTests, through
          the real host, and the two are deliberately paired (docs/engineering-traps.md: "the writer
          was called" is not evidence that a row exists).
        */
        var user = CreateTestUser();
        var request = new SetPinRequest { Pin = "123456", Password = "TestPass123!" };

        _userManagerMock
            .Setup(x => x.FindByIdAsync(user.Id.ToString()))
            .ReturnsAsync(user);
        _userManagerMock
            .Setup(x => x.CheckPasswordAsync(user, request.Password!))
            .ReturnsAsync(true);
        _passwordHasherMock
            .Setup(x => x.HashPin(request.Pin))
            .Returns("hashed-new-pin");

        var noticesTrackedWhenTheSaveRan = -1;
        _userManagerMock
            .Setup(x => x.UpdateAsync(user))
            .Callback(() => noticesTrackedWhenTheSaveRan = _context.ChangeTracker
                .Entries<SubscriberNotice>()
                .Count(e => e.State == EntityState.Added))
            .ReturnsAsync(IdentityResult.Success);

        await _sut.SetPinAsync(user.Id, request);

        noticesTrackedWhenTheSaveRan.Should().Be(
            1, "the notice must ride the save that persists the enrolment, so it has to be tracked "
               + "BEFORE UpdateAsync runs — after it, nothing saves again and the row is discarded");

        var notice = _context.ChangeTracker.Entries<SubscriberNotice>().Single().Entity;
        notice.UserId.Should().Be(user.Id);
        notice.Event.Should().Be(SecurityEvents.PinEnrolled);
        notice.DeliveredAt.Should().BeNull("nothing in the request renders it");
        notice.DeliveryReceipt.Should().BeNull();
    }

    [Fact]
    public async Task SetPinAsync_WhenChanging_TracksBothRowsBeforeUpdateAsync()
    {
        /*
          THE SAME ORDER FOR THE CHANGE (ADR-0047), and the integration test cannot stand in for it.
          That one only notices when the rows never persist at all; this one fails for the refactor
          that moves both Adds below UpdateAsync and adds a second SaveChangesAsync — the rows would
          then exist, InMemory would go green, and the change would no longer be atomic with them.
        */
        var user = CreateTestUser();
        user.PinHash = "an-existing-pin-hash";
        var request = new SetPinRequest { Pin = "999999", CurrentPin = "123456" };

        _userManagerMock
            .Setup(x => x.FindByIdAsync(user.Id.ToString()))
            .ReturnsAsync(user);
        _pinVerifierMock
            .Setup(x => x.VerifyPinAsync(user.Id, request.CurrentPin!))
            .ReturnsAsync(true);
        _passwordHasherMock
            .Setup(x => x.HashPin(request.Pin))
            .Returns("hashed-new-pin");

        var noticesTracked = -1;
        _userManagerMock
            .Setup(x => x.UpdateAsync(user))
            .Callback(() => noticesTracked = _context.ChangeTracker
                .Entries<SubscriberNotice>()
                .Count(e => e.State == EntityState.Added))
            .ReturnsAsync(IdentityResult.Success);

        await _sut.SetPinAsync(user.Id, request);

        noticesTracked.Should().Be(
            1, "the change's notice rides the save that persists the new hash, so it has to be "
               + "tracked BEFORE UpdateAsync runs");

        var notice = _context.ChangeTracker.Entries<SubscriberNotice>().Single().Entity;
        notice.UserId.Should().Be(user.Id);
        notice.Event.Should().Be(
            SecurityEvents.PinChanged, "a change is not an enrolment and must not borrow its name");
        notice.DeliveredAt.Should().BeNull("nothing in the request renders it");
    }

    [Fact]
    public async Task SetPinAsync_Enrolling_WithoutPassword_NeverTouchesTheHash()
    {
        // The cheapest possible statement of T7's rule: refused BEFORE anything is hashed or
        // written, so a rejected enrolment cannot leave a half-applied credential behind.
        var user = CreateTestUser();

        _userManagerMock
            .Setup(x => x.FindByIdAsync(user.Id.ToString()))
            .ReturnsAsync(user);

        var act = () => _sut.SetPinAsync(user.Id, new SetPinRequest { Pin = "123456" });

        (await act.Should().ThrowAsync<BusinessRuleException>())
            .Which.ErrorCode.Should().Be(ErrorCodes.PasswordRequired);
        user.PinHash.Should().BeNull();
        _userManagerMock.Verify(x => x.UpdateAsync(It.IsAny<ApplicationUser>()), Times.Never);
    }

    [Fact]
    public async Task SetPinAsync_UserNotFound_ThrowsNotFoundException()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();
        var request = new SetPinRequest { Pin = "123456" };

        _userManagerMock
            .Setup(x => x.FindByIdAsync(nonExistentId.ToString()))
            .ReturnsAsync((ApplicationUser?)null);

        // Act
        var act = () => _sut.SetPinAsync(nonExistentId, request);

        // Assert
        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task SetPinAsync_UpdateFails_ThrowsBusinessRuleException()
    {
        // Arrange
        var user = CreateTestUser();
        var request = new SetPinRequest { Pin = "123456", Password = "TestPass123!" };

        _userManagerMock
            .Setup(x => x.FindByIdAsync(user.Id.ToString()))
            .ReturnsAsync(user);

        _userManagerMock
            .Setup(x => x.CheckPasswordAsync(user, request.Password!))
            .ReturnsAsync(true);

        _passwordHasherMock
            .Setup(x => x.HashPin(request.Pin))
            .Returns("hashed-pin");

        _userManagerMock
            .Setup(x => x.UpdateAsync(user))
            .ReturnsAsync(IdentityResult.Failed(new IdentityError { Description = "Update failed" }));

        // Act
        var act = () => _sut.SetPinAsync(user.Id, request);

        // Assert
        await act.Should()
            .ThrowAsync<BusinessRuleException>()
            .WithMessage("*Failed to set PIN*Update failed*");
    }

    #endregion

    #region LogoutAsync Tests

    [Fact]
    public async Task LogoutAsync_RevokesEveryGrantOfTheUser_AsSignOutEverywhere_AtItsReceivedAt()
    {
        // /api/auth/logout keeps its effect — every session of the user — with the reason that says
        // so, and the request's ReceivedAt as RevokedAt (ADR-0057 §4.4). One session ends through
        // revoke.
        var userId = Guid.NewGuid();
        var receivedAt = DateTime.UtcNow.AddSeconds(-2);

        await _sut.LogoutAsync(userId, receivedAt);

        _refreshTokenServiceMock.Verify(
            x => x.RevokeAllForUserAsync(
                userId, RefreshTokenRevokedReason.SignOutEverywhere, receivedAt, It.IsAny<CancellationToken>()),
            Times.Once);
        _refreshTokenServiceMock.Verify(
            x => x.RevokeAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RevokeAsync_RevokesThePresentedGrants_AtItsReceivedAt()
    {
        var receivedAt = DateTime.UtcNow.AddSeconds(-1);
        string[] grants = ["grant-a", "grant-b"];

        await _sut.RevokeAsync(new RevokeRequest { RefreshTokens = grants }, receivedAt);

        _refreshTokenServiceMock.Verify(
            x => x.RevokeAsync(grants, receivedAt, It.IsAny<CancellationToken>()), Times.Once);
        _refreshTokenServiceMock.Verify(
            x => x.RevokeAllForUserAsync(
                It.IsAny<Guid>(), It.IsAny<RefreshTokenRevokedReason>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "ending one session must not touch the user's others");
    }

    #endregion

    #region RefreshAsync Tests

    [Fact]
    public async Task RefreshAsync_MintsAnAccessTokenCappedAtTheGrant_AndHandsBackNoRefreshToken()
    {
        // Arrange - renewal returns the owning user and the grant's expiry, and nothing else.
        var user = CreateTestUser();
        var receivedAt = DateTime.UtcNow;
        var grantExpiresAt = DateTime.UtcNow.AddMinutes(7);
        _refreshTokenServiceMock
            .Setup(x => x.RenewAsync("the-grant", receivedAt, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RenewResult(user, grantExpiresAt));
        _jwtServiceMock
            .Setup(x => x.GenerateToken(user, grantExpiresAt))
            .Returns(new TokenResult("new-access", grantExpiresAt));

        // Act
        var result = await _sut.RefreshAsync(new RefreshRequest { RefreshToken = "the-grant" }, receivedAt);

        // Assert - the access token is minted for the grant's user, capped at the grant's expiry
        // (ADR-0057 §4.1), and the answer carries only it: the grant is not rotated
        // (ADR-0057 §4.3).
        result.AccessToken.Should().Be("new-access");
        result.ExpiresAt.Should().Be(grantExpiresAt);
        typeof(RefreshResponse).GetProperties().Select(p => p.Name)
            .Should().BeEquivalentTo(["AccessToken", "ExpiresAt"]);
        _jwtServiceMock.Verify(x => x.GenerateToken(user, grantExpiresAt), Times.Once);
        _refreshTokenServiceMock.Verify(
            x => x.IssueAsync(It.IsAny<ApplicationUser>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RefreshAsync_InvalidToken_PropagatesAndMintsNoAccessToken()
    {
        // Arrange - the grant service is the single arbiter of validity; a rejection
        // must surface as-is and never reach JWT minting.
        _refreshTokenServiceMock
            .Setup(x => x.RenewAsync("bad", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AuthenticationException("Invalid refresh token.", ErrorCodes.RefreshTokenInvalid));

        // Act
        var act = () => _sut.RefreshAsync(new RefreshRequest { RefreshToken = "bad" }, DateTime.UtcNow);

        // Assert
        (await act.Should().ThrowAsync<AuthenticationException>())
            .Which.ErrorCode.Should().Be(ErrorCodes.RefreshTokenInvalid);
        _jwtServiceMock.Verify(x => x.GenerateToken(It.IsAny<ApplicationUser>(), It.IsAny<DateTime?>()), Times.Never);
    }

    #endregion
}
