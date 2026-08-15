using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transaction;
using AzureBank.Tests.Fixtures;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Base class for integration tests providing common functionality
/// like authentication helpers and HTTP client setup.
///
/// Implements IClassFixture for shared WebApplicationFactory across tests.
/// </summary>
public abstract class IntegrationTestBase : IClassFixture<CustomWebApplicationFactory>
{
    protected readonly HttpClient Client;
    protected readonly CustomWebApplicationFactory Factory;

    /// <summary>
    /// JSON options matching the API wire contract: enums as PascalCase strings.
    /// The System.Net.Http.Json defaults serialize enums as integers, which the
    /// API's StrictJsonStringEnumConverter rejects (and cannot read back the
    /// string values the API emits).
    /// </summary>
    protected static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        };

    protected IntegrationTestBase(CustomWebApplicationFactory factory)
    {
        Factory = factory;
        Client = factory.CreateClient();
    }

    /// <summary>
    /// Registers a new test user and returns the authentication token.
    /// Each call creates a unique user to ensure test isolation.
    /// </summary>
    /// <summary>The password every helper-created user gets — one place, so proofs can reuse it.</summary>
    protected const string TestUserPassword = "TestPass123!";

    protected async Task<(string Token, Guid UserId, Guid AccountId)> RegisterTestUserAsync()
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];

        var response = await Client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"test_user_{uniqueId}",
            Email = $"test{uniqueId}@example.com",
            Password = TestUserPassword,
            FirstName = "Test",
            LastName = "User"
        }, JsonOptions);

        response.EnsureSuccessStatusCode();

        var result = await response.Content
            .ReadFromJsonAsync<ApiResponse<RegisterResponse>>(JsonOptions);

        return (
            result!.Data!.Token.AccessToken,
            result.Data.User.Id,
            result.Data.Account.Id
        );
    }

    /// <summary>
    /// Sets the Authorization header for subsequent requests.
    /// </summary>
    protected void SetAuthHeader(string token)
    {
        Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>
    /// Clears the Authorization header.
    /// </summary>
    protected void ClearAuthHeader()
    {
        Client.DefaultRequestHeaders.Authorization = null;
    }

    /// <summary>
    /// Sets PIN for the authenticated user.
    /// </summary>
    protected async Task SetPinAsync(string token, string pin = "123456", string password = TestUserPassword)
    {
        SetAuthHeader(token);
        // The password is not optional bookkeeping: enrolling a PIN requires proving it (T7/#201).
        // Defaulted to what RegisterTestUserAsync uses so the thirty-odd existing call sites keep
        // reading as "give this user a PIN" rather than sprouting a credential each.
        var response = await Client.PostAsJsonAsync(
            "/api/auth/pin", new SetPinRequest { Pin = pin, Password = password }, JsonOptions);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Deposits money into an account.
    /// </summary>
    protected async Task<decimal> DepositAsync(string token, Guid accountId, decimal amount)
    {
        SetAuthHeader(token);
        var response = await PostMonetaryAsync("/api/transactions/deposit", new DepositRequest
        {
            AccountId = accountId,
            Amount = amount,
            Description = "Test deposit"
        });

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<DepositResponse>>(JsonOptions);
        return result!.Data!.NewBalance;
    }

    /// <summary>
    /// POSTs JSON to a monetary endpoint with an Idempotency-Key header
    /// (required per ADR-0009). Defaults to a fresh UUID per call so each
    /// test call keeps its original execute-once semantics; pass an explicit
    /// key to exercise replay/conflict behavior.
    /// </summary>
    protected Task<HttpResponseMessage> PostMonetaryAsync<T>(
        string url, T payload, Guid? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };
        request.Headers.Add(
            IdempotencyConstants.HeaderName,
            (idempotencyKey ?? Guid.NewGuid()).ToString());
        return Client.SendAsync(request);
    }
}
