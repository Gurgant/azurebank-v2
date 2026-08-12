using AzureBank.Api.Mappers;
using AzureBank.Api.Services.Implementations;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.DTOs.Transaction;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Exceptions;
using AzureBank.Shared.Utilities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Logging;
using Moq;

namespace AzureBank.Tests.Unit.Services;

/// <summary>
/// Unit tests for TransactionService.
/// Tests deposits, withdrawals, and transaction history operations.
/// </summary>
public class TransactionServiceTests : IDisposable
{
    private readonly AzureBankDbContext _context;
    private readonly Mock<IAccountAccessService> _accountAccessMock;
    private readonly Mock<IPinVerifier> _pinVerifierMock;
    private readonly TransactionMapper _mapper;
    private readonly Mock<ILogger<TransactionService>> _loggerMock;
    private readonly TransactionService _sut;
    private readonly FakeTimeProvider _clock;

    public TransactionServiceTests()
    {
        var options = new DbContextOptionsBuilder<AzureBankDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        // A fake clock, because UpdateTimestamps owns CreatedAt: a test cannot set it directly, and
        // two saves a few microseconds apart are not a reliable way to order anything. It STARTS at
        // the real now — xUnit builds a fresh fixture per test, so only the test that advances it
        // sees a shifted clock, and the date-window tests keep the semantics they were written with.
        _clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        _context = new AzureBankDbContext(options, _clock);
        _accountAccessMock = new Mock<IAccountAccessService>();
        _pinVerifierMock = new Mock<IPinVerifier>();
        _mapper = new TransactionMapper();
        _loggerMock = new Mock<ILogger<TransactionService>>();

        _sut = new TransactionService(
            _context,
            _accountAccessMock.Object,
            _pinVerifierMock.Object,
            _mapper,
            _loggerMock.Object);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    #region Helper Methods

    private Account CreateTestAccount(Guid userId, decimal balance = 0, bool isPrimary = true)
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
            CreatedAt = DateTime.UtcNow,
            RowVersion = [0, 0, 0, 0, 0, 0, 0, 1],
            User = null! // Navigation property not needed for unit tests
        };
    }

    private ApplicationUser CreateTestUser(Guid? id = null, string? pinHash = null)
    {
        return new ApplicationUser
        {
            Id = id ?? Guid.NewGuid(),
            Email = "test@example.com",
            AzureTag = "test.user",
            FirstName = "Test",
            LastName = "User",
            PinHash = pinHash
        };
    }

    private Transaction CreateTestTransaction(
        Guid accountId,
        decimal amount,
        TransactionType type,
        TransactionStatus status = TransactionStatus.Completed)
    {
        // NOTE: CreatedAt is server-stamped by AzureBankDbContext.UpdateTimestamps() on
        // save (and transactions are immutable afterwards), so tests can never backdate
        // a transaction — window tests move the WINDOW around "now" instead.
        return new Transaction
        {
            Id = Guid.NewGuid(),
            // The real generator, not a hand-rolled shape. The old literal minted the pre-#89
            // 19-character form, so this file seeded history rows in a format it separately
            // asserts the system cannot produce (see DepositAsync_GeneratesTransactionNumber).
            TransactionNumber = IdGenerator.GenerateTransactionNumber(),
            AccountId = accountId,
            Type = type,
            Amount = amount,
            BalanceBefore = 0,
            BalanceAfter = type == TransactionType.Deposit ? amount : -amount,
            Status = status,
            CreatedAt = DateTime.UtcNow,
            Account = null! // Navigation property not needed for unit tests
        };
    }

    #endregion

    #region DepositAsync Tests

    [Fact]
    public async Task DepositAsync_WithValidRequest_CreatesTransaction()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var account = CreateTestAccount(userId, balance: 100m);
        _context.Accounts.Add(account);
        await _context.SaveChangesAsync();

        _accountAccessMock
            .Setup(x => x.GetAccountWithOwnershipCheckAsync(account.Id, userId))
            .ReturnsAsync(account);

        var request = new DepositRequest
        {
            AccountId = account.Id,
            Amount = 50m,
            Description = "Test deposit"
        };

        // Act
        var result = await _sut.DepositAsync(userId, request);

        // Assert
        result.Should().NotBeNull();
        result.Transaction.Amount.Should().Be(50m);
        result.Transaction.Type.Should().Be(TransactionType.Deposit);
        result.NewBalance.Should().Be(150m);
    }

    [Fact]
    public async Task DepositAsync_UpdatesAccountBalance()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var account = CreateTestAccount(userId, balance: 0m);
        _context.Accounts.Add(account);
        await _context.SaveChangesAsync();

        _accountAccessMock
            .Setup(x => x.GetAccountWithOwnershipCheckAsync(account.Id, userId))
            .ReturnsAsync(account);

        var request = new DepositRequest
        {
            AccountId = account.Id,
            Amount = 100m
        };

        // Act
        await _sut.DepositAsync(userId, request);

        // Assert
        var updatedAccount = await _context.Accounts.FindAsync(account.Id);
        updatedAccount!.Balance.Should().Be(100m);
    }

    [Fact]
    public async Task DepositAsync_RecordsBalanceBeforeAndAfter()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var account = CreateTestAccount(userId, balance: 500m);
        _context.Accounts.Add(account);
        await _context.SaveChangesAsync();

        _accountAccessMock
            .Setup(x => x.GetAccountWithOwnershipCheckAsync(account.Id, userId))
            .ReturnsAsync(account);

        var request = new DepositRequest
        {
            AccountId = account.Id,
            Amount = 200m
        };

        // Act
        await _sut.DepositAsync(userId, request);

        // Assert
        var transaction = await _context.Transactions.FirstOrDefaultAsync(t => t.AccountId == account.Id);
        transaction.Should().NotBeNull();
        transaction!.BalanceBefore.Should().Be(500m);
        transaction.BalanceAfter.Should().Be(700m);
    }

    [Fact]
    public async Task DepositAsync_GeneratesTransactionNumber()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var account = CreateTestAccount(userId);
        _context.Accounts.Add(account);
        await _context.SaveChangesAsync();

        _accountAccessMock
            .Setup(x => x.GetAccountWithOwnershipCheckAsync(account.Id, userId))
            .ReturnsAsync(account);

        var request = new DepositRequest
        {
            AccountId = account.Id,
            Amount = 100m
        };

        // Act
        var result = await _sut.DepositAsync(userId, request);

        // Assert
        result.Transaction.TransactionNumber.Should().NotBeNullOrEmpty();
        // Ten Crockford base32 characters plus a trailing check symbol. Asserted HERE, on what the
        // service actually returns, and not only in IdGeneratorTests: the number reaching the caller
        // is the one printed on a receipt, and a service that stamped its own format would leave
        // that unit test green while the wire shape drifted.
        result.Transaction.TransactionNumber.Should()
            .MatchRegex(@"^TXN-[0-9]{8}-[0-9A-HJKMNP-TV-Z]{10}[0-9A-HJKMNP-TV-Z*~$=U]$");
        IdGenerator.IsValidTransactionNumber(result.Transaction.TransactionNumber).Should().BeTrue(
            "the check symbol must be correct on the value the service hands back, not merely present");
    }

    [Fact]
    public async Task DepositAsync_SetsStatusToCompleted()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var account = CreateTestAccount(userId);
        _context.Accounts.Add(account);
        await _context.SaveChangesAsync();

        _accountAccessMock
            .Setup(x => x.GetAccountWithOwnershipCheckAsync(account.Id, userId))
            .ReturnsAsync(account);

        var request = new DepositRequest
        {
            AccountId = account.Id,
            Amount = 100m
        };

        // Act
        var result = await _sut.DepositAsync(userId, request);

        // Assert
        result.Transaction.Status.Should().Be(TransactionStatus.Completed);
    }

    [Fact]
    public async Task DepositAsync_StoresDescription()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var account = CreateTestAccount(userId);
        _context.Accounts.Add(account);
        await _context.SaveChangesAsync();

        _accountAccessMock
            .Setup(x => x.GetAccountWithOwnershipCheckAsync(account.Id, userId))
            .ReturnsAsync(account);

        var request = new DepositRequest
        {
            AccountId = account.Id,
            Amount = 100m,
            Description = "Monthly salary"
        };

        // Act
        await _sut.DepositAsync(userId, request);

        // Assert
        var transaction = await _context.Transactions.FirstOrDefaultAsync(t => t.AccountId == account.Id);
        transaction!.Description.Should().Be("Monthly salary");
    }

    #endregion

    #region WithdrawAsync Tests

    [Fact]
    public async Task WithdrawAsync_WithValidRequestAndPin_CreatesTransaction()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var account = CreateTestAccount(userId, balance: 1000m);
        var user = CreateTestUser(userId, pinHash: "hashedPin");

        _context.Accounts.Add(account);
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        _accountAccessMock
            .Setup(x => x.GetAccountWithOwnershipCheckAsync(account.Id, userId))
            .ReturnsAsync(account);

        _pinVerifierMock
            .Setup(x => x.VerifyPinAsync(It.IsAny<Guid>(), "123456"))
            .ReturnsAsync(true);

        var request = new WithdrawRequest
        {
            AccountId = account.Id,
            Amount = 200m,
            Pin = "123456",
            Description = "ATM withdrawal"
        };

        // Act
        var result = await _sut.WithdrawAsync(userId, request);

        // Assert
        result.Should().NotBeNull();
        result.Transaction.Amount.Should().Be(200m);
        result.Transaction.Type.Should().Be(TransactionType.Withdrawal);
        result.NewBalance.Should().Be(800m);
    }

    [Fact]
    public async Task WithdrawAsync_WithInsufficientFunds_ThrowsInsufficientFundsException()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var account = CreateTestAccount(userId, balance: 50m);
        var user = CreateTestUser(userId, pinHash: "hashedPin");

        _context.Accounts.Add(account);
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        _accountAccessMock
            .Setup(x => x.GetAccountWithOwnershipCheckAsync(account.Id, userId))
            .ReturnsAsync(account);

        _pinVerifierMock
            .Setup(x => x.VerifyPinAsync(It.IsAny<Guid>(), "123456"))
            .ReturnsAsync(true);

        var request = new WithdrawRequest
        {
            AccountId = account.Id,
            Amount = 100m,
            Pin = "123456"
        };

        // Act
        var act = () => _sut.WithdrawAsync(userId, request);

        // Assert
        await act.Should()
            .ThrowAsync<InsufficientFundsException>();
    }

    [Fact]
    public async Task WithdrawAsync_WithExactBalance_Succeeds()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var account = CreateTestAccount(userId, balance: 100m);
        var user = CreateTestUser(userId, pinHash: "hashedPin");

        _context.Accounts.Add(account);
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        _accountAccessMock
            .Setup(x => x.GetAccountWithOwnershipCheckAsync(account.Id, userId))
            .ReturnsAsync(account);

        _pinVerifierMock
            .Setup(x => x.VerifyPinAsync(It.IsAny<Guid>(), "123456"))
            .ReturnsAsync(true);

        var request = new WithdrawRequest
        {
            AccountId = account.Id,
            Amount = 100m,
            Pin = "123456"
        };

        // Act
        var result = await _sut.WithdrawAsync(userId, request);

        // Assert
        result.NewBalance.Should().Be(0m);
    }

    [Fact]
    public async Task WithdrawAsync_WithInvalidPin_ThrowsAuthenticationException()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var account = CreateTestAccount(userId, balance: 1000m);
        var user = CreateTestUser(userId, pinHash: "hashedPin");

        _context.Accounts.Add(account);
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        _accountAccessMock
            .Setup(x => x.GetAccountWithOwnershipCheckAsync(account.Id, userId))
            .ReturnsAsync(account);

        _pinVerifierMock
            .Setup(x => x.VerifyPinAsync(It.IsAny<Guid>(), "wrongpin"))
            .ReturnsAsync(false);

        var request = new WithdrawRequest
        {
            AccountId = account.Id,
            Amount = 100m,
            Pin = "wrongpin"
        };

        // Act
        var act = () => _sut.WithdrawAsync(userId, request);

        // Assert
        await act.Should()
            .ThrowAsync<AuthenticationException>()
            .WithMessage("*Invalid PIN*");
    }

    [Fact]
    public async Task WithdrawAsync_WithNoPinSet_ThrowsBusinessRuleException()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var account = CreateTestAccount(userId, balance: 1000m);
        var user = CreateTestUser(userId, pinHash: null); // No PIN set

        _context.Accounts.Add(account);
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        _accountAccessMock
            .Setup(x => x.GetAccountWithOwnershipCheckAsync(account.Id, userId))
            .ReturnsAsync(account);

        var request = new WithdrawRequest
        {
            AccountId = account.Id,
            Amount = 100m,
            Pin = "123456"
        };

        // Act
        var act = () => _sut.WithdrawAsync(userId, request);

        // Assert
        await act.Should()
            .ThrowAsync<BusinessRuleException>()
            .WithMessage("*PIN must be set*");
    }

    [Fact]
    public async Task WithdrawAsync_RecordsWithdrawalType()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var account = CreateTestAccount(userId, balance: 1000m);
        var user = CreateTestUser(userId, pinHash: "hashedPin");

        _context.Accounts.Add(account);
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        _accountAccessMock
            .Setup(x => x.GetAccountWithOwnershipCheckAsync(account.Id, userId))
            .ReturnsAsync(account);

        _pinVerifierMock
            .Setup(x => x.VerifyPinAsync(It.IsAny<Guid>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        var request = new WithdrawRequest
        {
            AccountId = account.Id,
            Amount = 100m,
            Pin = "123456"
        };

        // Act
        var result = await _sut.WithdrawAsync(userId, request);

        // Assert
        result.Transaction.Type.Should().Be(TransactionType.Withdrawal);
    }

    #endregion

    #region GetTransactionsAsync Tests

    [Fact]
    public async Task GetTransactionsAsync_ReturnsUserTransactionsOnly()
    {
        // Arrange
        var user1Id = Guid.NewGuid();
        var user2Id = Guid.NewGuid();
        var account1 = CreateTestAccount(user1Id);
        var account2 = CreateTestAccount(user2Id);

        _context.Accounts.AddRange(account1, account2);

        var tx1 = CreateTestTransaction(account1.Id, 100m, TransactionType.Deposit);
        var tx2 = CreateTestTransaction(account2.Id, 200m, TransactionType.Deposit);
        tx1.Account = account1;
        tx2.Account = account2;

        _context.Transactions.AddRange(tx1, tx2);
        await _context.SaveChangesAsync();

        var filter = new TransactionFilter { Page = 1, PageSize = 10 };

        // Act
        var result = await _sut.GetTransactionsAsync(user1Id, filter);

        // Assert
        result.Data.Should().HaveCount(1);
        result.Data.First().Id.Should().Be(tx1.Id);
    }

    [Fact]
    public async Task GetTransactionsAsync_FiltersByAccountId()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var account1 = CreateTestAccount(userId);
        var account2 = CreateTestAccount(userId, isPrimary: false);

        _context.Accounts.AddRange(account1, account2);

        var tx1 = CreateTestTransaction(account1.Id, 100m, TransactionType.Deposit);
        var tx2 = CreateTestTransaction(account2.Id, 200m, TransactionType.Deposit);
        tx1.Account = account1;
        tx2.Account = account2;

        _context.Transactions.AddRange(tx1, tx2);
        await _context.SaveChangesAsync();

        var filter = new TransactionFilter
        {
            Page = 1,
            PageSize = 10,
            AccountId = account1.Id
        };

        // Act
        var result = await _sut.GetTransactionsAsync(userId, filter);

        // Assert
        result.Data.Should().HaveCount(1);
        result.Data.First().Id.Should().Be(tx1.Id);
    }


