using System.Net;
using System.Net.Http.Json;
using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transfer;
using AzureBank.Shared.Enums;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using System.Text.Json;
using AzureBank.Shared.Constants;

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
        var response = await PostMonetaryAsync("/api/transfers", request);

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
        var response = await PostMonetaryAsync("/api/transfers", request);

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
        var response = await PostMonetaryAsync("/api/transfers", request);

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
        var response = await PostMonetaryAsync("/api/transfers/internal", request);

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
        var response = await PostMonetaryAsync("/api/transfers/internal", request);

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
        var response = await PostMonetaryAsync("/api/transfers/internal", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task InternalTransfer_SameAccount_AnswersAsVALIDATION_NotAsTheServicesBusinessRule()
    {
        // The status alone (asserted above) does not say WHERE the 400 comes from, and that gap has
        // already cost us once. TransferService throws BusinessRuleException(SAME_ACCOUNT_TRANSFER)
        // for from == to, and the unit test pins that throw by calling the service directly. Read
        // together, the two suites imply the API answers 422 SAME_ACCOUNT_TRANSFER — and the
        // frontend was written to expect exactly that.
        //
        // It cannot arrive. InternalTransferRequestValidator's NotEqual rule runs during MODEL
        // validation, before the action, so the wire answer is a validation 400: an `errors`
        // dictionary keyed by field, and NO errorCode at all. The service check behind it is
        // deliberate defence in depth for non-HTTP callers ("should be caught by validator, but
        // double-check") and stays.
        //
        // This test pins the SHAPE so nobody wires a client to a code the API cannot emit.
        var (token, _, accountId) = await RegisterTestUserAsync();
        await DepositAsync(token, accountId, 1000m);

        SetAuthHeader(token);
        var response = await PostMonetaryAsync("/api/transfers/internal", new InternalTransferRequest
        {
            FromAccountId = accountId,
            ToAccountId = accountId,
            Amount = 100m
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        problem.TryGetProperty("errorCode", out _).Should().BeFalse(
            because: "a validation 400 carries an errors dictionary instead of an errorCode");
        problem.GetProperty("errors").GetProperty("toAccountId")[0].GetString()
            .Should().Be("Cannot transfer to the same account.");
    }

    [Fact]
    public async Task Transfer_UnknownRecipient_CarriesTheCodeAndSentence_NotTheHandle()
    {
        // The regression test for the NotFoundException overload trap, at the level the bug was
        // actually observed: over HTTP.
        //
        // `new NotFoundException("Recipient", request.RecipientAzureTag)` used to bind to
        // (string message, string errorCode) because the identifier is a string, so the wire
        // carried {"detail":"Recipient","errorCode":"<the handle the caller typed>"}. Every unit
        // test passed, because they all pass a Guid.
        //
        // Two things are asserted, and the second is the one that matters: the errorCode must be a
        // CONSTANT, never an echo of caller input, or a client switching on it can never match and
        // an unvalidated string leaks into a field consumers treat as a closed enum.
        var (token, _, accountId) = await RegisterTestUserAsync();
        await DepositAsync(token, accountId, 1000m);

        const string unknownHandle = "nobody_here_at_all";
        SetAuthHeader(token);
        var response = await PostMonetaryAsync("/api/transfers", new TransferRequest
        {
            FromAccountId = accountId,
            RecipientAzureTag = unknownHandle,
            Amount = 100m
        });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        problem.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.AccountNotFound);
        problem.GetProperty("detail").GetString()
            .Should().Be($"Recipient with identifier '{unknownHandle}' was not found.");
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
