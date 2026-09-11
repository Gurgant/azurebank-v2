using AzureBank.Api.Mappers;
using AzureBank.Api.Services;
using AzureBank.Api.Services.Implementations;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Transfer;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Exceptions;
using AzureBank.Shared.Options;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace AzureBank.Tests.Unit.Services;

/// <summary>
/// Unit tests for TransferService.
/// Tests external transfers (via AzureTag) and internal transfers (between own accounts).
/// </summary>
public class TransferServiceTests : IDisposable
{
    /// <summary>The PIN these tests enrol and then spend at the mint endpoint (ADR-0042).</summary>
    private const string TestPin = "123456";

    private readonly AzureBankDbContext _context;
    private readonly Mock<IAccountAccessService> _accountAccessMock;
    private readonly UserMapper _userMapper;
    private readonly Mock<IPinVerifier> _pinVerifierMock;
    private readonly Mock<ILogger<TransferService>> _loggerMock;
    private readonly Mock<IAuditService> _auditMock = new();
    private readonly StepUpAuthorizationService _stepUp;
    private readonly TransferService _sut;

    /// <summary>
    /// ONE clock for the context and the day's helper (ADR-0050): the rows these tests seed are
    /// stamped by it, and the window the helper sums is read from it.
    /// </summary>
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);

    /// <summary>
    /// The ceiling, mutable per test; the helper reads this instance, not a copy.
    /// </summary>
    private readonly DailyLimitOptions _dailyLimitOptions = new() { Amount = 5_000m };

    public TransferServiceTests()
    {
        var options = new DbContextOptionsBuilder<AzureBankDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _context = new AzureBankDbContext(options, _clock);
        _accountAccessMock = new Mock<IAccountAccessService>();
        _userMapper = new UserMapper();
        _pinVerifierMock = new Mock<IPinVerifier>();
        // Default to a CORRECT pin, so the pre-existing tests keep exercising what they were written
        // for. The PIN-specific cases below override this per test.
        _pinVerifierMock.Setup(v => v.VerifyPinAsync(It.IsAny<Guid>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        _loggerMock = new Mock<ILogger<TransferService>>();

        // A REAL step-up service over the same InMemory context, not a mock — and now that the
        // header is required (ADR-0042), every success path here actually mints and spends through
        // it, so these tests exercise the true mint/validate/consume rather than a stub that always
        // agrees. `AuthorisedAsync` below is the only way a transfer in this file can succeed.
        _stepUp = new StepUpAuthorizationService(
            _context,
            _pinVerifierMock.Object,
            _auditMock.Object,
            Options.Create(new StepUpOptions { BindingKey = new string('k', 32) }),
            new Mock<ILogger<StepUpAuthorizationService>>().Object);

        // The REAL day's helper over the same context and the same clock, not a mock: the three
        // checks these tests pin are about WHERE the helper is called, and a stub that always
        // agrees would let the calls move without a test noticing.
        var dailyLimit = new DailyOutflowLimitService(
            _context, Options.Create(_dailyLimitOptions), _clock);

        _sut = new TransferService(
            _context, _accountAccessMock.Object, _userMapper, _pinVerifierMock.Object, _stepUp,
            _loggerMock.Object, _auditMock.Object, dailyLimit, Options.Create(_dailyLimitOptions));
    }

    /// <summary>
    /// A COMPLETED external TransferOut row for <paramref name="account"/> today, saved through the
    /// context so <c>CreatedAt</c> is the shared clock's instant — what the day's sum reads.
    /// </summary>
    private async Task SpentTodayAsync(Account account, decimal amount)
    {
        _context.Transactions.Add(new Transaction
        {
            Id = Guid.CreateVersion7(),
            TransactionNumber = $"TXN-20260907-{Random.Shared.Next(100000, 999999)}",
            AccountId = account.Id,
            Account = account,
            Type = TransactionType.TransferOut,
            Amount = amount,
            BalanceBefore = account.Balance,
            BalanceAfter = account.Balance,
            RecipientAzureTag = "someone",
            Status = TransactionStatus.Completed
        });
        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// Mints a REAL authorisation for exactly this movement, the way the mint endpoint does.
    /// A transfer that must reach its business rules needs one: nothing else gets past
    /// <c>RequireAuthorization</c> and then past <c>ValidateAsync</c>'s binding comparison.
    /// </summary>
    private async Task<Guid> AuthorisedAsync(Guid userId, StepUpOperation operation, StepUpBinding binding)
        => (await _stepUp.MintAsync(userId, operation, binding, TestPin)).Id;

    /// <summary>
    /// A reference that IS presented and is worth nothing: syntactically an authorisation, bound to
    /// no movement, so it clears <c>RequireAuthorization</c> and would be refused
    /// <c>AUTHORIZATION_INVALID</c> by <c>ValidateAsync</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately not a real mint. These tests are about refusals that must happen BEFORE the
    /// authorisation is validated — self-transfer, unknown recipient, an unreceivable payee — and
    /// using a worthless reference is what makes them say so: if that ordering ever inverts, they
    /// stop reporting their own refusal and start reporting AUTHORIZATION_INVALID.
    /// </remarks>
    private static Guid Presented() => Guid.CreateVersion7();

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    #region Helper Methods

    private ApplicationUser CreateTestUser(string azureTag, string firstName = "Test", string lastName = "User")
    {
        var id = Guid.NewGuid();
        return new ApplicationUser
        {
            Id = id,
            AzureTag = azureTag.ToLower(),
            // Decouple (ADR-0015): Identity's UserName is the immutable user id, not the handle.
            UserName = id.ToString(),
            NormalizedUserName = id.ToString().ToUpperInvariant(),
            Email = $"{azureTag}@test.com",
            NormalizedEmail = $"{azureTag.ToUpper()}@TEST.COM",
            FirstName = firstName,
            LastName = lastName,
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString(),
            CreatedAt = DateTime.UtcNow,
            // Enrolled by default (ADR-0041): TransferAsync now refuses a user with no PinHash
            // before it reaches any other rule, which would mask every assertion below behind a
            // PIN_REQUIRED. The value is never verified here — IPinVerifier is mocked — so only
            // its presence matters. The not-enrolled case is covered explicitly further down.
            PinHash = "not-a-real-hash-presence-is-what-is-checked"
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
        var act = () => _sut.TransferAsync(sender.Id, request, Presented());

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
        var act = () => _sut.TransferAsync(sender.Id, request, Presented());

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
        var act = () => _sut.TransferAsync(sender.Id, request, Presented());

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
        var act = () => _sut.TransferAsync(sender.Id, request, Presented());

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

        // A REAL authorisation, bound to exactly this movement: the funds check sits BELOW
        // ValidateAsync, so a worthless reference would refuse here for the wrong reason and this
        // test would pass while proving nothing about the balance.
        var authorization = await AuthorisedAsync(
            sender.Id, StepUpOperation.Transfer,
            new StepUpBinding(senderAccount.Id, null, recipient.Id, 100m));

        // Act
        var act = () => _sut.TransferAsync(sender.Id, request, authorization);

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

        var authorization = await AuthorisedAsync(
            sender.Id, StepUpOperation.Transfer,
            new StepUpBinding(senderAccount.Id, null, recipient.Id, 100m));

        // Act - Should not throw InsufficientFundsException
        // Note: May fail due to transaction handling in InMemory, but the funds check passes
        var act = () => _sut.TransferAsync(sender.Id, request, authorization);

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
        var act = () => _sut.TransferAsync(nonExistentUserId, request, Presented());

        // Assert
        await act.Should()
            .ThrowAsync<NotFoundException>()
            .WithMessage("*User*");
    }

    #endregion

    #region TransferAsync - Successful Transfer Tests

#endregion

    #region The day's ceiling (ADR-0050)

    [Fact]
    public async Task AuthoriseTransferAsync_OverTheDaysLimit_RefusesBeforeThePinIsConsulted()
    {
        /*
          THE MINT RUNG (ADR-0049 D4, applied by ADR-0050 D4): a 422 that reveals only the caller's
          own state runs before IPinVerifier spends an attempt. Measured 2026-09-07 before this
          change (B1): a mint of 400 with 250 left answered 201 — the mint checked nothing about
          money, so a doomed transfer cost a PIN entry. Times.Never on the verifier is the whole
          test; a green run with the check moved below MintAsync would show a Once here.
        */
        var (sender, senderAccount, recipient, _) = await SetupTransferScenarioAsync();
        await SpentTodayAsync(senderAccount, 4_950m);

        var act = () => _sut.AuthoriseTransferAsync(sender.Id, new TransferAuthorizationRequest
        {
            FromAccountId = senderAccount.Id,
            RecipientAzureTag = recipient.AzureTag,
            Amount = 100m,
            Pin = "000000" // wrong on purpose: it must never be looked at
        });

        var refusal = await act.Should().ThrowAsync<DailyLimitExceededException>();
        refusal.Which.Details!["limit"].Should().Be(5_000m);
        refusal.Which.Details["used"].Should().Be(4_950m);
        refusal.Which.Details["requested"].Should().Be(100m);

        _pinVerifierMock.Verify(
            v => v.VerifyPinAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never,
            "a refusal from the caller's own ledger must not cost an attempt");
        (await _context.StepUpAuthorizations.CountAsync()).Should().Be(0, "nothing was minted");
    }

    [Fact]
    public async Task AuthoriseTransferAsync_AtExactlyTheLimit_StillMints()
    {
        var (sender, senderAccount, recipient, _) = await SetupTransferScenarioAsync();
        await SpentTodayAsync(senderAccount, 4_900m);

        var minted = await _sut.AuthoriseTransferAsync(sender.Id, new TransferAuthorizationRequest
        {
            FromAccountId = senderAccount.Id,
            RecipientAzureTag = recipient.AzureTag,
            Amount = 100m,
            Pin = TestPin
        });

        minted.AuthorizationId.Should().NotBeEmpty("used + requested == limit is allowed");
        _pinVerifierMock.Verify(v => v.VerifyPinAsync(sender.Id, TestPin), Times.Once);
    }

    [Fact]
    public async Task AuthoriseInternalTransferAsync_OverTheDaysLimit_StillMints()
    {
        // Internal moves are not counted and not bounded (ADR-0050 D2): the day's ceiling does not
        // sit on this rail at all, so an exhausted day changes nothing here.
        var owner = CreateTestUser("owner");
        var from = CreateTestAccount(owner.Id);
        var to = CreateTestAccount(owner.Id);
        _context.Users.Add(owner);
        _context.Accounts.AddRange(from, to);
        await _context.SaveChangesAsync();
        await SpentTodayAsync(from, 5_000m);
        _accountAccessMock.Setup(x => x.GetAccountWithOwnershipCheckAsync(from.Id, owner.Id)).ReturnsAsync(from);
        _accountAccessMock.Setup(x => x.GetAccountWithOwnershipCheckAsync(to.Id, owner.Id)).ReturnsAsync(to);

        var minted = await _sut.AuthoriseInternalTransferAsync(owner.Id, new InternalTransferAuthorizationRequest
        {
            FromAccountId = from.Id,
            ToAccountId = to.Id,
            Amount = 100m,
            Pin = TestPin
        });

        minted.AuthorizationId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task TransferAsync_OverTheDaysLimit_RefusesAfterValidateAsync_AndWritesNothing()
    {
        var (sender, senderAccount, recipient, _) = await SetupTransferScenarioAsync();
        await SpentTodayAsync(senderAccount, 4_950m);
        var rowsBefore = await _context.Transactions.CountAsync();

        // Minted THROUGH the step-up service directly, bypassing the mint's own daily check: the
        // transfer's pre-check must refuse on its own, because the mint does not reserve anything
        // and a day can be exhausted between a mint and its spend.
        var authorization = await AuthorisedAsync(
            sender.Id, StepUpOperation.Transfer,
            new StepUpBinding(senderAccount.Id, null, recipient.Id, 100m));

        var act = () => _sut.TransferAsync(sender.Id, new TransferRequest
        {
            FromAccountId = senderAccount.Id,
            RecipientAzureTag = recipient.AzureTag,
            Amount = 100m
        }, authorization);

        await act.Should().ThrowAsync<DailyLimitExceededException>();

        (await _context.Transactions.CountAsync()).Should().Be(rowsBefore, "a refused transfer builds no row");
        senderAccount.Balance.Should().Be(1000m, "and moves no money");
        (await _context.StepUpAuthorizations.SingleAsync(a => a.Id == authorization)).Status
            .Should().Be(StepUpAuthorizationStatus.Pending, "the authorisation is left spendable, as after INSUFFICIENT_FUNDS");
    }

    [Fact]
    public async Task TransferAsync_OverTheDaysLimit_WithAWorthlessAuthorisation_RefusesTheAuthorisationFirst()
    {
        // ORDER: the daily check sits AFTER ValidateAsync, so it cannot be an oracle without a
        // minted authorisation. A worthless reference must still answer AUTHORIZATION_INVALID,
        // not the daily code — the same trick Presented() plays for the payee refusals above.
        var (sender, senderAccount, recipient, _) = await SetupTransferScenarioAsync();
        await SpentTodayAsync(senderAccount, 5_000m);

        var act = () => _sut.TransferAsync(sender.Id, new TransferRequest
        {
            FromAccountId = senderAccount.Id,
            RecipientAzureTag = recipient.AzureTag,
            Amount = 100m
        }, Presented());

        (await act.Should().ThrowAsync<AuthenticationException>())
            .Which.ErrorCode.Should().Be(ErrorCodes.AuthorizationInvalid);
    }

    [Fact]
    public async Task TransferAsync_OverTheDaysLimitAndOverTheBalance_AnswersTheDailyCode()
    {
        /*
          DAILY BEFORE BALANCE, as a decision (ADR-0050 D4): the mint has no balance check, so
          keeping the daily check ahead of the funds loop makes the mint and the transfer refuse in
          the same order. With the pre-check deleted, this request would answer INSUFFICIENT_FUNDS
          from the loop — which is the mutant this test exists to catch.
        */
        var (sender, senderAccount, recipient, _) = await SetupTransferScenarioAsync(senderBalance: 50m);
        await SpentTodayAsync(senderAccount, 4_950m);

        var authorization = await AuthorisedAsync(
            sender.Id, StepUpOperation.Transfer,
            new StepUpBinding(senderAccount.Id, null, recipient.Id, 100m));

        var act = () => _sut.TransferAsync(sender.Id, new TransferRequest
        {
            FromAccountId = senderAccount.Id,
            RecipientAzureTag = recipient.AzureTag,
            Amount = 100m
        }, authorization);

        await act.Should().ThrowAsync<DailyLimitExceededException>(
            "both bounds are violated and the daily one is checked first");
    }

    [Fact]
    public async Task TransferAsync_AtExactlyTheLimit_IsNotRefusedForTheDay()
    {
        // Same posture as TransferAsync_ExactBalance above: the InMemory host cannot open the
        // transaction the delegate needs, so only the refusal that must NOT happen is asserted.
        var (sender, senderAccount, recipient, _) = await SetupTransferScenarioAsync();
        await SpentTodayAsync(senderAccount, 4_900m);

        var authorization = await AuthorisedAsync(
            sender.Id, StepUpOperation.Transfer,
            new StepUpBinding(senderAccount.Id, null, recipient.Id, 100m));

        try
        {
            await _sut.TransferAsync(sender.Id, new TransferRequest
            {
                FromAccountId = senderAccount.Id,
                RecipientAzureTag = recipient.AzureTag,
                Amount = 100m
            }, authorization);
        }
        catch (DailyLimitExceededException)
        {
            Assert.Fail("used + requested == limit must pass: the bound is inclusive");
        }
        catch
        {
            // Other exceptions are acceptable (InMemory transaction issues), as above.
        }
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
        // The user must exist in the context: minting the authorisation reads it, and the account
        // rules this test is actually about sit further down.
        var owner = CreateTestUser("owner");
        owner.Id = userId;
        _context.Users.Add(owner);
        await _context.SaveChangesAsync();

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
        var act = () => _sut.InternalTransferAsync(userId, request, Presented());

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
        // The user must exist in the context: minting the authorisation reads it, and the account
        // rules this test is actually about sit further down.
        var owner = CreateTestUser("owner");
        owner.Id = userId;
        _context.Users.Add(owner);
        await _context.SaveChangesAsync();

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

        // Real, and bound to this exact move: the funds check is below ValidateAsync.
        var authorization = await AuthorisedAsync(
            userId, StepUpOperation.InternalTransfer,
            new StepUpBinding(fromAccount.Id, toAccount.Id, null, 100m));

        // Act
        var act = () => _sut.InternalTransferAsync(userId, request, authorization);

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
        var act = () => _sut.InternalTransferAsync(userId, request, stepUpAuthorizationId: null);

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
        var act = () => _sut.InternalTransferAsync(userId, request, stepUpAuthorizationId: null);

        // Assert
        await act.Should().ThrowAsync<AuthorizationException>();
    }

    #endregion

    #region InternalTransferAsync - Successful Transfer Tests

#endregion
}
