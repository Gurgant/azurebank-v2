using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    protected async Task<(string Token, Guid UserId, Guid AccountId)> RegisterTestUserAsync()
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];

        var response = await Client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"test_user_{uniqueId}",
            Email = $"test{uniqueId}@example.com",
            Password = "TestPass123!",
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
    protected async Task SetPinAsync(string token, string pin = "123456")
    {
        SetAuthHeader(token);
        var response = await Client.PostAsJsonAsync("/api/auth/pin", new SetPinRequest { Pin = pin }, JsonOptions);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Deposits money into an account.
    /// </summary>
    protected async Task<decimal> DepositAsync(string token, Guid accountId, decimal amount)
    {
        SetAuthHeader(token);
        var response = await Client.PostAsJsonAsync("/api/transactions/deposit", new DepositRequest
        {
            AccountId = accountId,
            Amount = amount,
            Description = "Test deposit"
        }, JsonOptions);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<DepositResponse>>(JsonOptions);
        return result!.Data!.NewBalance;
    }
}