[Fact]
    public async Task GetTransactionsAsync_PaginatesCorrectly()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var account = CreateTestAccount(userId);
        _context.Accounts.Add(account);

        // Create 15 transactions
        var transactions = Enumerable.Range(1, 15)
            .Select(i =>
            {
                var tx = CreateTestTransaction(account.Id, i * 10m, TransactionType.Deposit);
                tx.CreatedAt = DateTime.UtcNow.AddMinutes(-i);
                tx.Account = account;
                return tx;
            })
            .ToList();

        _context.Transactions.AddRange(transactions);
        await _context.SaveChangesAsync();

        var filter = new TransactionFilter { Page = 2, PageSize = 5 };

        // Act
        var result = await _sut.GetTransactionsAsync(userId, filter);

        // Assert
        result.Data.Should().HaveCount(5);
        result.Pagination.Page.Should().Be(2);
        result.Pagination.PageSize.Should().Be(5);
        result.Pagination.TotalItems.Should().Be(15);
        result.Pagination.TotalPages.Should().Be(3);
    }

    [Fact]
    public async Task GetTransactionsAsync_OrdersByCreatedAtDescending()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var account = CreateTestAccount(userId);
        _context.Accounts.Add(account);

        /*
          Two SAVES, two hours apart on the fake clock. Setting CreatedAt on the entity does nothing
          — AzureBankDbContext.UpdateTimestamps overwrites it — so this test used to assert an order
          it never established: it passed only because the hook read the clock once PER ROW, which
          leaked insertion order in as a microsecond gap. Once one save meant one instant, the two
          rows tied and the assertion started failing about half the time.
        */
        var oldTx = CreateTestTransaction(account.Id, 100m, TransactionType.Deposit);
        oldTx.Account = account;
        _context.Transactions.Add(oldTx);
        await _context.SaveChangesAsync();

        _clock.Advance(TimeSpan.FromHours(2));

        var recentTx = CreateTestTransaction(account.Id, 200m, TransactionType.Deposit);
        recentTx.Account = account;
        _context.Transactions.Add(recentTx);
        await _context.SaveChangesAsync();

        var filter = new TransactionFilter { Page = 1, PageSize = 10 };

        // Act
        var result = await _sut.GetTransactionsAsync(userId, filter);

        // Assert
        result.Data.Should().HaveCount(2);
        result.Data.First().Id.Should().Be(recentTx.Id); // Most recent first
    }

    [Fact]
    public async Task GetTransactionsAsync_RowsSavedTogether_HaveAStableOrder_NotAnArbitraryOne()
    {
        /*
          The tie the previous test deliberately avoids. Every transfer writes its two legs in ONE
          SaveChanges, so they share one CreatedAt by construction — ordering on that column alone
          leaves them tied, and a tied ORDER BY under OFFSET/FETCH may hand back a row on two pages
          and drop another. The Id tiebreaker makes the order TOTAL and STABLE, which is the property
          paging needs.

          This asserts stability and nothing more, on purpose. A first draft also asserted that the
          later-minted id sorts first — and flaked one run in three, because Guid.CreateVersion7
          seeds its random sub-fields with random data rather than a counter, so two ids minted
          inside one millisecond order arbitrarily. (SQL Server would disagree with .NET about that
          order anyway: uniqueidentifier collates on a different byte order.) The direction of a tie
          is not a property this code has, so no test should claim it.
        */
        var userId = Guid.NewGuid();
        var account = CreateTestAccount(userId);
        _context.Accounts.Add(account);

        // Ids chosen so that descending-Id order is the OPPOSITE of insertion order. Without that
        // the assertion cannot discriminate: with no tiebreaker the provider returns tied rows in
        // insertion order, which for real UUIDv7s coincides with the expected order about half the
        // time — a test that fails one run in two is not a guard, it is a flake.
        var lowId = Guid.Parse("019ff770-0000-7000-8000-000000000001");
        var highId = Guid.Parse("019ff770-0000-7000-8000-000000000002");

        var insertedFirst = CreateTestTransaction(account.Id, 100m, TransactionType.Deposit);
        insertedFirst.Id = lowId;
        insertedFirst.Account = account;
        var insertedSecond = CreateTestTransaction(account.Id, 200m, TransactionType.Deposit);
        insertedSecond.Id = highId;
        insertedSecond.Account = account;

        _context.Transactions.AddRange(insertedFirst, insertedSecond);
        await _context.SaveChangesAsync();

        insertedFirst.CreatedAt.Should().Be(insertedSecond.CreatedAt,
            "one save is one instant — this IS the tie");

        var filter = new TransactionFilter { Page = 1, PageSize = 10 };
        var page = await _sut.GetTransactionsAsync(userId, filter);
        var again = await _sut.GetTransactionsAsync(userId, filter);

        page.Data.Select(t => t.Id).Should().Equal([highId, lowId],
            "the tiebreaker orders the tie by Id descending, against insertion order");
        again.Data.Select(t => t.Id).Should().Equal(page.Data.Select(t => t.Id),
            "and it is the same every time — that is what paging depends on");
    }

    [Fact]
    public async Task GetTransactionsAsync_WithNoAccounts_ReturnsEmptyList()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var filter = new TransactionFilter { Page = 1, PageSize = 10 };

        // Act
        var result = await _sut.GetTransactionsAsync(userId, filter);

        // Assert
        result.Data.Should().BeEmpty();
        result.Pagination.TotalItems.Should().Be(0);
    }

    [Fact]
    public async Task GetTransactionsAsync_FilterByOtherUsersAccount_ThrowsAuthorizationException()
    {
        // Arrange
        var user1Id = Guid.NewGuid();
        var user2Id = Guid.NewGuid();
        var user1Account = CreateTestAccount(user1Id);
        var user2Account = CreateTestAccount(user2Id);

        _context.Accounts.AddRange(user1Account, user2Account);
        await _context.SaveChangesAsync();

        var filter = new TransactionFilter
        {
            Page = 1,
            PageSize = 10,
            AccountId = user2Account.Id // User1 trying to filter by User2's account
        };

        // Act
        var act = () => _sut.GetTransactionsAsync(user1Id, filter);

        // Assert
        await act.Should()
            .ThrowAsync<AuthorizationException>()
            .WithMessage("*do not have access*");
    }

    #endregion

    #region GetTransactionByIdAsync Tests

    [Fact]
    public async Task GetTransactionByIdAsync_ExistingTransaction_ReturnsTransaction()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var account = CreateTestAccount(userId);
        _context.Accounts.Add(account);

        var transaction = CreateTestTransaction(account.Id, 100m, TransactionType.Deposit);
        transaction.Account = account;
        _context.Transactions.Add(transaction);
        await _context.SaveChangesAsync();

        // Act
        var result = await _sut.GetTransactionByIdAsync(transaction.Id, userId);

        // Assert
        result.Should().NotBeNull();
        result.Id.Should().Be(transaction.Id);
        result.Amount.Should().Be(100m);
    }

    [Fact]
    public async Task GetTransactionByIdAsync_NonExistentTransaction_ThrowsNotFoundException()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var nonExistentId = Guid.NewGuid();

        // Act
        var act = () => _sut.GetTransactionByIdAsync(nonExistentId, userId);

        // Assert
        await act.Should()
            .ThrowAsync<NotFoundException>()
            .WithMessage($"*Transaction*{nonExistentId}*");
    }

    [Fact]
    public async Task GetTransactionByIdAsync_OtherUsersTransaction_ThrowsAuthorizationException()
    {
        // Arrange
        var user1Id = Guid.NewGuid();
        var user2Id = Guid.NewGuid();
        var account = CreateTestAccount(user1Id);
        _context.Accounts.Add(account);

        var transaction = CreateTestTransaction(account.Id, 100m, TransactionType.Deposit);
        transaction.Account = account;
        _context.Transactions.Add(transaction);
        await _context.SaveChangesAsync();

        // Act - User2 trying to access User1's transaction
        var act = () => _sut.GetTransactionByIdAsync(transaction.Id, user2Id);

        // Assert
        await act.Should()
            .ThrowAsync<AuthorizationException>()
            .WithMessage("*do not have access*");
    }

    #endregion

    #region GetSummaryAsync Tests

    [Fact]
    public async Task GetSummaryAsync_ComposesIncomeExpensesAndNet()
    {
        // Arrange — income = Deposit + TransferIn, expenses = Withdrawal + TransferOut
        var userId = Guid.NewGuid();
        var account = CreateTestAccount(userId);
        _context.Accounts.Add(account);
        _context.Transactions.AddRange(
            CreateTestTransaction(account.Id, 100m, TransactionType.Deposit),
            CreateTestTransaction(account.Id, 50m, TransactionType.TransferIn),
            CreateTestTransaction(account.Id, 30m, TransactionType.Withdrawal),
            CreateTestTransaction(account.Id, 20m, TransactionType.TransferOut));
        await _context.SaveChangesAsync();

        // Act
        var result = await _sut.GetSummaryAsync(userId, new TransactionSummaryFilter());

        // Assert
        result.TotalIncome.Should().Be(150m);
        result.TotalExpenses.Should().Be(50m);
        result.NetChange.Should().Be(100m);
        result.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task GetSummaryAsync_CountsOnlyTransactionsInsideTheWindow()
    {
        // Arrange — CreatedAt is server-stamped on save and transactions are immutable,
        // so the window filter is exercised by moving the WINDOW around a "now"
        // transaction: one window contains it, one (entirely in the past) does not.
        var userId = Guid.NewGuid();
        var account = CreateTestAccount(userId);
        _context.Accounts.Add(account);
        _context.Transactions.Add(CreateTestTransaction(account.Id, 100m, TransactionType.Deposit));
        await _context.SaveChangesAsync();

        var containingWindow = new TransactionSummaryFilter
        {
            FromDate = DateTime.UtcNow.AddHours(-1),
            ToDate = DateTime.UtcNow.AddHours(1)
        };
        var pastWindow = new TransactionSummaryFilter
        {
            FromDate = DateTime.UtcNow.AddDays(-7),
            ToDate = DateTime.UtcNow.AddDays(-1)
        };

        // Act
        var inWindow = await _sut.GetSummaryAsync(userId, containingWindow);
        var outsideWindow = await _sut.GetSummaryAsync(userId, pastWindow);

        // Assert — the same transaction is counted or not purely by the window,
        // and the explicit bounds are echoed back verbatim
        inWindow.TotalIncome.Should().Be(100m);
        inWindow.FromDate.Should().Be(containingWindow.FromDate.Value);
        inWindow.ToDate.Should().Be(containingWindow.ToDate.Value);
        outsideWindow.TotalIncome.Should().Be(0m);
    }

    [Fact]
    public async Task GetSummaryAsync_ExcludesNonCompletedFromSums_AndCountsPending()
    {
        // Arrange — Pending/Failed/Reversed must not inflate the money totals
        var userId = Guid.NewGuid();
        var account = CreateTestAccount(userId);
        _context.Accounts.Add(account);
        _context.Transactions.AddRange(
            CreateTestTransaction(account.Id, 100m, TransactionType.Deposit, TransactionStatus.Pending),
            CreateTestTransaction(account.Id, 50m, TransactionType.Deposit, TransactionStatus.Failed),
            CreateTestTransaction(account.Id, 25m, TransactionType.Withdrawal, TransactionStatus.Reversed),
            CreateTestTransaction(account.Id, 10m, TransactionType.Deposit));
        await _context.SaveChangesAsync();

        // Act
        var result = await _sut.GetSummaryAsync(userId, new TransactionSummaryFilter());

        // Assert
        result.TotalIncome.Should().Be(10m);
        result.TotalExpenses.Should().Be(0m);
        result.NetChange.Should().Be(10m);
        result.PendingCount.Should().Be(1);
    }

    [Fact]
    public async Task GetSummaryAsync_WithNoAccounts_ReturnsZeros()
    {
        // Act — a user with no accounts short-circuits before the aggregate query
        var result = await _sut.GetSummaryAsync(Guid.NewGuid(), new TransactionSummaryFilter());

        // Assert
        result.TotalIncome.Should().Be(0m);
        result.TotalExpenses.Should().Be(0m);
        result.NetChange.Should().Be(0m);
        result.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task GetSummaryAsync_WithNoTransactionsInWindow_ReturnsZeros()
    {
        // Arrange — an account exists but the window is empty (no grouping row comes back)
        var userId = Guid.NewGuid();
        var account = CreateTestAccount(userId);
        _context.Accounts.Add(account);
        await _context.SaveChangesAsync();

        // Act
        var result = await _sut.GetSummaryAsync(userId, new TransactionSummaryFilter());

        // Assert
        result.TotalIncome.Should().Be(0m);
        result.TotalExpenses.Should().Be(0m);
        result.NetChange.Should().Be(0m);
        result.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task GetSummaryAsync_IgnoresOtherUsersTransactions()
    {
        // Arrange — two users with their own accounts and money movement
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var account = CreateTestAccount(userId);
        var otherAccount = CreateTestAccount(otherUserId);
        _context.Accounts.AddRange(account, otherAccount);
        _context.Transactions.AddRange(
            CreateTestTransaction(account.Id, 100m, TransactionType.Deposit),
            CreateTestTransaction(otherAccount.Id, 9_999m, TransactionType.Deposit));
        await _context.SaveChangesAsync();

        // Act
        var result = await _sut.GetSummaryAsync(userId, new TransactionSummaryFilter());

        // Assert — only the caller's account feeds the aggregate
        result.TotalIncome.Should().Be(100m);
    }

    [Fact]
    public async Task GetSummaryAsync_DefaultsToTheCurrentUtcMonth()
    {
        // Arrange — capture "now" on both sides so a month rollover mid-test cannot flake
        var beforeUtc = DateTime.UtcNow;

        // Act — no bounds provided
        var result = await _sut.GetSummaryAsync(Guid.NewGuid(), new TransactionSummaryFilter());
        var afterUtc = DateTime.UtcNow;

        // Assert — resolved window = first instant of the CURRENT UTC month up to "now"
        result.FromDate.Day.Should().Be(1);
        result.FromDate.TimeOfDay.Should().Be(TimeSpan.Zero);
        var matchesACapturedMonth =
            (result.FromDate.Year == beforeUtc.Year && result.FromDate.Month == beforeUtc.Month)
            || (result.FromDate.Year == afterUtc.Year && result.FromDate.Month == afterUtc.Month);
        matchesACapturedMonth.Should().BeTrue(
            "the default FromDate must be the first day of the current UTC month");
        result.FromDate.Should().BeOnOrBefore(result.ToDate);
        result.ToDate.Should().BeCloseTo(afterUtc, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task GetSummaryAsync_WithFutureFromDateAndDefaultToDate_ThrowsInvalidDateRange()
    {
        // Arrange — the filter's own validation cannot see this (ToDate is omitted);
        // the service must reject the RESOLVED inverted window.
        var filter = new TransactionSummaryFilter { FromDate = DateTime.UtcNow.AddDays(10) };

        // Act
        var act = () => _sut.GetSummaryAsync(Guid.NewGuid(), filter);

        // Assert
        var thrown = await act.Should().ThrowAsync<BusinessRuleException>();
        thrown.Which.ErrorCode.Should().Be("INVALID_DATE_RANGE");
    }

    [Fact]
    public async Task GetSummaryAsync_WithAccountId_AggregatesOnlyThatAccount()
    {
        // Arrange — two accounts, same owner. Without the scope these totals merge, which is
        // exactly what the dashboard used to have to apologise for.
        var userId = Guid.NewGuid();
        var scoped = CreateTestAccount(userId);
        var other = CreateTestAccount(userId, isPrimary: false);
        _context.Accounts.AddRange(scoped, other);
        _context.Transactions.AddRange(
            CreateTestTransaction(scoped.Id, 100m, TransactionType.Deposit),
            CreateTestTransaction(scoped.Id, 30m, TransactionType.Withdrawal),
            CreateTestTransaction(other.Id, 9_999m, TransactionType.Deposit));
        await _context.SaveChangesAsync();

        // Act
        var result = await _sut.GetSummaryAsync(
            userId, new TransactionSummaryFilter { AccountId = scoped.Id });

        // Assert — the other account's 9,999 must not appear anywhere in the figures.
        result.TotalIncome.Should().Be(100m);
        result.TotalExpenses.Should().Be(30m);
        result.NetChange.Should().Be(70m);
    }

    [Fact]
    public async Task GetSummaryAsync_WithoutAccountId_StillAggregatesEveryAccount()
    {
        // Arrange — the negative control for the test above: omitting the scope must not have
        // quietly become "the primary account only".
        var userId = Guid.NewGuid();
        var first = CreateTestAccount(userId);
        var second = CreateTestAccount(userId, isPrimary: false);
        _context.Accounts.AddRange(first, second);
        _context.Transactions.AddRange(
            CreateTestTransaction(first.Id, 100m, TransactionType.Deposit),
            CreateTestTransaction(second.Id, 25m, TransactionType.Deposit));
        await _context.SaveChangesAsync();

        // Act
        var result = await _sut.GetSummaryAsync(userId, new TransactionSummaryFilter());

        // Assert
        result.TotalIncome.Should().Be(125m);
    }

    [Fact]
    public async Task GetSummaryAsync_WithAnotherUsersAccountId_Throws()
    {
        // Arrange — the money question this parameter exists to get wrong: an id that is real,
        // but not the caller's.
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var mine = CreateTestAccount(userId);
        var theirs = CreateTestAccount(otherUserId);
        _context.Accounts.AddRange(mine, theirs);
        _context.Transactions.Add(CreateTestTransaction(theirs.Id, 9_999m, TransactionType.Deposit));
        await _context.SaveChangesAsync();

        // Act
        var act = () => _sut.GetSummaryAsync(
            userId, new TransactionSummaryFilter { AccountId = theirs.Id });

        // Assert — refused, not silently emptied: a zeroed summary would also be a leak, since it
        // would confirm the id parses and the window is valid.
        await act.Should().ThrowAsync<AuthorizationException>()
            .WithMessage("*do not have access*");
    }

    [Fact]
    public async Task GetSummaryAsync_WithUnknownAccountId_ThrowsTheSameWayAsAForeignOne()
    {
        // Arrange — the anti-oracle property, and the reason it is its own test: if "not yours"
        // and "does not exist" answered differently, the endpoint would let anyone sort guessed
        // GUIDs into real and unreal. The two cases must be indistinguishable from outside.
        var userId = Guid.NewGuid();
        _context.Accounts.Add(CreateTestAccount(userId));
        await _context.SaveChangesAsync();

        var foreignAccount = CreateTestAccount(Guid.NewGuid());
        _context.Accounts.Add(foreignAccount);
        await _context.SaveChangesAsync();

        // Act
        var unknown = await Record.ExceptionAsync(() => _sut.GetSummaryAsync(
            userId, new TransactionSummaryFilter { AccountId = Guid.NewGuid() }));
        var foreign = await Record.ExceptionAsync(() => _sut.GetSummaryAsync(
            userId, new TransactionSummaryFilter { AccountId = foreignAccount.Id }));

        // Assert — same type, same message, same error code. Compared rather than asserted
        // separately, because the property is that they AGREE.
        unknown.Should().BeOfType<AuthorizationException>();
        foreign.Should().BeOfType<AuthorizationException>();
        unknown!.Message.Should().Be(foreign!.Message);
        ((AuthorizationException)unknown).ErrorCode
            .Should().Be(((AuthorizationException)foreign).ErrorCode);
    }

    [Fact]
    public async Task GetSummaryAsync_WithSoftDeletedAccountId_Throws()
    {
        // Arrange — matches GetTransactionsAsync, whose ownership list is also built with
        // `!IsDeleted`. A closed account must not become a scope the summary silently accepts and
        // then reports zeros for.
        var userId = Guid.NewGuid();
        var deleted = CreateTestAccount(userId);
        deleted.IsDeleted = true;
        _context.Accounts.Add(deleted);
        await _context.SaveChangesAsync();

        // Act
        var act = () => _sut.GetSummaryAsync(
            userId, new TransactionSummaryFilter { AccountId = deleted.Id });

        // Assert
        await act.Should().ThrowAsync<AuthorizationException>();
    }

    #endregion

    // Date-range filtering now lives in Integration/HistoricalBalanceSqlServerTests.cs (same
    // reason: UpdateTimestamps() stamps every new row with the run's own clock).
}
