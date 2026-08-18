using System.Linq;
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
/// Where a transfer's PIN is proved, and what happens when it is not.
///
/// <para>
/// ADR-0041 put the PIN in the transfer body so the API could refuse on its own, closing a measured
/// hole: before it, the only step-up check lived in the BFF's <c>AuthLevelMiddleware</c>, so anything
/// reaching the API directly moved money with no PIN at all. ADR-0042's second half moved the proof
/// one endpoint upstream — the PIN is now spent at <c>POST /api/transfers/authorizations</c>, which
/// mints a reference bound to one amount and one payee, and the transfer accepts nothing else. Every
/// case in this file therefore targets THE MINT, not the transfer; the properties are unchanged, the
/// address is not.
/// </para>
///
/// <para>
/// Every case still goes STRAIGHT TO THE API — no BFF, no session cookie, nothing but a bearer
/// token, the exact shape the old design could not refuse. Status codes and error codes here were
/// MEASURED against this pipeline and the observed value is quoted beside each assertion.
/// <c>CustomWebApplicationFactory</c> runs <c>Program.cs</c> verbatim, so the envelope, the exception
/// handler and the validators are the real ones.
/// </para>
/// </summary>
public class TransferPinVerificationTests : IntegrationTestBase
{
    public TransferPinVerificationTests(CustomWebApplicationFactory factory) : base(factory) { }

    private const string CorrectPin = "123456";
    private const string WrongPin = "999999";
    private const string MintUrl = "/api/transfers/authorizations";

    private static TransferAuthorizationRequest Authorisation(Guid from, string recipient, string pin) => new()
    {
        FromAccountId = from,
        RecipientAzureTag = recipient,
        Amount = 25m,
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

    /// <summary>Posts a raw JSON body to the mint, so bodies C# cannot express are still testable.</summary>
    private Task<HttpResponseMessage> PostRawAsync(string json) =>
        Client.PostAsync(MintUrl, new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

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
    public async Task Authorising_WithNoPinEnrolled_IsRefused_AndMovesNoMoney()
    {
        var (token, account, recipient) = await ScenarioAsync(enrolPin: false);
        var before = await BalanceOfAsync(token, account);

        SetAuthHeader(token);
        var response = await Client.PostAsJsonAsync(
            MintUrl, Authorisation(account, recipient, CorrectPin), JsonOptions);

        // OBSERVED: 422 UnprocessableEntity / errorCode PIN_REQUIRED
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ErrorCodeOf(response)).Should().Be(ErrorCodes.PinRequired);
        (await BalanceOfAsync(token, account)).Should().Be(before, "a refusal must not move money");
    }

    [Fact]
    public async Task Authorising_WithTheWrongPin_IsRefused_AndMovesNoMoney()
    {
        var (token, account, recipient) = await ScenarioAsync(enrolPin: true);
        var before = await BalanceOfAsync(token, account);

        SetAuthHeader(token);
        var response = await Client.PostAsJsonAsync(
            MintUrl, Authorisation(account, recipient, WrongPin), JsonOptions);

        // OBSERVED: 401 Unauthorized / errorCode INVALID_PIN
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ErrorCodeOf(response)).Should().Be(ErrorCodes.InvalidPin);
        (await BalanceOfAsync(token, account)).Should().Be(before, "a refusal must not move money");
    }

