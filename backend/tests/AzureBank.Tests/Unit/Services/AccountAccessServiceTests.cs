using AzureBank.Api.Services.Implementations;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Exceptions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AzureBank.Tests.Unit.Services;

/// <summary>
/// Unit tests for AccountAccessService.
/// Tests account ownership verification logic.
/// </summary>
public class AccountAccessServiceTests : IDisposable
{
    private readonly AzureBankDbContext _context;
    private readonly AccountAccessService _sut;

    public AccountAccessServiceTests()
    {
        var options = new DbContextOptionsBuilder<AzureBankDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _context = new AzureBankDbContext(options);
        _sut = new AccountAccessService(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    #region Helper Methods

    private Account CreateTestAccount(Guid userId, bool isPrimary = true, bool isDeleted = false)
    {
        return new Account
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AccountNumber = $"AB-{Random.Shared.Next(1000, 9999)}-{Random.Shared.Next(1000, 9999)}-{Random.Shared.Next(10, 99)}",
            Name = "Test Account",
            Type = AccountType.Checking,
            Balance = 1000m,
            IsPrimary = isPrimary,
            IsDeleted = isDeleted,
            DeletedAt = isDeleted ? DateTime.UtcNow : null,
            CreatedAt = DateTime.UtcNow,
            RowVersion = [0, 0, 0, 0, 0, 0, 0, 1],
            User = null! // Navigation property not needed for unit tests
        };
    }

    #endregion

    #region GetAccountWithOwnershipCheckAsync Tests

    [Fact]
    public async Task GetAccountWithOwnershipCheckAsync_ValidOwner_ReturnsAccount()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var account = CreateTestAccount(userId);
        _context.Accounts.Add(account);
        await _context.SaveChangesAsync();

        // Act
        var result = await _sut.GetAccountWithOwnershipCheckAsync(account.Id, userId);

        // Assert
        result.Should().NotBeNull();
        result.Id.Should().Be(account.Id);
        result.UserId.Should().Be(userId);
    }

    [Fact]
    public async Task GetAccountWithOwnershipCheckAsync_NonExistentAccount_ThrowsNotFoundException()
    {
        // Arrange
        var nonExistentAccountId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        // Act
        var act = () => _sut.GetAccountWithOwnershipCheckAsync(nonExistentAccountId, userId);

        // Assert
        await act.Should()
            .ThrowAsync<NotFoundException>()
            .WithMessage($"*Account*{nonExistentAccountId}*");
    }

    [Fact]
    public async Task GetAccountWithOwnershipCheckAsync_DeletedAccount_ThrowsNotFoundException()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var deletedAccount = CreateTestAccount(userId, isDeleted: true);
        _context.Accounts.Add(deletedAccount);
        await _context.SaveChangesAsync();

        // Act
        var act = () => _sut.GetAccountWithOwnershipCheckAsync(deletedAccount.Id, userId);

        // Assert
        await act.Should()
            .ThrowAsync<NotFoundException>()
            .WithMessage($"*Account*{deletedAccount.Id}*");
    }

    [Fact]
    public async Task GetAccountWithOwnershipCheckAsync_WrongOwner_ThrowsAuthorizationException()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var account = CreateTestAccount(ownerId);
        _context.Accounts.Add(account);
        await _context.SaveChangesAsync();

        // Act
        var act = () => _sut.GetAccountWithOwnershipCheckAsync(account.Id, otherUserId);

        // Assert
        await act.Should()
            .ThrowAsync<AuthorizationException>()
            .WithMessage("*do not have access*");
    }

    [Fact]
    public async Task GetAccountWithOwnershipCheckAsync_ReturnsCorrectAccountProperties()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var account = CreateTestAccount(userId);
        account.Name = "My Special Account";
        account.Balance = 5000m;
        account.Type = AccountType.Savings;
        _context.Accounts.Add(account);
        await _context.SaveChangesAsync();

        // Act
        var result = await _sut.GetAccountWithOwnershipCheckAsync(account.Id, userId);

        // Assert
        result.Name.Should().Be("My Special Account");
        result.Balance.Should().Be(5000m);
        result.Type.Should().Be(AccountType.Savings);
    }

    [Fact]
    public async Task GetAccountWithOwnershipCheckAsync_MultipleAccounts_ReturnsCorrectOne()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var account1 = CreateTestAccount(userId);
        account1.Name = "Account 1";
        var account2 = CreateTestAccount(userId);
        account2.Name = "Account 2";
        _context.Accounts.AddRange(account1, account2);
        await _context.SaveChangesAsync();

        // Act
        var result = await _sut.GetAccountWithOwnershipCheckAsync(account2.Id, userId);

        // Assert
        result.Id.Should().Be(account2.Id);
        result.Name.Should().Be("Account 2");
    }

    #endregion

    #region ValidateAccountOwnershipAsync Tests

    [Fact]
    public async Task ValidateAccountOwnershipAsync_ValidOwner_ReturnsTrue()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var account = CreateTestAccount(userId);
        _context.Accounts.Add(account);
        await _context.SaveChangesAsync();

        // Act
        var result = await _sut.ValidateAccountOwnershipAsync(account.Id, userId);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateAccountOwnershipAsync_WrongOwner_ReturnsFalse()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var account = CreateTestAccount(ownerId);
        _context.Accounts.Add(account);
        await _context.SaveChangesAsync();

        // Act
        var result = await _sut.ValidateAccountOwnershipAsync(account.Id, otherUserId);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateAccountOwnershipAsync_NonExistentAccount_ReturnsFalse()
    {
        // Arrange
        var nonExistentAccountId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        // Act
        var result = await _sut.ValidateAccountOwnershipAsync(nonExistentAccountId, userId);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateAccountOwnershipAsync_DeletedAccount_ReturnsFalse()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var deletedAccount = CreateTestAccount(userId, isDeleted: true);
        _context.Accounts.Add(deletedAccount);
        await _context.SaveChangesAsync();

        // Act
        var result = await _sut.ValidateAccountOwnershipAsync(deletedAccount.Id, userId);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateAccountOwnershipAsync_DoesNotThrowExceptions()
    {
        // Arrange
        var nonExistentAccountId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        // Act - Should not throw, just return false
        var act = async () => await _sut.ValidateAccountOwnershipAsync(nonExistentAccountId, userId);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ValidateAccountOwnershipAsync_MultipleUsersWithAccounts_ValidatesCorrectly()
    {
        // Arrange
        var user1Id = Guid.NewGuid();
        var user2Id = Guid.NewGuid();
        var account1 = CreateTestAccount(user1Id);
        var account2 = CreateTestAccount(user2Id);
        _context.Accounts.AddRange(account1, account2);
        await _context.SaveChangesAsync();

        // Act & Assert
        (await _sut.ValidateAccountOwnershipAsync(account1.Id, user1Id)).Should().BeTrue();
        (await _sut.ValidateAccountOwnershipAsync(account1.Id, user2Id)).Should().BeFalse();
        (await _sut.ValidateAccountOwnershipAsync(account2.Id, user1Id)).Should().BeFalse();
        (await _sut.ValidateAccountOwnershipAsync(account2.Id, user2Id)).Should().BeTrue();
    }

    #endregion
}
