using AzureBank.Api.Mappers;
using AzureBank.Api.Services.Implementations;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Exceptions;
using AzureBank.Shared.Services.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
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

        _sut = new AuthService(
            _userManagerMock.Object,
            _context,
            _jwtServiceMock.Object,
            _passwordHasherMock.Object,
            _userMapper,
            _accountMapper,
            _loggerMock.Object);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    #region Helper Methods

    private ApplicationUser CreateTestUser(string email = "test@example.com", string azureTag = "testuser")
    {
        return new ApplicationUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            NormalizedEmail = email.ToUpper(),
            UserName = azureTag,
            NormalizedUserName = azureTag.ToUpper(),
            AzureTag = azureTag,
            FirstName = "Test",
            LastName = "User",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString(),
            CreatedAt = DateTime.UtcNow
        };
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
            .Returns("test-jwt-token");

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
        await act.Should()
            .ThrowAsync<ConflictException>()
            .WithMessage("*Email is already registered*");
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
        await act.Should()
            .ThrowAsync<ConflictException>()
            .WithMessage("*AzureTag is already taken*");
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
        await act.Should()
            .ThrowAsync<ConflictException>()
            .WithMessage("*AzureTag is already taken*");
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

        // Assert
        await act.Should()
            .ThrowAsync<BusinessRuleException>()
            .WithMessage("*Registration failed*Password too weak*");
    }

    [Fact(Skip = "Requires SQL Server - Account.RowVersion requires database value generation")]
    public async Task RegisterAsync_CreatesDefaultPrimaryAccount()
    {
        // This test requires SQL Server because Account.RowVersion is a database-generated value
        // InMemory provider doesn't support automatic value generation for byte[] properties
        await Task.CompletedTask;
    }

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

    [Fact]
    public async Task VerifyPinAsync_ValidPin_ReturnsTrue()
    {
        // Arrange
        var user = CreateTestUser();
        user.PinHash = "hashed-pin";

        _userManagerMock
            .Setup(x => x.FindByIdAsync(user.Id.ToString()))
            .ReturnsAsync(user);

        _passwordHasherMock
            .Setup(x => x.VerifyPin(user.PinHash, "123456"))
            .Returns(true);

        // Act
        var result = await _sut.VerifyPinAsync(user.Id, "123456");

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyPinAsync_InvalidPin_ReturnsFalse()
    {
        // Arrange
        var user = CreateTestUser();
        user.PinHash = "hashed-pin";

        _userManagerMock
            .Setup(x => x.FindByIdAsync(user.Id.ToString()))
            .ReturnsAsync(user);

        _passwordHasherMock
            .Setup(x => x.VerifyPin(user.PinHash, "654321"))
            .Returns(false);

        // Act
        var result = await _sut.VerifyPinAsync(user.Id, "654321");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task VerifyPinAsync_UserNotFound_ReturnsFalse()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();

        _userManagerMock
            .Setup(x => x.FindByIdAsync(nonExistentId.ToString()))
            .ReturnsAsync((ApplicationUser?)null);

        // Act
        var result = await _sut.VerifyPinAsync(nonExistentId, "123456");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task VerifyPinAsync_NoPinSet_ReturnsFalse()
    {
        // Arrange
        var user = CreateTestUser();
        user.PinHash = null; // No PIN set

        _userManagerMock
            .Setup(x => x.FindByIdAsync(user.Id.ToString()))
            .ReturnsAsync(user);

        // Act
        var result = await _sut.VerifyPinAsync(user.Id, "123456");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task VerifyPinAsync_EmptyPinHash_ReturnsFalse()
    {
        // Arrange
        var user = CreateTestUser();
        user.PinHash = ""; // Empty PIN hash

        _userManagerMock
            .Setup(x => x.FindByIdAsync(user.Id.ToString()))
            .ReturnsAsync(user);

        // Act
        var result = await _sut.VerifyPinAsync(user.Id, "123456");

        // Assert
        result.Should().BeFalse();
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
