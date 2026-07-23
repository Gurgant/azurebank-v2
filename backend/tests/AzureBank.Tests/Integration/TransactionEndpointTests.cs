using System.Net;
using System.Net.Http.Json;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transaction;
using AzureBank.Shared.Enums;
using AzureBank.Tests.Fixtures;
using FluentAssertions;

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
        // Arrange — 1000 + 500 in, 200 out (all inside the default current-month window)
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

        // Act
        var response = await Client.GetAsync("/api/transactions/summary");

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
        // Arrange
        var (token, _, _) = await RegisterTestUserAsync();
        SetAuthHeader(token);

        // Act
        var response = await Client.GetAsync("/api/transactions/summary");

        // Assert — the applied default window is observable in the response
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content
            .ReadFromJsonAsync<ApiResponse<TransactionSummaryResponse>>(JsonOptions);
        result!.Data!.FromDate.Day.Should().Be(1);
        result.Data.FromDate.Should().BeOnOrBefore(result.Data.ToDate);
    }

    #endregion
}
