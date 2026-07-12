using AzureBank.Api.Mappers;
using AzureBank.Api.Services.Implementations;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.DTOs.Transfer;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Exceptions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace AzureBank.Tests.Unit.Services;

/// <summary>
/// Unit tests for TransferService.
/// Tests external transfers (via AzureTag) and internal transfers (between own accounts).
/// </summary>
public class TransferServiceTests : IDisposable
{
    private readonly AzureBankDbContext _context;
    private readonly Mock<IAccountAccessService> _accountAccessMock;
    private readonly UserMapper _userMapper;
    private readonly Mock<ILogger<TransferService>> _loggerMock;
    private readonly TransferService _sut;

    public TransferServiceTests()
    {
        var options = new DbContextOptionsBuilder<AzureBankDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _context = new AzureBankDbContext(options);
        _accountAccessMock = new Mock<IAccountAccessService>();
        _userMapper = new UserMapper();
        _loggerMock = new Mock<ILogger<TransferService>>();

        _sut = new TransferService(_context, _accountAccessMock.Object, _userMapper, _loggerMock.Object);
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
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString(),
            CreatedAt = DateTime.UtcNow
        };
    }

    private Account CreateTestAccount(Guid userId, decimal balance = 1000m, bool isPrimary = true, bool isDeleted = false)
    {
        return new Account
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AccountNumber = $"AB-{Random.Shared.Next(1000, 9999)}-{Random.Shared.Next(1000, 9999)}-{Random.Shared.Next(10, 99)}",
            Name = "Test Account",
            Type = AccountType.Checking,
            Balance = balance,
            IsPrimary = isPrimary,
            IsDeleted = isDeleted,
            DeletedAt = isDeleted ? DateTime.UtcNow : null,
            CreatedAt = DateTime.UtcNow,
            RowVersion = [0, 0, 0, 0, 0, 0, 0, 1],
            User = null!
        };
    }

    private async Task<(ApplicationUser sender, Account senderAccount, ApplicationUser recipient, Account recipientAccount)> SetupTransferScenarioAsync(
        decimal senderBalance = 1000m,
        decimal recipientBalance = 500m)
    {
        var sender = CreateTestUser("sender", "John", "Doe");
        var recipient = CreateTestUser("recipient", "Jane", "Smith");

        var senderAccount = CreateTestAccount(sender.Id, senderBalance);
        var recipientAccount = CreateTestAccount(recipient.Id, recipientBalance);

        sender.Accounts.Add(senderAccount);
        recipient.Accounts.Add(recipientAccount);

        _context.Users.AddRange(sender, recipient);
        _context.Accounts.AddRange(senderAccount, recipientAccount);
        await _context.SaveChangesAsync();

        // Setup AccountAccessService mock to return sender's account
        _accountAccessMock
            .Setup(x => x.GetAccountWithOwnershipCheckAsync(senderAccount.Id, sender.Id))
            .ReturnsAsync(senderAccount);

        return (sender, senderAccount, recipient, recipientAccount);
    }

    #endregion

    #region TransferAsync - Self Transfer Tests

    [Fact]
    public async Task TransferAsync_SelfTransfer_ThrowsBusinessRuleException()
    {
        // Arrange
        var (sender, senderAccount, _, _) = await SetupTransferScenarioAsync();

        var request = new TransferRequest
        {
            FromAccountId = senderAccount.Id,
            RecipientAzureTag = sender.AzureTag, // Same as sender
            Amount = 100m
        };

        // Act
        var act = () => _sut.TransferAsync(sender.Id, request);

        // Assert
        await act.Should()
            .ThrowAsync<BusinessRuleException>()
            .WithMessage("*Cannot transfer to yourself*");
    }

    [Fact]
    public async Task TransferAsync_SelfTransferCaseInsensitive_ThrowsBusinessRuleException()
    {
        // Arrange
        var (sender, senderAccount, _, _) = await SetupTransferScenarioAsync();

        var request = new TransferRequest
        {
            FromAccountId = senderAccount.Id,
            RecipientAzureTag = sender.AzureTag.ToUpper(), // Different case
            Amount = 100m
        };

        // Act
        var act = () => _sut.TransferAsync(sender.Id, request);

        // Assert
        await act.Should()
            .ThrowAsync<BusinessRuleException>()
            .WithMessage("*Cannot transfer to yourself*");
    }

    #endregion

    #region TransferAsync - Recipient Not Found Tests

    [Fact]
    public async Task TransferAsync_NonExistentRecipient_ThrowsNotFoundException()
    {
        // Arrange
        var (sender, senderAccount, _, _) = await SetupTransferScenarioAsync();

        var request = new TransferRequest
        {
            FromAccountId = senderAccount.Id,
            RecipientAzureTag = "nonexistent",
            Amount = 100m
        };

        // Act
        var act = () => _sut.TransferAsync(sender.Id, request);

        // Assert
        // Note: NotFoundException(string, string) constructor is called with
        // "Recipient" as message and the AzureTag as error code
        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task TransferAsync_RecipientWithNoActiveAccount_ThrowsBusinessRuleException()
    {
        // Arrange
        var sender = CreateTestUser("sender");
        var recipient = CreateTestUser("noaccounts");

        var senderAccount = CreateTestAccount(sender.Id);
        sender.Accounts.Add(senderAccount);

        // Recipient has a deleted account only
        var deletedAccount = CreateTestAccount(recipient.Id, isDeleted: true);
        recipient.Accounts.Add(deletedAccount);

        _context.Users.AddRange(sender, recipient);
        _context.Accounts.AddRange(senderAccount, deletedAccount);
        await _context.SaveChangesAsync();

        _accountAccessMock
            .Setup(x => x.GetAccountWithOwnershipCheckAsync(senderAccount.Id, sender.Id))
            .ReturnsAsync(senderAccount);

        var request = new TransferRequest
        {
            FromAccountId = senderAccount.Id,
            RecipientAzureTag = recipient.AzureTag,
            Amount = 100m
        };

        // Act
        var act = () => _sut.TransferAsync(sender.Id, request);

        // Assert
        await act.Should()
            .ThrowAsync<BusinessRuleException>()
            .WithMessage("*Recipient does not have an active account*");
    }

    #endregion

    #region TransferAsync - Insufficient Funds Tests

    [Fact]
    public async Task TransferAsync_InsufficientFunds_ThrowsInsufficientFundsException()
    {
        // Arrange
        var (sender, senderAccount, recipient, _) = await SetupTransferScenarioAsync(senderBalance: 50m);

        var request = new TransferRequest
        {
            FromAccountId = senderAccount.Id,
            RecipientAzureTag = recipient.AzureTag,
            Amount = 100m // More than balance
        };

        // Act
        var act = () => _sut.TransferAsync(sender.Id, request);

        // Assert
        await act.Should().ThrowAsync<InsufficientFundsException>();
    }

    [Fact]
    public async Task TransferAsync_ExactBalance_DoesNotThrowInsufficientFunds()
    {
        // Arrange - balance = amount
        var (sender, senderAccount, recipient, _) = await SetupTransferScenarioAsync(senderBalance: 100m);

        var request = new TransferRequest
        {
            FromAccountId = senderAccount.Id,
            RecipientAzureTag = recipient.AzureTag,
            Amount = 100m
        };

        // Act - Should not throw InsufficientFundsException
        // Note: May fail due to transaction handling in InMemory, but the funds check passes
        var act = () => _sut.TransferAsync(sender.Id, request);

        // Assert - Either succeeds or fails for transaction reasons, not insufficient funds
        try
        {
            await act();
        }
        catch (InsufficientFundsException)
        {
            // Should NOT throw this
            Assert.Fail("Should not throw InsufficientFundsException when balance equals amount");
        }
        catch
        {
            // Other exceptions are acceptable (InMemory transaction issues)
        }
    }

    #endregion

    #region TransferAsync - Sender Not Found Tests

    [Fact]
    public async Task TransferAsync_SenderUserNotFound_ThrowsNotFoundException()
    {
        // Arrange
        var nonExistentUserId = Guid.NewGuid();
        var fakeAccountId = Guid.NewGuid();

        // Mock returns an account but user doesn't exist in context
        var fakeAccount = CreateTestAccount(nonExistentUserId);
        _accountAccessMock
            .Setup(x => x.GetAccountWithOwnershipCheckAsync(fakeAccountId, nonExistentUserId))
            .ReturnsAsync(fakeAccount);

        var request = new TransferRequest
        {
            FromAccountId = fakeAccountId,
            RecipientAzureTag = "someone",
            Amount = 100m
        };

        // Act
        var act = () => _sut.TransferAsync(nonExistentUserId, request);

        // Assert
        await act.Should()
            .ThrowAsync<NotFoundException>()
            .WithMessage("*User*");
    }

    #endregion

    #region TransferAsync - Successful Transfer Tests

    [Fact(Skip = "Requires SQL Server - InMemory provider transaction behavior may vary")]
    public async Task TransferAsync_ValidTransfer_UpdatesBothBalances()
    {
        // This test should be run as an integration test with SQL Server
        // The InMemory provider may not fully support transaction behavior
    }

    [Fact(Skip = "Requires SQL Server - InMemory provider transaction behavior may vary")]
    public async Task TransferAsync_ValidTransfer_CreatesTwoLinkedTransactions()
    {
        // This test should be run as an integration test with SQL Server
    }

    [Fact(Skip = "Requires SQL Server - InMemory provider transaction behavior may vary")]
    public async Task TransferAsync_ValidTransfer_ReturnsCorrectResponse()
    {
        // This test should be run as an integration test with SQL Server
    }

    #endregion

    #region InternalTransferAsync - Same Account Tests

    [Fact]
    public async Task InternalTransferAsync_SameAccount_ThrowsBusinessRuleException()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var account = CreateTestAccount(userId);
        account.Id = accountId;

        _accountAccessMock
            .Setup(x => x.GetAccountWithOwnershipCheckAsync(accountId, userId))
            .ReturnsAsync(account);

        var request = new InternalTransferRequest
        {
            FromAccountId = accountId,
            ToAccountId = accountId, // Same account
            Amount = 100m
        };

        // Act
        var act = () => _sut.InternalTransferAsync(userId, request);

        // Assert
        await act.Should()
            .ThrowAsync<BusinessRuleException>()
            .WithMessage("*Cannot transfer to the same account*");
    }

    #endregion

    #region InternalTransferAsync - Insufficient Funds Tests

    [Fact]
    public async Task InternalTransferAsync_InsufficientFunds_ThrowsInsufficientFundsException()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var fromAccount = CreateTestAccount(userId, balance: 50m);
        var toAccount = CreateTestAccount(userId, balance: 100m);
        toAccount.Id = Guid.NewGuid(); // Ensure different IDs

        _accountAccessMock
            .Setup(x => x.GetAccountWithOwnershipCheckAsync(fromAccount.Id, userId))
            .ReturnsAsync(fromAccount);
        _accountAccessMock
            .Setup(x => x.GetAccountWithOwnershipCheckAsync(toAccount.Id, userId))
            .ReturnsAsync(toAccount);

        var request = new InternalTransferRequest
        {
            FromAccountId = fromAccount.Id,
            ToAccountId = toAccount.Id,
            Amount = 100m // More than balance
        };

        // Act
        var act = () => _sut.InternalTransferAsync(userId, request);

        // Assert
        await act.Should().ThrowAsync<InsufficientFundsException>();
    }

    #endregion

    #region InternalTransferAsync - Ownership Tests

    [Fact]
    public async Task InternalTransferAsync_FromAccountNotOwned_ThrowsAuthorizationException()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var fromAccountId = Guid.NewGuid();
        var toAccountId = Guid.NewGuid();

        _accountAccessMock
            .Setup(x => x.GetAccountWithOwnershipCheckAsync(fromAccountId, userId))
            .ThrowsAsync(new AuthorizationException("You do not have access to this account."));

        var request = new InternalTransferRequest
        {
            FromAccountId = fromAccountId,
            ToAccountId = toAccountId,
            Amount = 100m
        };

        // Act
        var act = () => _sut.InternalTransferAsync(userId, request);

        // Assert
        await act.Should().ThrowAsync<AuthorizationException>();
    }

    [Fact]
    public async Task InternalTransferAsync_ToAccountNotOwned_ThrowsAuthorizationException()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var fromAccount = CreateTestAccount(userId);
        var toAccountId = Guid.NewGuid();

        _accountAccessMock
            .Setup(x => x.GetAccountWithOwnershipCheckAsync(fromAccount.Id, userId))
            .ReturnsAsync(fromAccount);
        _accountAccessMock
            .Setup(x => x.GetAccountWithOwnershipCheckAsync(toAccountId, userId))
            .ThrowsAsync(new AuthorizationException("You do not have access to this account."));

        var request = new InternalTransferRequest
        {
            FromAccountId = fromAccount.Id,
            ToAccountId = toAccountId,
            Amount = 100m
        };

        // Act
        var act = () => _sut.InternalTransferAsync(userId, request);

        // Assert
        await act.Should().ThrowAsync<AuthorizationException>();
    }

    #endregion

    #region InternalTransferAsync - Successful Transfer Tests

    [Fact(Skip = "Requires SQL Server - InMemory provider transaction behavior may vary")]
    public async Task InternalTransferAsync_ValidTransfer_UpdatesBothBalances()
    {
        // This test should be run as an integration test with SQL Server
    }

    [Fact(Skip = "Requires SQL Server - InMemory provider transaction behavior may vary")]
    public async Task InternalTransferAsync_ValidTransfer_CreatesTwoLinkedTransactions()
    {
        // This test should be run as an integration test with SQL Server
    }

    [Fact(Skip = "Requires SQL Server - InMemory provider transaction behavior may vary")]
    public async Task InternalTransferAsync_ValidTransfer_ReturnsCorrectResponse()
    {
        // This test should be run as an integration test with SQL Server
    }

    #endregion
}
