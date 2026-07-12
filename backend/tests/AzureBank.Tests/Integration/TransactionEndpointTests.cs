using System.Net;
using System.Net.Http.Json;
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
        var response = await Client.PostAsJsonAsync("/api/transactions/deposit", request, JsonOptions);

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
        var response = await Client.PostAsJsonAsync("/api/transactions/deposit", request, JsonOptions);

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
        var response = await Client.PostAsJsonAsync("/api/transactions/deposit", request, JsonOptions);

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
        var response = await Client.PostAsJsonAsync("/api/transactions/withdraw", request, JsonOptions);

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
        var response = await Client.PostAsJsonAsync("/api/transactions/withdraw", request, JsonOptions);

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
        var response = await Client.PostAsJsonAsync("/api/transactions/withdraw", request, JsonOptions);

        // Assert - business-rule violations are 422 per contract (BusinessRuleException)
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
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
        var depositResponse = await Client.PostAsJsonAsync("/api/transactions/deposit", new DepositRequest
        {
            AccountId = accountId,
            Amount = 100m,
            Description = "Test"
        }, JsonOptions);

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
}
