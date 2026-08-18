using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transaction;
using AzureBank.Shared.DTOs.Transfer;
using AzureBank.Tests.Fixtures;
using FluentAssertions;

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
    /// <param name="url">The monetary endpoint under test.</param>
    /// <param name="payload">The request body, serialised with the API's wire options.</param>
    /// <param name="idempotencyKey">Explicit key for replay/conflict cases; a fresh one otherwise.</param>
    /// <param name="stepUpAuthorizationId">
    /// The <c>Step-Up-Authorization</c> header, required by both transfer endpoints since ADR-0042.
    /// A HEADER and never part of <paramref name="payload"/>: the idempotency fingerprint is
    /// computed over the body alone, so putting it there would make the same transfer resent with a
    /// different authorisation a 422 <c>IDEMPOTENCY_KEY_REUSE</c> instead of reaching the endpoint.
    /// Omit it to send none, which is what the refusal tests do.
    /// </param>
    protected Task<HttpResponseMessage> PostMonetaryAsync<T>(
        string url, T payload, Guid? idempotencyKey = null, Guid? stepUpAuthorizationId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };
        request.Headers.Add(
            IdempotencyConstants.HeaderName,
            (idempotencyKey ?? Guid.NewGuid()).ToString());
        if (stepUpAuthorizationId is { } authorizationId)
        {
            request.Headers.Add(StepUpConstants.HeaderName, authorizationId.ToString());
        }
        return Client.SendAsync(request);
    }

    /// <summary>
    /// A reference that IS presented and is worth nothing: syntactically an authorisation, bound to
    /// no movement. It clears the "none presented" refusal and would be turned away
    /// <c>401 AUTHORIZATION_INVALID</c> by the binding check.
    /// </summary>
    /// <remarks>
    /// For tests whose subject is a refusal that must happen BEFORE the authorisation is validated —
    /// an unknown recipient, a payee with no account. Using a worthless reference is what makes them
    /// say so: if that ordering ever inverts they stop reporting their own refusal and start
    /// reporting AUTHORIZATION_INVALID, instead of quietly passing for a new reason.
    /// </remarks>
    protected static Guid PresentedAuthorization() => Guid.CreateVersion7();

    /// <summary>
    /// Spends the PIN at the external mint endpoint and returns the authorisation bound to exactly
    /// this movement — the only thing <c>POST /api/transfers</c> accepts as a second factor since
    /// ADR-0042.
    /// </summary>
    /// <remarks>
    /// Goes through the real endpoint rather than the service, so a test that transfers is also
    /// exercising the mint it depends on. The amount must match the transfer's to the cent: the
    /// binding is an HMAC over (operation, payer, source account, payee, amount) and a mismatch is
    /// refused <c>401 AUTHORIZATION_INVALID</c>, which is the point of the whole mechanism.
    /// </remarks>
    protected async Task<Guid> AuthoriseTransferAsync(
        Guid fromAccountId, string recipientAzureTag, decimal amount, string pin = "123456")
    {
        var response = await Client.PostAsJsonAsync(
            "/api/transfers/authorizations",
            new TransferAuthorizationRequest
            {
                FromAccountId = fromAccountId,
                RecipientAzureTag = recipientAzureTag,
                Amount = amount,
                Pin = pin
            },
            JsonOptions);

        response.StatusCode.Should().Be(
            HttpStatusCode.Created,
            "a transfer cannot be sent without a minted authorisation, so a failure here would "
            + "surface as an unrelated 401 on the transfer under test");

        var body = await response.Content
            .ReadFromJsonAsync<ApiResponse<StepUpAuthorizationResponse>>(JsonOptions);
        return body!.Data!.AuthorizationId;
    }

    /// <summary>The same, for a move between the caller's own accounts.</summary>
    protected async Task<Guid> AuthoriseInternalTransferAsync(
        Guid fromAccountId, Guid toAccountId, decimal amount, string pin = "123456")
    {
        var response = await Client.PostAsJsonAsync(
            "/api/transfers/internal/authorizations",
            new InternalTransferAuthorizationRequest
            {
                FromAccountId = fromAccountId,
                ToAccountId = toAccountId,
                Amount = amount,
                Pin = pin
            },
            JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content
            .ReadFromJsonAsync<ApiResponse<StepUpAuthorizationResponse>>(JsonOptions);
        return body!.Data!.AuthorizationId;
    }
}
