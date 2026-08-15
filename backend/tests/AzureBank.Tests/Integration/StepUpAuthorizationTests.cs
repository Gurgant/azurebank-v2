using System.Net;
using System.Net.Http.Json;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transfer;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBank.Tests.Integration;

/// <summary>
/// ADR-0042: a transfer authorisation is minted from the PIN, bound to one amount and one payee, and
/// accepted once.
///
/// <para>
/// Every case goes straight to the API through <c>CustomWebApplicationFactory</c>, which runs
/// <c>Program.cs</c> verbatim — so the middleware order, the exception handler and the validators are
/// the real ones. Status and error codes below were OBSERVED on this pipeline and quoted beside the
/// assertion, never copied from the withdraw path or from an ADR.
/// </para>
///
/// <para>
/// The load-bearing case is <see cref="Replay_OfACompletedTransfer_SucceedsEvenWithAnExpiredAuthorisation"/>.
/// It is what makes the placement decision falsifiable: move the authorisation check in front of the
/// idempotency claim and that test goes red, because a replay would start being refused for a
/// credential that expired after the money had already moved.
/// </para>
/// </summary>
public class StepUpAuthorizationTests : IntegrationTestBase
{
    public StepUpAuthorizationTests(CustomWebApplicationFactory factory) : base(factory) { }

    private const string CorrectPin = "123456";
    private const string WrongPin = "999999";
    private const decimal Amount = 25m;

    private async Task<string?> ErrorCodeOf(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("errorCode", out var code) ? code.GetString() : null;
    }

    private async Task<decimal> BalanceOfAsync(string token, Guid accountId)
    {
        SetAuthHeader(token);
        var response = await Client.GetAsync($"/api/accounts/{accountId}");
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<AccountResponse>>(JsonOptions);
        return result!.Data!.Balance;
    }

