using System.Net;
using System.Net.Http.Json;
using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.Enums;
using AzureBank.Tests.Fixtures;
using FluentAssertions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Integration tests for Account endpoints.
/// Tests: /api/accounts (GET, POST), /api/accounts/{id} (GET, PATCH, DELETE), /api/accounts/{id}/balance
/// </summary>
public class AccountEndpointTests : IntegrationTestBase
{
    public AccountEndpointTests(CustomWebApplicationFactory factory) : base(factory) { }

    #region List Accounts Tests

    [Fact]
    public async Task ListAccounts_WithValidToken_ReturnsAccounts()
    {
        // Arrange
        var (token, _, _) = await RegisterTestUserAsync();
        SetAuthHeader(token);

        // Act
        var response = await Client.GetAsync("/api/accounts");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<List<AccountResponse>>>(JsonOptions);
        result!.Data.Should().NotBeNull();
        result.Data.Should().HaveCountGreaterThanOrEqualTo(1); // At least primary account
        result.Data!.First().IsPrimary.Should().BeTrue();
    }

    [Fact]
    public async Task ListAccounts_WithoutToken_ReturnsUnauthorized()
    {
        // Arrange
        ClearAuthHeader();

        // Act
        var response = await Client.GetAsync("/api/accounts");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    #endregion

    #region Create Account Tests

    [Fact]
    public async Task CreateAccount_WithValidData_ReturnsCreated()
    {
        // Arrange
        var (token, _, _) = await RegisterTestUserAsync();
        SetAuthHeader(token);

        var request = new CreateAccountRequest
        {
            Name = "Test Savings Account",
            Type = AccountType.Savings
        };

        // Act
        var response = await Client.PostAsJsonAsync("/api/accounts", request, JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<AccountResponse>>(JsonOptions);
        result!.Data!.Name.Should().Be("Test Savings Account");
        result.Data.Type.Should().Be(AccountType.Savings);
        result.Data.Balance.Should().Be(0);
        result.Data.IsPrimary.Should().BeFalse();
    }

    [Fact]
    public async Task CreateAccount_WithInvalidName_ReturnsBadRequest()
    {
        // Arrange
        var (token, _, _) = await RegisterTestUserAsync();
        SetAuthHeader(token);

        var request = new CreateAccountRequest
        {
            Name = "X", // Too short
            Type = AccountType.Savings
        };

        // Act
        var response = await Client.PostAsJsonAsync("/api/accounts", request, JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    #endregion

    #region Get Account Tests

    [Fact]
    public async Task GetAccount_WithValidId_ReturnsAccount()
    {
        // Arrange
        var (token, _, accountId) = await RegisterTestUserAsync();
        SetAuthHeader(token);

        // Act
        var response = await Client.GetAsync($"/api/accounts/{accountId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<AccountResponse>>(JsonOptions);
        result!.Data!.Id.Should().Be(accountId);
    }

    [Fact]
    public async Task GetAccount_WithInvalidId_ReturnsNotFound()
    {
        // Arrange
        var (token, _, _) = await RegisterTestUserAsync();
        SetAuthHeader(token);

        // Act
        var response = await Client.GetAsync($"/api/accounts/{Guid.NewGuid()}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetAccount_BelongingToOtherUser_ReturnsForbidden()
    {
        // Arrange - Create two users
        var (token1, _, accountId1) = await RegisterTestUserAsync();
        var (token2, _, _) = await RegisterTestUserAsync();

        // Act - User 2 tries to access User 1's account
        SetAuthHeader(token2);
        var response = await Client.GetAsync($"/api/accounts/{accountId1}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    #endregion

    #region Get Balance Tests

    [Fact]
    public async Task GetBalance_CurrentBalance_ReturnsBalanceInfo()
    {
        // Arrange
        var (token, _, accountId) = await RegisterTestUserAsync();
        SetAuthHeader(token);

        // Act
        var response = await Client.GetAsync($"/api/accounts/{accountId}/balance");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<BalanceResponse>>(JsonOptions);
        result!.Data!.AccountId.Should().Be(accountId);
        result.Data.Currency.Should().Be("EUR");
        result.Data.IsHistorical.Should().BeFalse();
    }

    [Fact]
    public async Task GetBalance_HistoricalBalance_ReturnsHistoricalBalanceInfo()
    {
        // Arrange
        var (token, _, accountId) = await RegisterTestUserAsync();
        SetAuthHeader(token);
        var historicalDate = DateTime.UtcNow.AddDays(-1).ToString("o");

        // Act
        var response = await Client.GetAsync($"/api/accounts/{accountId}/balance?at={historicalDate}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<BalanceResponse>>(JsonOptions);
        result!.Data!.IsHistorical.Should().BeTrue();
    }

    #endregion

    #region Update Account Tests

    [Fact]
    public async Task UpdateAccount_WithValidData_ReturnsUpdatedAccount()
    {
        // Arrange
        var (token, _, accountId) = await RegisterTestUserAsync();
        SetAuthHeader(token);

        var request = new UpdateAccountRequest
        {
            Name = "Updated Account Name"
        };

        // Act
        var response = await Client.PatchAsJsonAsync($"/api/accounts/{accountId}", request, JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<AccountResponse>>(JsonOptions);
        result!.Data!.Name.Should().Be("Updated Account Name");
    }

    #endregion

    #region Set Primary Account Tests

    [Fact]
    public async Task SetPrimaryAccount_WithValidAccount_ReturnsOk()
    {
        // Arrange
        var (token, _, _) = await RegisterTestUserAsync();
        SetAuthHeader(token);

        // Create second account
        var createResponse = await Client.PostAsJsonAsync("/api/accounts", new CreateAccountRequest
        {
            Name = "Secondary Account",
            Type = AccountType.Savings
        }, JsonOptions);
        var newAccount = await createResponse.Content.ReadFromJsonAsync<ApiResponse<AccountResponse>>(JsonOptions);

        // Act
        var response = await Client.PatchAsync($"/api/accounts/{newAccount!.Data!.Id}/set-primary", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify new account is primary
        var getResponse = await Client.GetAsync($"/api/accounts/{newAccount.Data.Id}");
        var account = await getResponse.Content.ReadFromJsonAsync<ApiResponse<AccountResponse>>(JsonOptions);
        account!.Data!.IsPrimary.Should().BeTrue();
    }

    #endregion
}
