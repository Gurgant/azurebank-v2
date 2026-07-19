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
using AzureBank.Shared.Exceptions;
using AzureBank.Shared.Services.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Compliance.Classification;
using Microsoft.Extensions.Compliance.Redaction;
using Microsoft.Extensions.Logging;
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
    private readonly Mock<IPasswordHasher> _passwordHasherMock;
    private readonly UserMapper _userMapper;
    private readonly AccountMapper _accountMapper;
    private readonly Mock<ILogger<AuthService>> _loggerMock;
    private readonly Mock<IPinVerifier> _pinVerifierMock;
    private readonly Mock<ILoginTimingEqualizer> _timingEqualizerMock;
    private readonly AuthService _sut;

    public AuthServiceTests()
    {
        var options = new DbContextOptionsBuilder<AzureBankDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _context = new AzureBankDbContext(options);

        // Mock UserManager - requires special setup
        var userStoreMock = new Mock<IUserStore<ApplicationUser>>();
        _userManagerMock = new Mock<UserManager<ApplicationUser>>(
            userStoreMock.Object, null!, null!, null!, null!, null!, null!, null!, null!);

        _jwtServiceMock = new Mock<IJwtService>();
        _passwordHasherMock = new Mock<IPasswordHasher>();
        _userMapper = new UserMapper();
        _accountMapper = new AccountMapper();
        _loggerMock = new Mock<ILogger<AuthService>>();
        _pinVerifierMock = new Mock<IPinVerifier>();
        _timingEqualizerMock = new Mock<ILoginTimingEqualizer>();

        // The REAL email redactor behind a stub provider: log-content assertions below
        // then prove the exact masked shape that production logging emits.
        var redactorProviderMock = new Mock<IRedactorProvider>();
        redactorProviderMock
            .Setup(x => x.GetRedactor(It.IsAny<DataClassificationSet>()))
            .Returns(new EmailMaskingRedactor());

        _sut = new AuthService(
            _userManagerMock.Object,
            _context,
            _jwtServiceMock.Object,
            _passwordHasherMock.Object,
            _pinVerifierMock.Object,
            _userMapper,
            _accountMapper,
            _timingEqualizerMock.Object,
            _loggerMock.Object,
            redactorProviderMock.Object);
    }

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
        _jwtServiceMock.Verify(x => x.GenerateToken(It.IsAny<ApplicationUser>()), Times.Never);
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
        _jwtServiceMock.Setup(x => x.GenerateToken(user))
            .Returns(new TokenResult("jwt", DateTime.UtcNow.AddMinutes(15)));

        var result = await _sut.LoginAsync(new LoginRequest { Email = user.Email!, Password = "correct" });

        result.Token.Should().Be("jwt");
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
        _jwtServiceMock.Setup(x => x.GenerateToken(user))
            .Returns(new TokenResult("jwt", DateTime.UtcNow.AddMinutes(15)));

        var result = await _sut.LoginAsync(new LoginRequest { Email = user.Email!, Password = "correct" });

        result.Token.Should().Be("jwt");
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
        _jwtServiceMock.Setup(x => x.GenerateToken(user))
            .Returns(new TokenResult("jwt", DateTime.UtcNow.AddMinutes(15)));

        var result = await _sut.LoginAsync(new LoginRequest { Email = user.Email!, Password = "correct" });

        result.Token.Should().Be("jwt");
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

        _jwtServiceMock
            .Setup(x => x.GenerateToken(user))
            .Returns(new TokenResult("test-jwt-token", DateTime.UtcNow.AddMinutes(15)));

        // Act
        var result = await _sut.LoginAsync(request);

        // Assert
        result.Should().NotBeNull();
        result.Token.Should().Be("test-jwt-token");
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

    #region RegisterAsync Tests

    [Fact(Skip = "Requires SQL Server - Account.RowVersion requires database value generation")]
    public async Task RegisterAsync_ValidRequest_ReturnsRegisterResponse()
    {
        // This test requires SQL Server because Account.RowVersion is a database-generated value
        // InMemory provider doesn't support automatic value generation for byte[] properties
        await Task.CompletedTask;
    }

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
    public async Task RegisterAsync_CreateAsyncRaceThrowsDbUpdateException_ThrowsNeutralConflict()
    {
        // The genuine write-time race: Identity's validators pass but the unique index
        // rejects at SaveChanges. It must be neutralised to the same neutral 409 (ADR-0013).
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
            .ThrowsAsync(new DbUpdateException("unique index violation"));

        var act = () => _sut.RegisterAsync(request);

        await act.Should().ThrowAsync<ConflictException>()
            .Where(e => e.ErrorCode == ErrorCodes.RegistrationFailed);
    }

    [Fact(Skip = "Requires SQL Server - Account.RowVersion requires database value generation")]
    public async Task RegisterAsync_CreatesDefaultPrimaryAccount()
    {
        // This test requires SQL Server because Account.RowVersion is a database-generated value
        // InMemory provider doesn't support automatic value generation for byte[] properties
        await Task.CompletedTask;
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
        var request = new SetPinRequest { Pin = "123456" };
        var expectedHash = "hashed-new-pin";

        _userManagerMock
            .Setup(x => x.FindByIdAsync(user.Id.ToString()))
            .ReturnsAsync(user);

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
        var request = new SetPinRequest { Pin = "123456" };

        _userManagerMock
            .Setup(x => x.FindByIdAsync(user.Id.ToString()))
            .ReturnsAsync(user);

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
    public async Task LogoutAsync_ValidUserId_CompletesWithoutError()
    {
        // Arrange
        var userId = Guid.NewGuid();

        // Act - Should not throw
        var act = async () => await _sut.LogoutAsync(userId);

        // Assert
        await act.Should().NotThrowAsync();
    }

    #endregion
}