    [Fact]
    public async Task Authorising_WithNoPinField_IsRejectedByValidation()
    {
        var (token, account, recipient) = await ScenarioAsync(enrolPin: true);

        SetAuthHeader(token);
        // Deliberately hand-rolled JSON: the DTO's `required string Pin` makes the omission
        // unrepresentable in C#, and that is the whole point — a client that thinks the PIN is
        // optional must be refused rather than defaulted.
        var response = await PostRawAsync(
            $$"""
            {"fromAccountId":"{{account}}","recipientAzureTag":"{{recipient}}","amount":25}
            """);

        /*
          THE ENVELOPE IS THE POINT, not just the status. `required string Pin` is refused by
          System.Text.Json during DESERIALISATION, so the request never reaches FluentValidation —
          which means it gets the FRAMEWORK envelope (rfc9110 type, default title, `$`/`request`
          keys) rather than the hand-written "Validation Failed" one:

            400 {"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1",
                 "title":"One or more validation errors occurred.",
                 "errors":{"$":["JSON deserialization for type
                                 'AzureBank.Shared.DTOs.Transfer.TransferAuthorizationRequest' was
                                 missing required properties including: 'pin'."],
                           "request":["The request field is required."]}}

          A status-only assertion cannot tell those apart, and the difference is what broke the
          contract suite once: a probe with no pin stopped testing the rule it named and started
          testing the deserialiser.
        */
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        doc.RootElement.GetProperty("title").GetString()
            .Should().Be("One or more validation errors occurred.");
        doc.RootElement.GetProperty("errors").EnumerateObject()
            .Select(p => p.Name).Should().BeEquivalentTo(["$", "request"]);
    }

    [Fact]
    public async Task MalformedPin_IsRefusedByModelBinding_AndCostsNoAttempt()
    {
        /*
          THE ONE THAT MATTERS FOR THE LOCKOUT. `[Pin]` is a DataAnnotation, so it fires during model
          binding — before the action, before the service, before IPinVerifier. A malformed value
          therefore cannot be used to exhaust someone's attempts.

          Three malformed sends and then one genuinely wrong PIN: if the malformed ones counted, the
          fourth would be 429 PIN_LOCKED rather than 401.

          OBSERVED: 400 {"errors":{"Pin":["PIN must be exactly 6 digits."]}} — PascalCase key,
          because DataAnnotations report against the bound PROPERTY, unlike FluentValidation's
          camelCase keys on the very same endpoint.
        */
        var (token, account, recipient) = await ScenarioAsync(enrolPin: true);
        SetAuthHeader(token);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var junk = await Client.PostAsJsonAsync(
                MintUrl, Authorisation(account, recipient, "abcdef"), JsonOptions);
            junk.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            using var doc = System.Text.Json.JsonDocument.Parse(
                await junk.Content.ReadAsStringAsync());
            doc.RootElement.GetProperty("errors").TryGetProperty("Pin", out var pinErrors)
                .Should().BeTrue("the key follows the bound property, so it is PascalCase");
            pinErrors.EnumerateArray().Select(e => e.GetString())
                .Should().Contain("PIN must be exactly 6 digits.");
        }

