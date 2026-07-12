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

        // Assert
        result.Should().NotBeNull();
        result.Exists.Should().BeFalse(); // Treated as not found for transfer purposes
        result.DisplayName.Should().Be("This is your own account");
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

    #endregion

    #region SearchUsersAsync Tests

    [Fact]
    public async Task SearchUsersAsync_MatchingQuery_ReturnsMatchingUsers()
    {
        // Arrange
        var excludeUserId = Guid.NewGuid();
        var user1 = CreateTestUser("johnsmith", "John", "Smith");
        var user2 = CreateTestUser("johndoe", "John", "Doe");
        var user3 = CreateTestUser("janedoe", "Jane", "Doe");
        _context.Users.AddRange(user1, user2, user3);
        await _context.SaveChangesAsync();

        // Act
        var result = await _sut.SearchUsersAsync("john", excludeUserId);

        // Assert
        result.Should().HaveCount(2);
        result.Should().Contain(r => r.AzureTag == "johnsmith");
        result.Should().Contain(r => r.AzureTag == "johndoe");
    }

    [Fact]
    public async Task SearchUsersAsync_NoMatches_ReturnsEmptyList()
    {
        // Arrange
        var excludeUserId = Guid.NewGuid();
        var user = CreateTestUser("testuser", "Test", "User");
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        var result = await _sut.SearchUsersAsync("xyz", excludeUserId);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchUsersAsync_ExcludesCurrentUser()
    {
        // Arrange
        var currentUser = CreateTestUser("john", "John", "Self");
        var otherUser = CreateTestUser("johnny", "Johnny", "Other");
        _context.Users.AddRange(currentUser, otherUser);
        await _context.SaveChangesAsync();

        // Act
        var result = await _sut.SearchUsersAsync("john", currentUser.Id);

        // Assert
        result.Should().HaveCount(1);
        result.First().AzureTag.Should().Be("johnny");
    }

    [Fact]
    public async Task SearchUsersAsync_QueryTooShort_ReturnsEmptyList()
    {
        // Arrange
        var excludeUserId = Guid.NewGuid();
        var user = CreateTestUser("ab", "Ab", "User");
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act - Query is only 1 character
        var result = await _sut.SearchUsersAsync("a", excludeUserId);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchUsersAsync_EmptyQuery_ReturnsEmptyList()
    {
        // Arrange
        var excludeUserId = Guid.NewGuid();
        var user = CreateTestUser("testuser", "Test", "User");
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        var result = await _sut.SearchUsersAsync("", excludeUserId);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchUsersAsync_NullQuery_ReturnsEmptyList()
    {
        // Arrange
        var excludeUserId = Guid.NewGuid();
        var user = CreateTestUser("testuser", "Test", "User");
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        var result = await _sut.SearchUsersAsync(null!, excludeUserId);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchUsersAsync_LimitsResults()
    {
        // Arrange
        var excludeUserId = Guid.NewGuid();
        // Create 15 users matching "user"
        for (int i = 0; i < 15; i++)
        {
            var user = CreateTestUser($"user{i:D2}", $"User{i}", "Test");
            _context.Users.Add(user);
        }
        await _context.SaveChangesAsync();

        // Act
        var result = await _sut.SearchUsersAsync("user", excludeUserId);

        // Assert - Should be limited to 10
        result.Should().HaveCount(10);
    }

    [Fact]
    public async Task SearchUsersAsync_CaseInsensitive()
    {
        // Arrange
        var excludeUserId = Guid.NewGuid();
        var user = CreateTestUser("johnsmith", "John", "Smith");
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        var result = await _sut.SearchUsersAsync("JOHN", excludeUserId);

        // Assert
        result.Should().HaveCount(1);
        result.First().AzureTag.Should().Be("johnsmith");
    }

    [Fact]
    public async Task SearchUsersAsync_ReturnsDisplayNameMasked()
    {
        // Arrange
        var excludeUserId = Guid.NewGuid();
        var user = CreateTestUser("johnsmith", "John", "Smith");
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        var result = await _sut.SearchUsersAsync("john", excludeUserId);

        // Assert
        result.Should().HaveCount(1);
        result.First().DisplayName.Should().Be("John S."); // First name + last initial
    }

    #endregion
}
