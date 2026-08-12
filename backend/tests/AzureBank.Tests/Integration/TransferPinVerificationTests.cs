using System.Net;
using System.Net.Http.Json;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transfer;
using AzureBank.Tests.Fixtures;
using FluentAssertions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// ADR-0041: a transfer carries its PIN in-band and the API verifies it ITSELF.
///
/// <para>
/// Every case below goes STRAIGHT TO THE API — no BFF in the pipeline, no session cookie, nothing
/// but a bearer token, which is exactly the shape of request the old design could not refuse. Before
/// this change the only PIN check for a transfer lived in <c>AuthLevelMiddleware</c> in the BFF
/// process, so anything that reached the API directly moved money with no PIN at all. That is not a
/// hypothetical: it was measured on the running stack and is written up in
/// <c>BearerTokenTransformProvider</c>.
/// </para>
///
/// <para>
/// Status codes and error codes here were MEASURED against this pipeline, not copied from the
/// withdraw path they were modelled on — the observed value is quoted beside each assertion.
/// <c>CustomWebApplicationFactory</c> runs <c>Program.cs</c> verbatim, so the envelope, the
/// exception handler and the validators are the real ones.
/// </para>
/// </summary>
public class TransferPinVerificationTests : IntegrationTestBase
{
    public TransferPinVerificationTests(CustomWebApplicationFactory factory) : base(factory) { }

    private const string CorrectPin = "123456";
    private const string WrongPin = "999999";

    private static TransferRequest Transfer(Guid from, string recipient, string pin) => new()
    {
        FromAccountId = from,
        RecipientAzureTag = recipient,
        Amount = 25m,
        Description = "PIN verification test",
        Pin = pin
    };

    private async Task<decimal> BalanceOfAsync(string token, Guid accountId)
    {
        SetAuthHeader(token);
        var response = await Client.GetAsync($"/api/accounts/{accountId}");
        response.EnsureSuccessStatusCode();
        var result = await response.Content
            .ReadFromJsonAsync<ApiResponse<AccountResponse>>(JsonOptions);
        return result!.Data!.Balance;
    }

    private async Task<string?> ErrorCodeOf(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("errorCode", out var code) ? code.GetString() : null;
    }