    /// <summary>A funded sender with a PIN, plus a registered recipient.</summary>
    private async Task<(string Token, Guid UserId, Guid Account, string RecipientTag)> ScenarioAsync(
        decimal funding = 500m)
    {
        var (token, userId, account) = await RegisterTestUserAsync();

        var unique = Guid.NewGuid().ToString("N")[..8];
        var recipientTag = $"payee_{unique}";
        var registered = await Client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = recipientTag,
            Email = $"payee{unique}@example.com",
            Password = "TestPass123!",
            FirstName = "Payee",
            LastName = "User"
        }, JsonOptions);
        registered.EnsureSuccessStatusCode();

        await DepositAsync(token, account, funding);
        await SetPinAsync(token, CorrectPin);
        SetAuthHeader(token);
        return (token, userId, account, recipientTag);
    }

    private async Task<HttpResponseMessage> MintAsync(
        Guid from, string recipientTag, decimal amount, string pin)
        => await Client.PostAsJsonAsync("/api/transfers/authorizations", new TransferAuthorizationRequest
        {
            FromAccountId = from,
            RecipientAzureTag = recipientTag,
            Amount = amount,
            Pin = pin
        }, JsonOptions);

    private async Task<StepUpAuthorizationResponse> MintOkAsync(
        Guid from, string recipientTag, decimal amount = Amount)
    {
        var response = await MintAsync(from, recipientTag, amount, CorrectPin);
        response.StatusCode.Should().Be(HttpStatusCode.Created, "minting with a correct PIN must succeed");
        var body = await response.Content
            .ReadFromJsonAsync<ApiResponse<StepUpAuthorizationResponse>>(JsonOptions);
        return body!.Data!;
    }

    /// <summary>
    /// POSTs a transfer carrying an idempotency key and, optionally, an authorisation header. Built
    /// by hand rather than through <c>PostMonetaryAsync</c> precisely because the authorisation must
    /// travel as a HEADER — the whole point of ADR-0042's placement is that it stays out of the body,
    /// where it would change the idempotency fingerprint.
    /// </summary>
    private Task<HttpResponseMessage> TransferAsync(
        Guid from, string recipientTag, Guid? authorizationId, Guid idempotencyKey, decimal amount = Amount)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/transfers")
        {
            Content = JsonContent.Create(new TransferRequest
            {
                FromAccountId = from,
                RecipientAzureTag = recipientTag,
                Amount = amount,
                Description = "step-up test",
                Pin = CorrectPin
            }, options: JsonOptions)
        };
        request.Headers.Add(IdempotencyConstants.HeaderName, idempotencyKey.ToString());
        if (authorizationId is { } id)
        {
            request.Headers.Add(StepUpConstants.HeaderName, id.ToString());
        }
        return Client.SendAsync(request);
    }

    /// <summary>Moves an authorisation's expiry into the past, the way the PIN-lock tests age a lock.</summary>
    private async Task ExpireAsync(Guid authorizationId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var authorization = await db.StepUpAuthorizations.FindAsync(authorizationId);
        authorization.Should().NotBeNull("the mint endpoint must have persisted it");
        authorization!.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();
    }

    private async Task<StepUpAuthorization?> ReadAsync(Guid authorizationId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return await db.StepUpAuthorizations.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == authorizationId);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Minting
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Mint_WithTheCorrectPin_ReturnsAPendingAuthorisationThatExpires()
    {
        var (_, userId, account, recipient) = await ScenarioAsync();

        var minted = await MintOkAsync(account, recipient);

        minted.AuthorizationId.Should().NotBeEmpty();
        minted.ExpiresAt.Should().BeAfter(DateTime.UtcNow, "a fresh authorisation must still be spendable");
        minted.ExpiresAt.Should().BeBefore(DateTime.UtcNow.AddMinutes(5),
            "the window is two minutes; anything near the session's five would mean the wrong option was bound");

        var stored = await ReadAsync(minted.AuthorizationId);
        stored.Should().NotBeNull();
        stored!.Status.Should().Be(StepUpAuthorizationStatus.Pending);
        stored.UserId.Should().Be(userId);
        stored.Operation.Should().Be(StepUpOperation.Transfer);
        stored.ConsumedAt.Should().BeNull();
    }

    [Fact]
    public async Task Mint_WithTheWrongPin_IsRefused_AndMintsNothing()
    {
        var (_, userId, account, recipient) = await ScenarioAsync();

        var response = await MintAsync(account, recipient, Amount, WrongPin);

        // OBSERVED: 401 Unauthorized / errorCode INVALID_PIN — the same answer the transfer gives,
        // deliberately: minting must not be a cheaper PIN oracle than the transfer itself.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ErrorCodeOf(response)).Should().Be(ErrorCodes.InvalidPin);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        (await db.StepUpAuthorizations.CountAsync(a => a.UserId == userId))
            .Should().Be(0, "a refused mint must leave no authorisation behind");
    }

    [Fact]
    public async Task Mint_ForAnUnknownRecipient_IsRefused()
    {
        var (_, _, account, _) = await ScenarioAsync();

        var response = await MintAsync(account, "nobody_at_all", Amount, CorrectPin);

        // OBSERVED: 404 NotFound. An authorisation must never name a payee the transfer would
        // then refuse — that is why the mint resolves the recipient instead of trusting the handle.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Spending
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Transfer_WithAMatchingAuthorisation_Succeeds_AndConsumesItExactlyOnce()
    {
        var (token, _, account, recipient) = await ScenarioAsync();
        var before = await BalanceOfAsync(token, account);
        var minted = await MintOkAsync(account, recipient);

        SetAuthHeader(token);
        var response = await TransferAsync(account, recipient, minted.AuthorizationId, Guid.NewGuid());

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await BalanceOfAsync(token, account)).Should().Be(before - Amount);

        var spent = await ReadAsync(minted.AuthorizationId);
        spent!.Status.Should().Be(StepUpAuthorizationStatus.Consumed);
        spent.ConsumedAt.Should().NotBeNull();
        spent.ConsumedByTransactionId.Should().NotBeNull(
            "the row is also the evidence, so it must name the movement it paid for");
    }

    [Fact]
    public async Task Transfer_ReusingASpentAuthorisation_IsRefused_AndMovesNoSecondAmount()
    {
        var (token, _, account, recipient) = await ScenarioAsync();
        var minted = await MintOkAsync(account, recipient);

        SetAuthHeader(token);
        (await TransferAsync(account, recipient, minted.AuthorizationId, Guid.NewGuid()))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var after = await BalanceOfAsync(token, account);

        // A DIFFERENT idempotency key, so nothing replays: this is a genuinely new request that
        // happens to present an authorisation already spent. RTS Art. 4(1): accepted once.
        SetAuthHeader(token);
        var second = await TransferAsync(account, recipient, minted.AuthorizationId, Guid.NewGuid());

        // OBSERVED: 401 Unauthorized / errorCode AUTHORIZATION_INVALID
        second.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ErrorCodeOf(second)).Should().Be(ErrorCodes.AuthorizationInvalid);
        (await BalanceOfAsync(token, account)).Should().Be(after, "a refusal must not move money");
    }

    [Fact]
    public async Task Transfer_WithAnExpiredAuthorisation_IsRefused_AndCostsNoPinAttempt()
    {
        var (token, userId, account, recipient) = await ScenarioAsync();
        var minted = await MintOkAsync(account, recipient);
        var before = await BalanceOfAsync(token, account);

        await ExpireAsync(minted.AuthorizationId);

        SetAuthHeader(token);
        var response = await TransferAsync(account, recipient, minted.AuthorizationId, Guid.NewGuid());

        // OBSERVED: 401 Unauthorized / errorCode AUTHORIZATION_EXPIRED — distinguishable from
        // AUTHORIZATION_INVALID and from INVALID_PIN, because the three demand different sentences.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ErrorCodeOf(response)).Should().Be(ErrorCodes.AuthorizationExpired);
        (await BalanceOfAsync(token, account)).Should().Be(before, "a refusal must not move money");

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var user = await db.Users.FindAsync(userId);
        user!.PinAccessFailedCount.Should().Be(0,
            "an expiry is not a failed authentication and must never count towards the lock");
        user.PinLockoutEnd.Should().BeNull();

        (await ReadAsync(minted.AuthorizationId))!.Status
            .Should().Be(StepUpAuthorizationStatus.Pending, "an expired authorisation is refused, not spent");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // The binding
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Transfer_ForADifferentAmountThanAuthorised_IsRefused()
    {
        var (token, _, account, recipient) = await ScenarioAsync();
        var minted = await MintOkAsync(account, recipient, amount: 25m);
        var before = await BalanceOfAsync(token, account);

        SetAuthHeader(token);
        var response = await TransferAsync(account, recipient, minted.AuthorizationId, Guid.NewGuid(), amount: 26m);

        // RTS Art. 5(1)(d): any change to the amount invalidates the code.
        // OBSERVED: 401 Unauthorized / errorCode AUTHORIZATION_INVALID
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ErrorCodeOf(response)).Should().Be(ErrorCodes.AuthorizationInvalid);
        (await BalanceOfAsync(token, account)).Should().Be(before);
    }

    [Fact]
    public async Task Transfer_ToADifferentPayeeThanAuthorised_IsRefused()
    {
        var (token, _, account, recipient) = await ScenarioAsync();

        var unique = Guid.NewGuid().ToString("N")[..8];
        var otherTag = $"other_{unique}";
        (await Client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = otherTag,
            Email = $"other{unique}@example.com",
            Password = "TestPass123!",
            FirstName = "Other",
            LastName = "User"
        }, JsonOptions)).EnsureSuccessStatusCode();

        SetAuthHeader(token);
        var minted = await MintOkAsync(account, recipient);
        var before = await BalanceOfAsync(token, account);

        SetAuthHeader(token);
        var response = await TransferAsync(account, otherTag, minted.AuthorizationId, Guid.NewGuid());

        // RTS Art. 5(1)(d) again, on the payee half.
        // OBSERVED: 401 Unauthorized / errorCode AUTHORIZATION_INVALID
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ErrorCodeOf(response)).Should().Be(ErrorCodes.AuthorizationInvalid);
        (await BalanceOfAsync(token, account)).Should().Be(before);
    }

    [Fact]
    public async Task Transfer_WithSomeoneElsesAuthorisation_IsRefused_WithNoOracle()
    {
        var (ownerToken, _, ownerAccount, recipient) = await ScenarioAsync();
        var minted = await MintOkAsync(ownerAccount, recipient);

        // A second, unrelated user tries to spend it.
        var (attackerToken, _, attackerAccount) = await RegisterTestUserAsync();
        await DepositAsync(attackerToken, attackerAccount, 500m);
        await SetPinAsync(attackerToken, CorrectPin);

        SetAuthHeader(attackerToken);
        var response = await TransferAsync(attackerAccount, recipient, minted.AuthorizationId, Guid.NewGuid());

        // OBSERVED: 401 Unauthorized / errorCode AUTHORIZATION_INVALID — the SAME answer an unknown
        // reference gets, so the endpoint is not an oracle for which references are live.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ErrorCodeOf(response)).Should().Be(ErrorCodes.AuthorizationInvalid);

        (await ReadAsync(minted.AuthorizationId))!.Status
            .Should().Be(StepUpAuthorizationStatus.Pending, "someone else's attempt must not spend it");

        // The owner minted but never sent, so their funding is untouched.
        (await BalanceOfAsync(ownerToken, ownerAccount)).Should().Be(500m,
            "the owner's money must be untouched");
    }

    [Fact]
    public async Task Transfer_WithAnUnknownAuthorisation_IsRefusedTheSameWay()
    {
        var (token, _, account, recipient) = await ScenarioAsync();

        SetAuthHeader(token);
        var response = await TransferAsync(account, recipient, Guid.NewGuid(), Guid.NewGuid());

        // OBSERVED: 401 Unauthorized / errorCode AUTHORIZATION_INVALID
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ErrorCodeOf(response)).Should().Be(ErrorCodes.AuthorizationInvalid);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // The placement property — this is the one that justifies ADR-0042
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A completed transfer must replay its stored 201 even when the authorisation it was paid with
    /// has since expired.
    ///
    /// <para>
    /// This is the whole reason the check lives inside <c>TransferService</c> rather than in the BFF
    /// gate or a middleware beside the idempotency claim. Measured:
    /// <c>IdempotencyMiddleware</c> writes the stored response and returns before
    /// <c>await _next(context)</c>, so a replay never enters MVC. Move the authorisation check in
    /// front of the claim and this test goes red — and with it goes the ability of the one client
    /// that provably cannot know whether its money moved to find out.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Replay_OfACompletedTransfer_SucceedsEvenWithAnExpiredAuthorisation()
    {
        var (token, _, account, recipient) = await ScenarioAsync();
        var minted = await MintOkAsync(account, recipient);
        var key = Guid.NewGuid();

        SetAuthHeader(token);
        var first = await TransferAsync(account, recipient, minted.AuthorizationId, key);
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        var afterFirst = await BalanceOfAsync(token, account);

        // The two-minute window lapses while the client is retrying a response it never received.
        await ExpireAsync(minted.AuthorizationId);

        SetAuthHeader(token);
        var replay = await TransferAsync(account, recipient, minted.AuthorizationId, key);

        replay.StatusCode.Should().Be(HttpStatusCode.Created, "the stored result must still be reachable");
        replay.Headers.GetValues(IdempotencyConstants.ReplayedHeaderName)
            .Should().ContainSingle().Which.Should().Be("true");
        (await BalanceOfAsync(token, account)).Should().Be(afterFirst, "a replay must not move money twice");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // The temporary PR-1 shape
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// PR 1 ships without a client, so a transfer with NO authorisation header still works on the
    /// in-band PIN alone. Nothing is weaker than before this PR — the PIN is verified either way.
    /// PR 2 makes the header required and deletes <c>TransferRequest.Pin</c>, at which point this
    /// test is expected to be REPLACED by its opposite rather than deleted quietly.
    /// </summary>
    [Fact]
    public async Task Transfer_WithNoAuthorisationHeader_StillWorksOnTheInBandPin()
    {
        var (token, _, account, recipient) = await ScenarioAsync();
        var before = await BalanceOfAsync(token, account);

        SetAuthHeader(token);
        var response = await TransferAsync(account, recipient, authorizationId: null, Guid.NewGuid());

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await BalanceOfAsync(token, account)).Should().Be(before - Amount);
    }

    [Fact]
    public async Task Transfer_WithAMalformedAuthorisationHeader_IsRejectedByModelBinding()
    {
        var (token, _, account, recipient) = await ScenarioAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/transfers")
        {
            Content = JsonContent.Create(new TransferRequest
            {
                FromAccountId = account,
                RecipientAzureTag = recipient,
                Amount = Amount,
                Pin = CorrectPin
            }, options: JsonOptions)
        };
        request.Headers.Add(IdempotencyConstants.HeaderName, Guid.NewGuid().ToString());
        request.Headers.Add(StepUpConstants.HeaderName, "not-a-guid");

        SetAuthHeader(token);
        var response = await Client.SendAsync(request);

        // OBSERVED: 400 BadRequest — model binding refuses a non-UUID before the action runs, which
        // is the same shape as IDEMPOTENCY_KEY_INVALID and needs no code of its own.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