        var wrong = await Client.PostAsJsonAsync(
            MintUrl, Authorisation(account, recipient, WrongPin), JsonOptions);
        wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "three MALFORMED pins must not have consumed the three real attempts");
        (await ErrorCodeOf(wrong)).Should().Be(ErrorCodes.InvalidPin);
    }

    [Fact]
    public async Task NonStringPin_FailsDeserialisation_AndIsKeyedByJsonPath()
    {
        /*
          The mock coerced this with String(pin) until an earlier review round, and String(123456) is
          "123456" — which matches the six-digit rule. So the mock ACCEPTED a payload the API
          refuses outright. Pinning the real answer here is what stops that drifting back.

          OBSERVED, for a JSON number and for a boolean alike:
            400 {"request":["The request field is required."],
                 "$.pin":["The JSON value could not be converted to System.String. …"]}

          Keyed by JSON PATH, not by property name: System.Text.Json fails the conversion before
          DataAnnotations ever run. NULL is different and is covered below — it converts fine and is
          rejected by [Required].
        */
        var (token, account, recipient) = await ScenarioAsync(enrolPin: true);
        SetAuthHeader(token);

        foreach (var literal in new[] { "123456", "true" })
        {
            var response = await PostRawAsync(
                $"{{\"fromAccountId\":\"{account}\",\"recipientAzureTag\":\"{recipient}\","
                + $"\"amount\":25,\"pin\":{literal}}}");

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            using var doc = System.Text.Json.JsonDocument.Parse(
                await response.Content.ReadAsStringAsync());
            var keys = doc.RootElement.GetProperty("errors").EnumerateObject()
                .Select(p => p.Name).ToList();
            keys.Should().Contain("$.pin", "a conversion failure is keyed by JSON path");
            keys.Should().NotContain("Pin", "DataAnnotations never ran — the bind aborted first");
        }
    }

    [Fact]
    public async Task NullPin_FiresRequiredAlone_NotTheFormatRule()
    {
        // OBSERVED: {"pin":null} -> 400 {"Pin":["The Pin field is required."]} — ONE message.
        // DataAnnotations skip non-Required validators on null but run them all on "".
        var (token, account, recipient) = await ScenarioAsync(enrolPin: true);
        SetAuthHeader(token);

        var response = await PostRawAsync(
            $"{{\"fromAccountId\":\"{account}\",\"recipientAzureTag\":\"{recipient}\","
            + "\"amount\":25,\"pin\":null}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var doc = System.Text.Json.JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("errors").GetProperty("Pin").EnumerateArray()
            .Select(e => e.GetString())
            .Should().BeEquivalentTo(["The Pin field is required."]);
    }

    [Fact]
    public async Task ModelState_AggregatesEveryBadField_InOnePass()
    {
        /*
          [MoneyRange] on Amount and [Pin] on Pin are both DataAnnotations, evaluated together, so a
          doubly-invalid body names BOTH. The mock returned early from its amount check before the
          pin gate ran, so a form could highlight one field where the API highlights two.

          OBSERVED: amount -5, pin "12" -> 400 with BOTH `Pin` and `Amount` keys.
        */
        var (token, account, recipient) = await ScenarioAsync(enrolPin: true);
        SetAuthHeader(token);

        var response = await Client.PostAsJsonAsync(MintUrl, new TransferAuthorizationRequest
        {
            FromAccountId = account,
            RecipientAzureTag = recipient,
            Amount = -5m,
            Pin = "12"
        }, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var doc = System.Text.Json.JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("errors").EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo(["Pin", "Amount"],
                "one model-state pass reports every bad field, not the first");
    }

    [Fact]
    public async Task UnknownSourceAccount_IsRefusedBeforeThePin()
    {
        /*
          Ownership precedes the PIN in AuthoriseTransferAsync — deliberately, and it is the same
          ordering the transfer applies — so a probe against a foreign account id is a 404 whatever
          pin it carries. It cannot be used to test PINs, and it costs no attempt.

          OBSERVED, same unknown id, two pins:
            correct pin -> 404 ACCOUNT_NOT_FOUND
            WRONG   pin -> 404 ACCOUNT_NOT_FOUND
        */
        var (token, _, recipient) = await ScenarioAsync(enrolPin: true);
        SetAuthHeader(token);
        var stranger = Guid.NewGuid();

        foreach (var pin in new[] { CorrectPin, WrongPin })
        {
            var response = await Client.PostAsJsonAsync(
                MintUrl, Authorisation(stranger, recipient, pin), JsonOptions);
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await ErrorCodeOf(response)).Should().Be(ErrorCodes.AccountNotFound);
        }
    }

    [Fact]
    public async Task TheCorrectPin_MintsAnAuthorisation_ThatMovesTheMoney()
    {
        // The non-vacuity guard for every refusal above: same pipeline, right PIN, and the minted
        // reference actually spends. A file of refusals proves nothing without one success.
        var (token, account, recipient) = await ScenarioAsync(enrolPin: true);
        var before = await BalanceOfAsync(token, account);

        SetAuthHeader(token);
        var minted = await Client.PostAsJsonAsync(
            MintUrl, Authorisation(account, recipient, CorrectPin), JsonOptions);

        // OBSERVED: 201 Created
        minted.StatusCode.Should().Be(HttpStatusCode.Created);
        var authorization = (await minted.Content
            .ReadFromJsonAsync<ApiResponse<StepUpAuthorizationResponse>>(JsonOptions))!.Data!;

        var transfer = await PostMonetaryAsync("/api/transfers", new TransferRequest
        {
            FromAccountId = account,
            RecipientAzureTag = recipient,
            Amount = 25m,
            Description = "PIN verification test"
        }, stepUpAuthorizationId: authorization.AuthorizationId);

        transfer.StatusCode.Should().Be(HttpStatusCode.Created);
        (await BalanceOfAsync(token, account)).Should().Be(before - 25m);
    }

    [Fact]
    public async Task AuthorisingAnInternalTransfer_WithTheWrongPin_IsRefused_AndMovesNoMoney()
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
        var response = await Client.PostAsJsonAsync(
            "/api/transfers/internal/authorizations",
            new InternalTransferAuthorizationRequest
            {
                FromAccountId = primaryAccount,
                ToAccountId = second,
                Amount = 25m,
                Pin = WrongPin
            },
            JsonOptions);

        // OBSERVED: 401 Unauthorized / errorCode INVALID_PIN
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ErrorCodeOf(response)).Should().Be(ErrorCodes.InvalidPin);
        (await BalanceOfAsync(token, primaryAccount)).Should().Be(before);
    }

    [Fact]
    public async Task IdempotencyKeyIsCheckedBeforeTheAuthorisation()
    {
        /*
          ORDER, not just outcomes — and this one stays on the TRANSFER, because that is where both
          checks meet. Idempotency is MIDDLEWARE (`app.UseIdempotency()`, Program.cs:139), not an
          action filter: it runs before MVC is entered at all, which is why a replay skips model
          binding, the controller and the service entirely — and therefore skips the authorisation
          check too, the property ADR-0042 depends on.

          What is pinned here is that a request holding a PERFECTLY GOOD authorisation and no
          idempotency key is refused for the key, not waved through. Worth pinning because the mock
          has to reproduce the same order, and an order asserted only in the mock is an order nobody
          measured.
        */
        var (token, account, recipient) = await ScenarioAsync(enrolPin: true);
        SetAuthHeader(token);
        var authorization = await AuthoriseTransferAsync(account, recipient, 25m);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/transfers")
        {
            Content = JsonContent.Create(new TransferRequest
            {
                FromAccountId = account,
                RecipientAzureTag = recipient,
                Amount = 25m,
                Description = "PIN verification test"
            }, options: JsonOptions)
        };
        request.Headers.Add(StepUpConstants.HeaderName, authorization.ToString());
        var response = await Client.SendAsync(request);

        // OBSERVED: 400 BadRequest / errorCode IDEMPOTENCY_KEY_MISSING
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodeOf(response)).Should().Be("IDEMPOTENCY_KEY_MISSING");
    }

    [Fact]
    public async Task Authorising_AfterRepeatedWrongPins_IsLockedOut()
    {
        /*
          The attempt limiter is shared with withdraw (ADR-0011), so what is proved here is that the
          MINT goes THROUGH it rather than around it — a service that verified the hash directly
          would let an attacker enumerate a six-digit PIN without ever tripping a lock. Since
          ADR-0042 this is the only endpoint on the transfer path that can spend an attempt, so if
          the lockout is not here it is nowhere.
        */
        var (token, account, recipient) = await ScenarioAsync(enrolPin: true);
        SetAuthHeader(token);

        // Status AND code, kept together: PIN_LOCKED carried on a 401 or a 422 would satisfy a
        // code-only assertion while breaking the contract the SPA branches on (429 + Retry-After).
        var outcomes = new List<(HttpStatusCode Status, string? Code)>();
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var response = await Client.PostAsJsonAsync(
                MintUrl, Authorisation(account, recipient, WrongPin), JsonOptions);
            outcomes.Add((response.StatusCode, await ErrorCodeOf(response)));
        }

        outcomes.Should().Contain(
            o => o.Status == HttpStatusCode.TooManyRequests && o.Code == ErrorCodes.PinLocked,
            "repeated wrong PINs at the mint must trip the same lockout withdraw uses");
    }
}
