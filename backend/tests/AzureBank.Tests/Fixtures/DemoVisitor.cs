using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transaction;
using AzureBank.Shared.DTOs.Transfer;
using FluentAssertions;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// One signed-in user driving the in-process API the way a visitor's session does: every request
/// carries this user's bearer token, so two visitors can share one <see cref="HttpClient"/>.
/// </summary>
/// <remarks>
/// The demo pool's SQL Server tests use it for what a copy's owner does before the copy is deleted
/// or looked at from another copy: transfers, a closed account, a changed PIN. Each call returns the
/// response, so a test states the status it expects where it matters.
/// </remarks>
internal sealed class DemoVisitor
{
    public static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly HttpClient _client;
    private readonly string _token;

    private DemoVisitor(HttpClient client, string token, Guid userId)
    {
        _client = client;
        _token = token;
        UserId = userId;
    }

    public Guid UserId { get; }

    /// <summary><c>POST /api/auth/login</c>, as it answered.</summary>
    public static Task<HttpResponseMessage> TrySignInAsync(HttpClient client, string email, string password) =>
        client.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = email, Password = password }, Json);

    /// <summary>Signs in, and fails the test when the API refuses.</summary>
    public static async Task<DemoVisitor> SignInAsync(HttpClient client, string email, string password)
    {
        using var response = await TrySignInAsync(client, email, password);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "ARRANGE: this user has a password and signs in with it");
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>(Json);
        return new DemoVisitor(client, body!.Data!.Token.AccessToken, body.Data.User.Id);
    }

    /// <summary>
    /// <c>POST /api/auth/demo/claim</c>, as it answered: the BFF's own client asking for a free copy
    /// for the visitor at <paramref name="clientAddress"/>.
    /// </summary>
    public static Task<HttpResponseMessage> ClaimAsync(HttpClient client, string clientAddress = "203.0.113.7") =>
        client.PostAsJsonAsync("/api/auth/demo/claim", new DemoClaimRequest { ClientAddress = clientAddress }, Json);

    /// <summary>What a claim answered with, and a failed test when it did not answer 200.</summary>
    public static async Task<DemoClaimResponse> ClaimedAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(
            HttpStatusCode.OK, "a free copy is there to be claimed ({0})", await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ApiResponse<DemoClaimResponse>>(Json))!.Data!;
    }

    /// <summary>
    /// The owner of the copy a claim answered, on the session that claim opened: no sign-in of its
    /// own.
    /// </summary>
    public static DemoVisitor OfClaim(HttpClient client, DemoClaimResponse claim) =>
        new(client, claim.Token.AccessToken, claim.User.Id);

    /// <summary>The <c>errorCode</c> of a refusal's body, or null when the body carries none.</summary>
    public static async Task<string?> ErrorCodeOfAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return body.Length > 0
            && JsonNode.Parse(body) is JsonObject json
            && json.TryGetPropertyValue("errorCode", out var code)
                ? code?.GetValue<string>()
                : null;
    }

    /// <summary>
    /// Registers a user outside every copy, with a PIN, and returns it with its handle and its one
    /// account.
    /// </summary>
    public static async Task<(DemoVisitor Visitor, string AzureTag, Guid AccountId)> RegisterAsync(
        HttpClient client, string prefix, string pin = "123456")
    {
        const string password = "TestPass123!";
        var unique = Guid.NewGuid().ToString("N")[..8];
        var handle = $"{prefix}_{unique}";
        using var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = handle,
            Email = $"{prefix}{unique}@example.com",
            Password = password,
            FirstName = "Outside",
            LastName = "Anycopy",
        }, Json);
        response.StatusCode.Should().Be(HttpStatusCode.Created, "ARRANGE: registration is open outside the demo");
        var registered = await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json);

        var visitor = new DemoVisitor(client, registered!.Data!.Token.AccessToken, registered.Data.User.Id);
        using var enrolled = await visitor.SendAsync(
            HttpMethod.Post, "/api/auth/pin", new SetPinRequest { Pin = pin, Password = password });
        enrolled.EnsureSuccessStatusCode();
        return (visitor, handle, registered.Data.Account.Id);
    }

    /// <summary>The caller's open accounts.</summary>
    public async Task<List<AccountResponse>> AccountsAsync()
    {
        using var response = await SendAsync<object>(HttpMethod.Get, "/api/accounts");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ApiResponse<List<AccountResponse>>>(Json))!.Data!;
    }

    /// <summary><c>GET /api/accounts</c>, as it answered.</summary>
    public Task<HttpResponseMessage> ListAccountsAsync() =>
        SendAsync<object>(HttpMethod.Get, "/api/accounts");

    /// <summary><c>GET /api/accounts/{id}/full-number</c>: the reveal of an account's whole number.</summary>
    public Task<HttpResponseMessage> RevealAsync(Guid accountId) =>
        SendAsync<object>(HttpMethod.Get, $"/api/accounts/{accountId}/full-number");

    /// <summary>
    /// <c>POST /api/transactions/deposit</c>, under a key of its own unless
    /// <paramref name="idempotencyKey"/> names one: the same key and the same deposit again is a
    /// retry, which the API answers from what it stored.
    /// </summary>
    public Task<HttpResponseMessage> DepositAsync(Guid accountId, decimal amount, Guid? idempotencyKey = null) =>
        SendAsync(
            HttpMethod.Post,
            "/api/transactions/deposit",
            new DepositRequest { AccountId = accountId, Amount = amount, Description = "Test deposit" },
            idempotencyKey: idempotencyKey ?? Guid.NewGuid());

    /// <summary><c>POST /api/auth/pin/verify</c>.</summary>
    public Task<HttpResponseMessage> VerifyPinAsync(string pin) =>
        SendAsync(HttpMethod.Post, "/api/auth/pin/verify", new VerifyPinRequest { Pin = pin });

    /// <summary><c>POST /api/auth/logout</c>: sign out of every session.</summary>
    public Task<HttpResponseMessage> SignOutEverywhereAsync() =>
        SendAsync<object>(HttpMethod.Post, "/api/auth/logout");

    /// <summary>Any other request as this user, with no body.</summary>
    public Task<HttpResponseMessage> RequestAsync(HttpMethod method, string url) =>
        SendAsync<object>(method, url);

    /// <summary><c>GET /api/users/{handle}</c>: the recipient lookup.</summary>
    public Task<HttpResponseMessage> LookupAsync(string handle) =>
        SendAsync<object>(HttpMethod.Get, $"/api/users/{handle}");

    /// <summary><c>POST /api/transfers/authorizations</c>: the PIN spent on one transfer.</summary>
    public Task<HttpResponseMessage> MintTransferAsync(Guid fromAccountId, string handle, decimal amount, string pin = "123456") =>
        SendAsync(HttpMethod.Post, "/api/transfers/authorizations", new TransferAuthorizationRequest
        {
            FromAccountId = fromAccountId,
            RecipientAzureTag = handle,
            Amount = amount,
            Pin = pin,
        });

    /// <summary><c>POST /api/transfers</c>, presenting <paramref name="authorizationId"/>.</summary>
    public Task<HttpResponseMessage> TransferAsync(Guid fromAccountId, string handle, decimal amount, Guid authorizationId) =>
        SendAsync(
            HttpMethod.Post,
            "/api/transfers",
            new TransferRequest { FromAccountId = fromAccountId, RecipientAzureTag = handle, Amount = amount },
            authorizationId,
            idempotencyKey: Guid.NewGuid());

    /// <summary><c>POST /api/transfers/internal/authorizations</c>.</summary>
    public Task<HttpResponseMessage> MintInternalTransferAsync(Guid fromAccountId, Guid toAccountId, decimal amount, string pin = "123456") =>
        SendAsync(HttpMethod.Post, "/api/transfers/internal/authorizations", new InternalTransferAuthorizationRequest
        {
            FromAccountId = fromAccountId,
            ToAccountId = toAccountId,
            Amount = amount,
            Pin = pin,
        });

    /// <summary><c>POST /api/transfers/internal</c>, presenting <paramref name="authorizationId"/>.</summary>
    public Task<HttpResponseMessage> InternalTransferAsync(Guid fromAccountId, Guid toAccountId, decimal amount, Guid authorizationId) =>
        SendAsync(
            HttpMethod.Post,
            "/api/transfers/internal",
            new InternalTransferRequest { FromAccountId = fromAccountId, ToAccountId = toAccountId, Amount = amount },
            authorizationId,
            idempotencyKey: Guid.NewGuid());

    /// <summary><c>POST /api/accounts/{id}/deletion-authorizations</c>.</summary>
    public Task<HttpResponseMessage> MintClosureAsync(Guid accountId, string pin = "123456") =>
        SendAsync(
            HttpMethod.Post,
            $"/api/accounts/{accountId}/deletion-authorizations",
            new AccountDeletionAuthorizationRequest { Pin = pin });

    /// <summary><c>DELETE /api/accounts/{id}</c>, presenting <paramref name="authorizationId"/>.</summary>
    public Task<HttpResponseMessage> CloseAccountAsync(Guid accountId, Guid authorizationId) =>
        SendAsync<object>(HttpMethod.Delete, $"/api/accounts/{accountId}", authorizationId: authorizationId);

    /// <summary><c>POST /api/auth/pin</c>, replacing the PIN the user has.</summary>
    public Task<HttpResponseMessage> ChangePinAsync(string currentPin, string newPin) =>
        SendAsync(HttpMethod.Post, "/api/auth/pin", new SetPinRequest { Pin = newPin, CurrentPin = currentPin });

    /// <summary>The authorisation a mint answered, and a failed test when it did not answer 201.</summary>
    public static async Task<Guid> AuthorizationOfAsync(Task<HttpResponseMessage> mint)
    {
        using var response = await mint;
        response.StatusCode.Should().Be(
            HttpStatusCode.Created, "the PIN mints an authorisation ({0})", await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ApiResponse<StepUpAuthorizationResponse>>(Json))!
            .Data!.AuthorizationId;
    }

    /// <summary>
    /// A response as status and body, with what differs between two requests for the same answer
    /// taken out: the trace id, and the handle the caller typed, which both bodies echo.
    /// With parentheses where the body has braces: <see cref="ComparableText"/> says why.
    /// </summary>
    public static async Task<string> AnswerAsync(HttpResponseMessage response, string handle)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (JsonNode.Parse(body) is JsonObject json)
        {
            json.Remove("traceId");
            body = json.ToJsonString();
        }

        return ComparableText.Of($"{(int)response.StatusCode} {response.Content.Headers.ContentType?.MediaType} "
            + body.Replace(handle, "<handle>", StringComparison.Ordinal));
    }

    private async Task<HttpResponseMessage> SendAsync<T>(
        HttpMethod method, string url, T? payload = default, Guid? authorizationId = null, Guid? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload, options: Json);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        if (authorizationId is { } authorization)
        {
            request.Headers.Add(StepUpConstants.HeaderName, authorization.ToString());
        }

        if (idempotencyKey is { } key)
        {
            request.Headers.Add(IdempotencyConstants.HeaderName, key.ToString());
        }

        return await _client.SendAsync(request);
    }
}