    /// <summary>
    /// Registers a funded sender plus a recipient, and returns the recipient's handle. The sender
    /// is enrolled with a PIN only when asked — the not-enrolled case is a test in its own right.
    /// </summary>
    private async Task<(string SenderToken, Guid SenderAccount, string RecipientTag)> ScenarioAsync(
        bool enrolPin, decimal funding = 500m)
    {
        var (senderToken, _, senderAccount) = await RegisterTestUserAsync();

        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var recipientTag = $"recipient_{uniqueId}";
        var registered = await Client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = recipientTag,
            Email = $"recipient{uniqueId}@example.com",
            Password = "TestPass123!",
            FirstName = "Recipient",
            LastName = "User"
        }, JsonOptions);
        registered.EnsureSuccessStatusCode();

        await DepositAsync(senderToken, senderAccount, funding);
        if (enrolPin)
        {
            await SetPinAsync(senderToken, CorrectPin);
        }

        SetAuthHeader(senderToken);
        return (senderToken, senderAccount, recipientTag);
    }

    [Fact]
    public async Task Transfer_WithNoPinEnrolled_IsRefused_AndMovesNoMoney()
    {
        var (token, account, recipient) = await ScenarioAsync(enrolPin: false);
        var before = await BalanceOfAsync(token, account);

        SetAuthHeader(token);
        var response = await PostMonetaryAsync(
            "/api/transfers", Transfer(account, recipient, CorrectPin));

        // OBSERVED: 422 UnprocessableEntity / errorCode PIN_REQUIRED
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ErrorCodeOf(response)).Should().Be(ErrorCodes.PinRequired);
        (await BalanceOfAsync(token, account)).Should().Be(before, "a refusal must not move money");
    }

    [Fact]
    public async Task Transfer_WithTheWrongPin_IsRefused_AndMovesNoMoney()
    {
        var (token, account, recipient) = await ScenarioAsync(enrolPin: true);
        var before = await BalanceOfAsync(token, account);

        SetAuthHeader(token);
        var response = await PostMonetaryAsync(
            "/api/transfers", Transfer(account, recipient, WrongPin));

        // OBSERVED: 401 Unauthorized / errorCode INVALID_PIN
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ErrorCodeOf(response)).Should().Be(ErrorCodes.InvalidPin);
        (await BalanceOfAsync(token, account)).Should().Be(before, "a refusal must not move money");
    }

    [Fact]
    public async Task Transfer_WithNoPinField_IsRejectedByValidation()
    {
        var (token, account, recipient) = await ScenarioAsync(enrolPin: true);

        SetAuthHeader(token);
        // Deliberately hand-rolled JSON: the DTO's `required string Pin` makes the omission
        // unrepresentable in C#, and that is the whole point — an OLD CLIENT that predates ADR-0041
        // sends exactly this body, and it must be refused rather than defaulted.
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/transfers")
        {
            Content = new StringContent(
                $$"""
                {"fromAccountId":"{{account}}","recipientAzureTag":"{{recipient}}","amount":25}
                """,
                System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add(IdempotencyConstants.HeaderName, Guid.NewGuid().ToString());
        var response = await Client.SendAsync(request);

        // OBSERVED: 400 BadRequest
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Transfer_WithTheCorrectPin_Succeeds()
    {
        // The non-vacuity guard for every refusal above: same pipeline, same shape, right PIN.
        var (token, account, recipient) = await ScenarioAsync(enrolPin: true);
        var before = await BalanceOfAsync(token, account);

        SetAuthHeader(token);
        var response = await PostMonetaryAsync(
            "/api/transfers", Transfer(account, recipient, CorrectPin));

        // OBSERVED: 201 Created
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await BalanceOfAsync(token, account)).Should().Be(before - 25m);
    }

    [Fact]
    public async Task InternalTransfer_WithTheWrongPin_IsRefused_AndMovesNoMoney()
    {
        // The internal path is a separate method with its own ownership checks, so it gets its own
        // proof rather than an assumption that the two share a code path.
        var (token, _, primaryAccount) = await RegisterTestUserAsync();
        await DepositAsync(token, primaryAccount, 500m);
        await SetPinAsync(token, CorrectPin);

        SetAuthHeader(token);
        var created = await Client.PostAsJsonAsync("/api/accounts", new CreateAccountRequest
        {
            Name = "Second",
            Type = Shared.Enums.AccountType.Savings
        }, JsonOptions);
        created.EnsureSuccessStatusCode();
        var second = (await created.Content
            .ReadFromJsonAsync<ApiResponse<AccountResponse>>(JsonOptions))!.Data!.Id;

        var before = await BalanceOfAsync(token, primaryAccount);

        SetAuthHeader(token);
        var response = await PostMonetaryAsync("/api/transfers/internal", new InternalTransferRequest
        {
            FromAccountId = primaryAccount,
            ToAccountId = second,
            Amount = 25m,
            Description = "PIN verification test",
            Pin = WrongPin
        });

        // OBSERVED: 401 Unauthorized / errorCode INVALID_PIN
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ErrorCodeOf(response)).Should().Be(ErrorCodes.InvalidPin);
        (await BalanceOfAsync(token, primaryAccount)).Should().Be(before);
    }

    [Fact]
    public async Task IdempotencyKeyIsCheckedBeforeThePin()
    {
        /*
          ORDER, not just outcomes. The idempotency filter is an action filter, so it runs before
          the controller action and therefore before the service's PIN check — which means a
          request with a CORRECT PIN and no key is refused for the key, not waved through.

          Worth pinning because the mock has to reproduce the same order, and an order asserted only
          in the mock is an order nobody measured.
        */
        var (token, account, recipient) = await ScenarioAsync(enrolPin: true);

        SetAuthHeader(token);
        var response = await Client.PostAsJsonAsync(
            "/api/transfers", Transfer(account, recipient, CorrectPin), JsonOptions);

        // OBSERVED: 400 BadRequest / errorCode IDEMPOTENCY_KEY_MISSING
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodeOf(response)).Should().Be("IDEMPOTENCY_KEY_MISSING");
    }

    [Fact]
    public async Task Transfer_AfterRepeatedWrongPins_IsLockedOut()
    {
        /*
          The attempt limiter is shared with withdraw (ADR-0011), so what is proved here is that the
          transfer path GOES THROUGH it rather than around it — a service that verified the hash
          directly would let an attacker enumerate a six-digit PIN without ever tripping a lock.
        */
        var (token, account, recipient) = await ScenarioAsync(enrolPin: true);
        SetAuthHeader(token);

        var codes = new List<string?>();
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var response = await PostMonetaryAsync(
                "/api/transfers", Transfer(account, recipient, WrongPin));
            codes.Add(await ErrorCodeOf(response));
        }

        codes.Should().Contain(ErrorCodes.PinLocked,
            "repeated wrong PINs on the TRANSFER path must trip the same lockout withdraw uses");
    }
}
