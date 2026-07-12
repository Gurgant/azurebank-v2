using System.Net;
using System.Net.Http.Json;
using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transfer;
using AzureBank.Shared.Enums;
using AzureBank.Tests.Fixtures;
using FluentAssertions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Integration tests for Transfer endpoints.
/// Tests: /api/transfers (POST), /api/transfers/internal (POST)
/// </summary>
public class TransferEndpointTests : IntegrationTestBase
{
    public TransferEndpointTests(CustomWebApplicationFactory factory) : base(factory) { }

    #region External Transfer Tests

    [Fact]
    public async Task Transfer_ToExistingUser_ReturnsCreated()
    {
        // Arrange - Create sender and recipient
        var (senderToken, _, senderAccountId) = await RegisterTestUserAsync();
        var recipientData = await RegisterRecipientAsync();

        // Fund sender account
        await DepositAsync(senderToken, senderAccountId, 1000m);

        SetAuthHeader(senderToken);
        var request = new TransferRequest
        {
            FromAccountId = senderAccountId,
            RecipientAzureTag = recipientData.AzureTag,
            Amount = 100m,
            Description = "Test transfer"
        };

        // Act
        var response = await Client.PostAsJsonAsync("/api/transfers", request, JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<TransferResponse>>(JsonOptions);
        result!.Data!.Amount.Should().Be(100m);
        result.Data.NewBalance.Should().Be(900m);
        result.Data.RecipientAzureTag.Should().Be(recipientData.AzureTag);
    }

    [Fact]
    public async Task Transfer_ToNonExistentUser_ReturnsNotFound()
    {
        // Arrange
        var (token, _, accountId) = await RegisterTestUserAsync();
        await DepositAsync(token, accountId, 1000m);

        SetAuthHeader(token);
        var request = new TransferRequest
        {
            FromAccountId = accountId,
            // Valid-format tag (passes validation) that no registered user owns
            RecipientAzureTag = "nonexistent_user_999",
            Amount = 100m,
            Description = "Test transfer"
        };

        // Act
        var response = await Client.PostAsJsonAsync("/api/transfers", request, JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Transfer_InsufficientFunds_ReturnsUnprocessableEntity()
    {
        // Arrange
        var (senderToken, _, senderAccountId) = await RegisterTestUserAsync();
        var recipientData = await RegisterRecipientAsync();

        // No deposit - balance is 0
        SetAuthHeader(senderToken);
        var request = new TransferRequest
        {
            FromAccountId = senderAccountId,
            RecipientAzureTag = recipientData.AzureTag,
            Amount = 100m,
            Description = "Overdraft attempt"
        };

        // Act
        var response = await Client.PostAsJsonAsync("/api/transfers", request, JsonOptions);

        // Assert - business-rule violations are 422 per contract (BusinessRuleException)
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    #endregion

    #region Internal Transfer Tests

    [Fact]
    public async Task InternalTransfer_BetweenOwnAccounts_ReturnsCreated()
    {
        // Arrange
        var (token, _, primaryAccountId) = await RegisterTestUserAsync();
        SetAuthHeader(token);

        // Create second account
        var createResponse = await Client.PostAsJsonAsync("/api/accounts", new CreateAccountRequest
        {
            Name = "Savings",
            Type = AccountType.Savings
        }, JsonOptions);
        var secondAccount = await createResponse.Content.ReadFromJsonAsync<ApiResponse<AccountResponse>>(JsonOptions);

        // Fund primary account
        await DepositAsync(token, primaryAccountId, 1000m);

        var request = new InternalTransferRequest
        {
            FromAccountId = primaryAccountId,
            ToAccountId = secondAccount!.Data!.Id,
            Amount = 300m,
            Description = "Move to savings"
        };

        // Act
        var response = await Client.PostAsJsonAsync("/api/transfers/internal", request, JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<InternalTransferResponse>>(JsonOptions);
        result!.Data!.Amount.Should().Be(300m);
        result.Data.FromAccountNewBalance.Should().Be(700m);
        result.Data.ToAccountNewBalance.Should().Be(300m);
    }

    [Fact]
    public async Task InternalTransfer_ToOtherUsersAccount_ReturnsForbidden()
    {
        // Arrange
        var (token1, _, accountId1) = await RegisterTestUserAsync();
        var (token2, _, accountId2) = await RegisterTestUserAsync();

        await DepositAsync(token1, accountId1, 1000m);

        SetAuthHeader(token1);
        var request = new InternalTransferRequest
        {
            FromAccountId = accountId1,
            ToAccountId = accountId2, // Other user's account
            Amount = 100m,
            Description = "Unauthorized transfer"
        };

        // Act
        var response = await Client.PostAsJsonAsync("/api/transfers/internal", request, JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task InternalTransfer_SameAccount_ReturnsBadRequest()
    {
        // Arrange
        var (token, _, accountId) = await RegisterTestUserAsync();
        await DepositAsync(token, accountId, 1000m);

        SetAuthHeader(token);
        var request = new InternalTransferRequest
        {
            FromAccountId = accountId,
            ToAccountId = accountId, // Same account
            Amount = 100m,
            Description = "Self transfer"
        };

        // Act
        var response = await Client.PostAsJsonAsync("/api/transfers/internal", request, JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    #endregion

    #region Helper Methods

    private async Task<(string AzureTag, Guid UserId, Guid AccountId)> RegisterRecipientAsync()
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var azureTag = $"recipient_{uniqueId}";

        var response = await Client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = azureTag,
            Email = $"recipient{uniqueId}@example.com",
            Password = "TestPass123!",
            FirstName = "Recipient",
            LastName = "User"
        }, JsonOptions);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(JsonOptions);
        return (azureTag, result!.Data!.User.Id, result.Data.Account.Id);
    }

    #endregion
}
