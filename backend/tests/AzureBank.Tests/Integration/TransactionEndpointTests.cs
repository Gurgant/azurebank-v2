using System.Net;
using System.Net.Http.Json;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transaction;
using AzureBank.Shared.Enums;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Integration tests for Transaction endpoints.
/// Tests: /api/transactions (GET), /api/transactions/{id} (GET),
///        /api/transactions/deposit (POST), /api/transactions/withdraw (POST)
/// </summary>
public class TransactionEndpointTests : IntegrationTestBase
{
    public TransactionEndpointTests(CustomWebApplicationFactory factory) : base(factory) { }

    #region Deposit Tests

    [Fact]
    public async Task Deposit_WithValidData_ReturnsCreated()
    {
        // Arrange
        var (token, _, accountId) = await RegisterTestUserAsync();
        SetAuthHeader(token);

        var request = new DepositRequest
        {
            AccountId = accountId,
            Amount = 1000.00m,
            Description = "Test deposit"
        };

        // Act
        var response = await PostMonetaryAsync("/api/transactions/deposit", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<DepositResponse>>(JsonOptions);
        result!.Data!.Transaction.Type.Should().Be(TransactionType.Deposit);
        result.Data.Transaction.Amount.Should().Be(1000.00m);
        result.Data.NewBalance.Should().Be(1000.00m);
    }

    [Fact]
    public async Task Deposit_WithZeroAmount_ReturnsBadRequest()
    {
        // Arrange
        var (token, _, accountId) = await RegisterTestUserAsync();
        SetAuthHeader(token);

        var request = new DepositRequest
        {
            AccountId = accountId,
            Amount = 0,
            Description = "Zero deposit"
        };

        // Act
        var response = await PostMonetaryAsync("/api/transactions/deposit", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Deposit_ToOtherUsersAccount_ReturnsForbidden()
    {
        // Arrange
        var (token1, _, accountId1) = await RegisterTestUserAsync();
        var (token2, _, _) = await RegisterTestUserAsync();

        // User 2 tries to deposit to User 1's account
        SetAuthHeader(token2);

        var request = new DepositRequest
        {
            AccountId = accountId1,
            Amount = 100.00m,
            Description = "Unauthorized deposit"
        };

        // Act
        var response = await PostMonetaryAsync("/api/transactions/deposit", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    #endregion

    #region Withdraw Tests

    [Fact]
    public async Task Withdraw_WithValidDataAndPin_ReturnsCreated()
    {
        // Arrange
        var (token, _, accountId) = await RegisterTestUserAsync();
        await SetPinAsync(token, "123456");
        await DepositAsync(token, accountId, 1000m);

        var request = new WithdrawRequest
        {
            AccountId = accountId,
            Amount = 200.00m,
            Pin = "123456",
            Description = "Test withdrawal"
        };

        // Act
        var response = await PostMonetaryAsync("/api/transactions/withdraw", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<WithdrawResponse>>(JsonOptions);
        result!.Data!.Transaction.Type.Should().Be(TransactionType.Withdrawal);
        result.Data.Transaction.Amount.Should().Be(200.00m);
        result.Data.NewBalance.Should().Be(800.00m);
    }

    [Fact]
    public async Task Withdraw_WithIncorrectPin_ReturnsUnauthorized()
    {
        // Arrange
        var (token, _, accountId) = await RegisterTestUserAsync();
        await SetPinAsync(token, "123456");
        await DepositAsync(token, accountId, 1000m);

        var request = new WithdrawRequest
        {
            AccountId = accountId,
            Amount = 200.00m,
            Pin = "654321", // Wrong PIN
            Description = "Test withdrawal"
        };

        // Act
        var response = await PostMonetaryAsync("/api/transactions/withdraw", request);

        // Assert - wrong PIN is a step-up authentication failure (401 per contract)
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Withdraw_InsufficientFunds_ReturnsUnprocessableEntity()
    {
        // Arrange
        var (token, _, accountId) = await RegisterTestUserAsync();
        await SetPinAsync(token, "123456");
        // No deposit - balance is 0

        var request = new WithdrawRequest
        {
            AccountId = accountId,
            Amount = 100.00m,
            Pin = "123456",
            Description = "Overdraft attempt"
        };

        // Act
        var response = await PostMonetaryAsync("/api/transactions/withdraw", request);

        // Assert - business-rule violations are 422 per contract (BusinessRuleException)
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Withdraw_WhenPinLocked_Returns429_AndMovesNoMoney()
    {
        var (token, _, accountId) = await RegisterTestUserAsync();
        await SetPinAsync(token, "123456");
        await DepositAsync(token, accountId, 1000m);

        var wrong = new WithdrawRequest { AccountId = accountId, Amount = 200m, Pin = "654321", Description = "x" };

        // Wrong PIN is 401 up to the threshold; the crossing attempt locks the PIN (429).
        for (var i = 0; i < ValidationRules.MaxPinAttempts - 1; i++)
        {
            (await PostMonetaryAsync("/api/transactions/withdraw", wrong)).StatusCode
                .Should().Be(HttpStatusCode.Unauthorized);
        }
        (await PostMonetaryAsync("/api/transactions/withdraw", wrong)).StatusCode
            .Should().Be(HttpStatusCode.TooManyRequests);

        // A CORRECT-PIN withdrawal is now blocked (429) - before any money moves.
        var correct = new WithdrawRequest { AccountId = accountId, Amount = 200m, Pin = "123456", Description = "x" };
        var blocked = await PostMonetaryAsync("/api/transactions/withdraw", correct);
        blocked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        blocked.Headers.RetryAfter.Should().NotBeNull("a lockout must advertise Retry-After");
        blocked.Headers.RetryAfter!.Delta.Should().NotBeNull();
        blocked.Headers.RetryAfter.Delta!.Value.Should().BeCloseTo(
            TimeSpan.FromMinutes(ValidationRules.PinLockoutMinutes), TimeSpan.FromSeconds(30));
        (await blocked.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.PinLocked);

        // No withdrawal transaction was created: the lock precedes the money movement.
        var list = await Client.GetAsync($"/api/transactions?accountId={accountId}&pageSize=50");
        list.EnsureSuccessStatusCode();
        var page = await list.Content.ReadFromJsonAsync<PaginatedResponse<TransactionResponse>>(JsonOptions);
        page!.Data.Count(t => t.Type == TransactionType.Withdrawal).Should().Be(0);
    }

    /// <summary>
    /// The complement of the test above, and the half a browser cannot prove: once the lockout
    /// window has passed, the withdrawal the user was refused actually SUCCEEDS against the server.
    ///
    /// The frontend has its own expiry coverage — `withdraw.test.tsx` with fake timers, and
    /// `e2e/pinLockExpiry.spec.ts` against a real 429 — but both advance the CLIENT's clock only.
    /// Playwright's `page.clock` moves one browser; the API's `lockedUntil` is real wall-clock time
    /// fifteen minutes out, so no browser test can reach the far side of the window. That property
    /// belongs here, where the lock can be aged directly.
    ///
    /// The lock is NOT fabricated: it is earned with real wrong PINs through the real endpoint, and
    /// only its `PinLockoutEnd` is then moved into the past — the same shape as
    /// `PinServiceTests.VerifyPinAsync_ExpiredLock_AllowsFreshAttempt`, one layer up.
    /// </summary>
    [Fact]
    public async Task Withdraw_AfterPinLockoutWindowPasses_Succeeds_AndMovesMoney()
    {
        var (token, userId, accountId) = await RegisterTestUserAsync();
        await SetPinAsync(token, "123456");
        await DepositAsync(token, accountId, 1000m);

        // Earn a genuine lock through the endpoint, exactly as a user would.
        var wrong = new WithdrawRequest { AccountId = accountId, Amount = 200m, Pin = "654321", Description = "x" };
        for (var i = 0; i < ValidationRules.MaxPinAttempts - 1; i++)
        {
            (await PostMonetaryAsync("/api/transactions/withdraw", wrong)).StatusCode
                .Should().Be(HttpStatusCode.Unauthorized);
        }
        (await PostMonetaryAsync("/api/transactions/withdraw", wrong)).StatusCode
            .Should().Be(HttpStatusCode.TooManyRequests);

        // Age the lock past its end. This is the ONLY thing simulated: the lock itself, the
        // failure counter and every code path below are the real ones.
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
            var locked = await db.Users.FindAsync(userId);
            locked.Should().NotBeNull("the lock must exist before it can be aged");
            locked!.PinLockoutEnd.Should().NotBeNull("the endpoint must have written a lockout end");
            locked.PinLockoutEnd = DateTimeOffset.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }

        // The withdrawal that was refused now goes through.
        var correct = new WithdrawRequest { AccountId = accountId, Amount = 200m, Pin = "123456", Description = "after lockout" };
        var response = await PostMonetaryAsync("/api/transactions/withdraw", correct);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<WithdrawResponse>>(JsonOptions);
        body!.Data!.NewBalance.Should().Be(800m, "1000 deposited minus the 200 that was locked out");

        // And the money really moved — the receipt is not the ledger.
        var list = await Client.GetAsync($"/api/transactions?accountId={accountId}&pageSize=50");
        list.EnsureSuccessStatusCode();
        var page = await list.Content.ReadFromJsonAsync<PaginatedResponse<TransactionResponse>>(JsonOptions);
        page!.Data.Count(t => t.Type == TransactionType.Withdrawal).Should().Be(1);

        // A successful verify clears the counter, so the next mistake starts a fresh window
        // rather than re-locking immediately.
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
            var unlocked = await db.Users.FindAsync(userId);
            unlocked!.PinAccessFailedCount.Should().Be(0);
            unlocked.PinLockoutEnd.Should().BeNull();
        }
    }

    #endregion

    #region List Transactions Tests

    [Fact]
    public async Task ListTransactions_WithValidToken_ReturnsPaginatedList()
    {
        // Arrange
        var (token, _, accountId) = await RegisterTestUserAsync();
        await DepositAsync(token, accountId, 100m);
        await DepositAsync(token, accountId, 200m);

        // Act
        var response = await Client.GetAsync("/api/transactions?Page=1&PageSize=10");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<PaginatedResponse<TransactionResponse>>(JsonOptions);
        result!.Data.Should().NotBeNull();
        result.Data.Should().HaveCountGreaterThanOrEqualTo(2);
        result.Pagination.Page.Should().Be(1);
        result.Pagination.PageSize.Should().Be(10);
    }

    [Fact]
    public async Task ListTransactions_WithAccountFilter_ReturnsFilteredList()
    {
        // Arrange
        var (token, _, accountId) = await RegisterTestUserAsync();
        await DepositAsync(token, accountId, 100m);

        // Act
        var response = await Client.GetAsync($"/api/transactions?AccountId={accountId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<PaginatedResponse<TransactionResponse>>(JsonOptions);
        result!.Data.Should().NotBeEmpty();
    }

    #endregion

    #region Get Transaction Tests

    [Fact]
    public async Task GetTransaction_WithValidId_ReturnsTransaction()
    {
        // Arrange
        var (token, _, accountId) = await RegisterTestUserAsync();
        SetAuthHeader(token);

        // Create a transaction
        var depositResponse = await PostMonetaryAsync("/api/transactions/deposit", new DepositRequest
        {
            AccountId = accountId,
            Amount = 100m,
            Description = "Test"
        });

        var depositResult = await depositResponse.Content.ReadFromJsonAsync<ApiResponse<DepositResponse>>(JsonOptions);
        var transactionId = depositResult!.Data!.Transaction.Id;

        // Act
        var response = await Client.GetAsync($"/api/transactions/{transactionId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<TransactionResponse>>(JsonOptions);
        result!.Data!.Id.Should().Be(transactionId);
        result.Data.Amount.Should().Be(100m);
    }

    #endregion

    #region Summary Tests

    [Fact]
    public async Task Summary_AggregatesTheUsersTransactions()
    {
        // Arrange — 1000 + 500 in, 200 out. Queried through an EXPLICIT ±1-day window so
        // the test cannot flake across a UTC month rollover (the default current-month
        // window has its own dedicated test).
        var (token, _, accountId) = await RegisterTestUserAsync();
        SetAuthHeader(token);
        await SetPinAsync(token, "123456");
        await DepositAsync(token, accountId, 1000m);
        await DepositAsync(token, accountId, 500m);

        var withdraw = new WithdrawRequest
        {
            AccountId = accountId,
            Amount = 200m,
            Pin = "123456",
            Description = "Summary test withdrawal"
        };
        (await PostMonetaryAsync("/api/transactions/withdraw", withdraw))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var fromDate = Uri.EscapeDataString(DateTime.UtcNow.AddDays(-1).ToString("O"));
        var toDate = Uri.EscapeDataString(DateTime.UtcNow.AddDays(1).ToString("O"));

        // Act
        var response = await Client.GetAsync(
            $"/api/transactions/summary?FromDate={fromDate}&ToDate={toDate}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content
            .ReadFromJsonAsync<ApiResponse<TransactionSummaryResponse>>(JsonOptions);
        result!.Data!.TotalIncome.Should().Be(1500m);
        result.Data.TotalExpenses.Should().Be(200m);
        result.Data.NetChange.Should().Be(1300m);
        result.Data.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task Summary_WithAccountId_ScopesToThatAccount()
    {
        // Arrange — two accounts on ONE user, money in both. The whole point of the parameter is
        // that these figures stop being merged, so the test has to have something to separate.
        var (token, _, firstAccountId) = await RegisterTestUserAsync();
        SetAuthHeader(token);
        await DepositAsync(token, firstAccountId, 1000m);

        // `JsonOptions` is not optional here: the enum is serialized as a STRING by the API's
        // converter, and the default serializer writes it as a number, which the model binder
        // rejects with a 400. The first run of this test failed on exactly that.
        var created = await Client.PostAsJsonAsync("/api/accounts", new CreateAccountRequest
        {
            Name = "Second",
            Type = AccountType.Savings
        }, JsonOptions);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var secondAccountId = (await created.Content
            .ReadFromJsonAsync<ApiResponse<AccountResponse>>(JsonOptions))!.Data!.Id;
        await DepositAsync(token, secondAccountId, 7m);

        var fromDate = Uri.EscapeDataString(DateTime.UtcNow.AddDays(-1).ToString("O"));
        var toDate = Uri.EscapeDataString(DateTime.UtcNow.AddDays(1).ToString("O"));

        // Act — scoped to the second account, then unscoped as the control.
        var scoped = await Client.GetAsync(
            $"/api/transactions/summary?AccountId={secondAccountId}&FromDate={fromDate}&ToDate={toDate}");
        var all = await Client.GetAsync(
            $"/api/transactions/summary?FromDate={fromDate}&ToDate={toDate}");

        // Assert — 7 alone, and 1007 together. Asserting both directions is what proves the
        // parameter narrows rather than that the fixture happened to hold one account.
        scoped.StatusCode.Should().Be(HttpStatusCode.OK);
        var scopedResult = await scoped.Content
            .ReadFromJsonAsync<ApiResponse<TransactionSummaryResponse>>(JsonOptions);
        scopedResult!.Data!.TotalIncome.Should().Be(7m);

        var allResult = await all.Content
            .ReadFromJsonAsync<ApiResponse<TransactionSummaryResponse>>(JsonOptions);
        allResult!.Data!.TotalIncome.Should().Be(1007m);
    }

    [Fact]
    public async Task Summary_WithAnotherUsersAccountId_ReturnsForbidden()
    {
        // Arrange — two REAL users, and the second asks about the first's account. Through the
        // full pipeline, because the status is the part the client sees and the service only
        // throws an exception; the mapping to 403 lives in the handler.
        var (ownerToken, _, ownersAccountId) = await RegisterTestUserAsync();
        SetAuthHeader(ownerToken);
        await DepositAsync(ownerToken, ownersAccountId, 4_242m);

        var (intruderToken, _, _) = await RegisterTestUserAsync();
        SetAuthHeader(intruderToken);

        // Act
        var response = await Client.GetAsync(
            $"/api/transactions/summary?AccountId={ownersAccountId}");

        // Assert — refused, and the body carries no figure from the account it refused.
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("4242");
    }

    [Fact]
    public async Task Summary_WithoutToken_ReturnsUnauthorized()
    {
        // Arrange
        ClearAuthHeader();

        // Act
        var response = await Client.GetAsync("/api/transactions/summary");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Summary_WithInvertedExplicitRange_ReturnsBadRequest()
    {
        // Arrange — both bounds provided and inverted → the filter's model validation
        var (token, _, _) = await RegisterTestUserAsync();
        SetAuthHeader(token);

        // Act
        var response = await Client.GetAsync(
            "/api/transactions/summary?FromDate=2026-02-01T00:00:00Z&ToDate=2026-01-01T00:00:00Z");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Summary_WithLoneFutureFromDate_ReturnsUnprocessableEntity()
    {
        // Arrange — only FromDate (in the future): model validation cannot see the pair,
        // so the service's resolved-window guard must answer 422 INVALID_DATE_RANGE.
        var (token, _, _) = await RegisterTestUserAsync();
        SetAuthHeader(token);
        var from = Uri.EscapeDataString(DateTime.UtcNow.AddDays(30).ToString("O"));

        // Act
        var response = await Client.GetAsync($"/api/transactions/summary?FromDate={from}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync()).Should().Contain("INVALID_DATE_RANGE");
    }

    [Fact]
    public async Task Summary_DefaultWindow_EchoesTheCurrentUtcMonth()
    {
        // Arrange — capture "now" on both sides of the call so a month rollover mid-test
        // cannot flake the assertion (the resolved month must match one of the captures).
        var (token, _, _) = await RegisterTestUserAsync();
        SetAuthHeader(token);
        var beforeUtc = DateTime.UtcNow;

        // Act
        var response = await Client.GetAsync("/api/transactions/summary");
        var afterUtc = DateTime.UtcNow;

        // Assert — the applied default window is observable in the response:
        // FromDate = first instant of the CURRENT UTC month, ToDate ≈ now.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content
            .ReadFromJsonAsync<ApiResponse<TransactionSummaryResponse>>(JsonOptions);
        var fromDate = result!.Data!.FromDate;
        fromDate.Day.Should().Be(1);
        fromDate.TimeOfDay.Should().Be(TimeSpan.Zero);
        var matchesACapturedMonth =
            (fromDate.Year == beforeUtc.Year && fromDate.Month == beforeUtc.Month)
            || (fromDate.Year == afterUtc.Year && fromDate.Month == afterUtc.Month);
        matchesACapturedMonth.Should().BeTrue(
            "the default FromDate must be the first day of the current UTC month");
        result.Data.ToDate.Should().BeOnOrAfter(fromDate);
        result.Data.ToDate.Should().BeCloseTo(afterUtc, TimeSpan.FromMinutes(1));
    }

    #endregion
}
