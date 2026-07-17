using AzureBank.Api.Services.Implementations;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Exceptions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace AzureBank.Tests.Unit.Services;

/// <summary>
/// Unit tests for UserService.
/// Tests user profile retrieval and recipient lookup operations.
/// </summary>
public class UserServiceTests : IDisposable
{
    private readonly AzureBankDbContext _context;
    private readonly Mock<ILogger<UserService>> _loggerMock;
    private readonly UserService _sut;

    public UserServiceTests()
    {
        var options = new DbContextOptionsBuilder<AzureBankDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _context = new AzureBankDbContext(options);
        _loggerMock = new Mock<ILogger<UserService>>();

        _sut = new UserService(_context, _loggerMock.Object);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    #region Helper Methods

    private ApplicationUser CreateTestUser(string azureTag, string firstName = "Test", string lastName = "User")
    {
        return new ApplicationUser
        {
            Id = Guid.NewGuid(),
            AzureTag = azureTag.ToLower(),
            UserName = azureTag.ToLower(),
            NormalizedUserName = azureTag.ToUpper(),
            Email = $"{azureTag}@test.com",
            NormalizedEmail = $"{azureTag.ToUpper()}@TEST.COM",
            FirstName = firstName,
            LastName = lastName,
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString(),
            CreatedAt = DateTime.UtcNow
        };
    }

    #endregion

    #region GetUserByIdAsync Tests

    [Fact]
    public async Task GetUserByIdAsync_ExistingUser_ReturnsUserResponse()
    {
        // Arrange
        var user = CreateTestUser("testuser", "John", "Doe");
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        var result = await _sut.GetUserByIdAsync(user.Id);

        // Assert
        result.Should().NotBeNull();
        result.UserId.Should().Be(user.Id);
        result.Email.Should().Be(user.Email);
        result.AzureTag.Should().Be(user.AzureTag);
        result.FirstName.Should().Be("John");
        result.LastName.Should().Be("Doe");
    }

    [Fact]
    public async Task GetUserByIdAsync_NonExistentUser_ThrowsNotFoundException()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();

        // Act
        var act = () => _sut.GetUserByIdAsync(nonExistentId);

        // Assert
        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task GetUserByIdAsync_MultipleUsers_ReturnsCorrectUser()
    {
        // Arrange
        var user1 = CreateTestUser("user1", "Alice", "Smith");
        var user2 = CreateTestUser("user2", "Bob", "Jones");
        _context.Users.AddRange(user1, user2);
        await _context.SaveChangesAsync();

        // Act
        var result = await _sut.GetUserByIdAsync(user2.Id);

        // Assert
        result.UserId.Should().Be(user2.Id);
        result.FirstName.Should().Be("Bob");
    }

    #endregion

    #region GetUserByAzureTagAsync Tests

    [Fact]
    public async Task GetUserByAzureTagAsync_ExistingUser_ReturnsLookupResponse()
    {
        // Arrange
        var currentUserId = Guid.NewGuid();
        var targetUser = CreateTestUser("targetuser", "Jane", "Doe");
        _context.Users.Add(targetUser);
        await _context.SaveChangesAsync();

        // Act
        var result = await _sut.GetUserByAzureTagAsync("targetuser", currentUserId);

        // Assert
        result.Should().NotBeNull();
        result.Exists.Should().BeTrue();
        result.AzureTag.Should().Be("targetuser");
        result.DisplayName.Should().Be("Jane D."); // Masked name
    }

    [Fact]
    public async Task GetUserByAzureTagAsync_NonExistentUser_ReturnsNotExists()
    {
        // Arrange
        var currentUserId = Guid.NewGuid();

        // Act
        var result = await _sut.GetUserByAzureTagAsync("nonexistent", currentUserId);

        // Assert
        result.Should().NotBeNull();
        result.Exists.Should().BeFalse();
        result.AzureTag.Should().Be("nonexistent");
        result.DisplayName.Should().BeEmpty();
    }

    [Fact]
    public async Task GetUserByAzureTagAsync_SelfLookup_ReturnsSelfIndicator()
    {
        // Arrange
        var user = CreateTestUser("selfuser", "Self", "User");
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        var result = await _sut.GetUserByAzureTagAsync("selfuser", user.Id);

        // Assert - not a valid transfer recipient, and no name is echoed back (ADR-0014).
        result.Should().NotBeNull();
        result.Exists.Should().BeFalse();
        result.DisplayName.Should().BeEmpty();
    }

    [Fact]
    public async Task GetUserByAzureTagAsync_CaseInsensitiveLookup_ReturnsUser()
    {
        // Arrange
        var currentUserId = Guid.NewGuid();
        var user = CreateTestUser("lowercase", "Test", "User");
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        var result = await _sut.GetUserByAzureTagAsync("LOWERCASE", currentUserId);

        // Assert
        result.Exists.Should().BeTrue();
    }

    [Fact]
    public async Task GetUserByAzureTagAsync_SubstringDoesNotMatch_ReturnsNotExists()
    {
        // Exact-match only: a substring of a real tag must NOT resolve (ADR-0014 — no
        // directory browsing). "smith" must not find "johnsmith".
        var user = CreateTestUser("johnsmith", "John", "Smith");
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var result = await _sut.GetUserByAzureTagAsync("smith", Guid.NewGuid());

        result.Exists.Should().BeFalse();
        result.DisplayName.Should().BeEmpty();
    }

    #endregion
}
