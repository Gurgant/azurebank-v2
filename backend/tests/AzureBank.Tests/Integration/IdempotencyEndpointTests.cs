using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transaction;
using AzureBank.Shared.DTOs.Transfer;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Integration tests for the idempotency mechanism (ADR-0009) on the
/// monetary endpoints: /api/transactions/deposit, /api/transactions/withdraw,
/// /api/transfers, /api/transfers/internal.
/// </summary>
public class IdempotencyEndpointTests : IntegrationTestBase
{
    public IdempotencyEndpointTests(CustomWebApplicationFactory factory) : base(factory) { }

    #region Header validation

    [Theory]
    [InlineData("/api/transactions/deposit")]
    [InlineData("/api/transactions/withdraw")]
    [InlineData("/api/transfers")]
    [InlineData("/api/transfers/internal")]
    public async Task MonetaryEndpoint_WithoutIdempotencyKey_Returns400KeyMissing(string url)
    {
        var (token, _, _) = await RegisterTestUserAsync();
        SetAuthHeader(token);

        // Body irrelevant: the header check runs before model binding
        var response = await Client.PostAsJsonAsync(url, new { }, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await ReadProblemAsync(response);
        problem.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.IdempotencyKeyMissing);
        problem.TryGetProperty("traceId", out _).Should().BeTrue();
    }

    [Theory]
    [InlineData("not-a-uuid")]
    [InlineData("00000000-0000-0000-0000-000000000000")] // Guid.Empty = careless default
    public async Task Deposit_WithMalformedIdempotencyKey_Returns400KeyInvalid(string headerValue)
    {
        var (token, _, accountId) = await RegisterTestUserAsync();
        SetAuthHeader(token);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/transactions/deposit")
        {
            Content = JsonContent.Create(NewDeposit(accountId, 10m), options: JsonOptions)
        };
        request.Headers.Add(IdempotencyConstants.HeaderName, headerValue);

        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await ReadProblemAsync(response);
        problem.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.IdempotencyKeyInvalid);
    }

    [Fact]
    public async Task UnauthenticatedRequest_Returns401_WithoutTouchingIdempotency()
    {
        ClearAuthHeader();
        // No Idempotency-Key: auth must short-circuit BEFORE the idempotency
        // middleware, so the error is 401, not 400 KEY_MISSING.
        var response = await Client.PostAsJsonAsync(
            "/api/transactions/deposit", NewDeposit(Guid.NewGuid(), 10m), JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    #endregion

    #region Replay semantics

    [Fact]
    public async Task Deposit_RetrySameKeySameBody_ReplaysStoredResponse_ExecutesOnce()
    {
        var (token, _, accountId) = await RegisterTestUserAsync();
        SetAuthHeader(token);
        var key = Guid.NewGuid();
        var body = NewDeposit(accountId, 250m);

        var first = await PostMonetaryAsync("/api/transactions/deposit", body, key);
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        first.Headers.Contains(IdempotencyConstants.ReplayedHeaderName).Should().BeFalse(
            "the first execution is not a replay");
        var firstBody = await first.Content.ReadAsStringAsync();

        var second = await PostMonetaryAsync("/api/transactions/deposit", body, key);

        second.StatusCode.Should().Be(HttpStatusCode.Created, "replays preserve the original status");
        second.Headers.TryGetValues(IdempotencyConstants.ReplayedHeaderName, out var values)
            .Should().BeTrue();
        values!.Single().Should().Be("true");
        (await second.Content.ReadAsStringAsync()).Should().Be(firstBody,
            "the stored response is replayed byte-identically");

        // The money moved exactly once
        (await GetBalanceAsync(accountId)).Should().Be(250m);
        (await CountTransactionsAsync(token, accountId)).Should().Be(1);
    }

    [Fact]
    public async Task Transfer_RetrySameKeySameBody_DoesNotExecuteTwice()
    {
        var (senderToken, _, senderAccountId) = await RegisterTestUserAsync();
        var recipient = await RegisterRecipientAsync();
        await DepositAsync(senderToken, senderAccountId, 1000m);

        SetAuthHeader(senderToken);
        var key = Guid.NewGuid();
        var body = new TransferRequest
        {
            FromAccountId = senderAccountId,
            RecipientAzureTag = recipient.AzureTag,
            Amount = 100m,
            Description = "Idempotent transfer"
        };

        var first = await PostMonetaryAsync("/api/transfers", body, key);
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await PostMonetaryAsync("/api/transfers", body, key);
        second.StatusCode.Should().Be(HttpStatusCode.Created);
        second.Headers.Contains(IdempotencyConstants.ReplayedHeaderName).Should().BeTrue();

        (await GetBalanceAsync(senderAccountId)).Should().Be(900m, "debited exactly once");
        (await CountTransactionsAsync(senderToken, senderAccountId)).Should().Be(2,
            "funding deposit + ONE outgoing transfer");
    }

    [Fact]
    public async Task Monetary201_CarriesNoLocationHeader()
    {
        // Replay fidelity guard: replays only restore status/body/content-type.
        // If a future refactor introduces CreatedAtAction (Location header),
        // this must fail so replay handling is extended.
        var (token, _, accountId) = await RegisterTestUserAsync();
        SetAuthHeader(token);

        var response = await PostMonetaryAsync("/api/transactions/deposit", NewDeposit(accountId, 10m));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location.Should().BeNull();
    }

    #endregion

    #region Key reuse / conflicts

    [Fact]
    public async Task Deposit_SameKeyDifferentBody_Returns422KeyReuse()
    {
        var (token, _, accountId) = await RegisterTestUserAsync();
        SetAuthHeader(token);
        var key = Guid.NewGuid();

        var first = await PostMonetaryAsync("/api/transactions/deposit", NewDeposit(accountId, 100m), key);
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await PostMonetaryAsync("/api/transactions/deposit", NewDeposit(accountId, 999m), key);

        second.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var problem = await ReadProblemAsync(second);
        problem.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.IdempotencyKeyReuse);

        (await GetBalanceAsync(accountId)).Should().Be(100m, "the second amount must not execute");
    }

    [Fact]
    public async Task Deposit_WhileInFlight_Returns409InFlight()
    {
        var (token, userId, accountId) = await RegisterTestUserAsync();
        SetAuthHeader(token);
        var key = Guid.NewGuid();
        var rawBody = RawJson(NewDeposit(accountId, 50m));

        // Simulate a live concurrent claim: a fresh (non-stale) Processing row
        SeedRecord(userId, "POST api/transactions/deposit", key, HashOf(rawBody),
            IdempotencyStatus.Processing, ageMinutes: 0);

        var response = await PostRawAsync("/api/transactions/deposit", rawBody, key);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await ReadProblemAsync(response);
        problem.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.IdempotencyInFlight);
    }

    [Fact]
    public async Task Deposit_WhileInFlightWithDifferentBody_Returns422KeyReuse()
    {
        var (token, userId, accountId) = await RegisterTestUserAsync();
        SetAuthHeader(token);
        var key = Guid.NewGuid();

        SeedRecord(userId, "POST api/transactions/deposit", key, HashOf("something-else"),
            IdempotencyStatus.Processing, ageMinutes: 0);

        var response = await PostRawAsync(
            "/api/transactions/deposit", RawJson(NewDeposit(accountId, 50m)), key);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var problem = await ReadProblemAsync(response);
        problem.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.IdempotencyKeyReuse);
    }

    [Fact]
    public async Task Deposit_StaleProcessingClaim_IsTakenOverAndExecutes()
    {
        var (token, userId, accountId) = await RegisterTestUserAsync();
        SetAuthHeader(token);
        var key = Guid.NewGuid();
        var rawBody = RawJson(NewDeposit(accountId, 75m));

        // A Processing row older than ProcessingStaleAfter (10min) means the
        // claimant died before committing anything (a commit would have
        // flipped it to Executed) -> takeover is safe.
        SeedRecord(userId, "POST api/transactions/deposit", key, HashOf(rawBody),
            IdempotencyStatus.Processing, ageMinutes: 11);

        var response = await PostRawAsync("/api/transactions/deposit", rawBody, key);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Contains(IdempotencyConstants.ReplayedHeaderName).Should().BeFalse();
        (await GetBalanceAsync(accountId)).Should().Be(75m);
    }

    [Fact]
    public async Task Deposit_ExecutedRecord_Returns409ResultUnknown()
    {
        var (token, userId, accountId) = await RegisterTestUserAsync();
        SetAuthHeader(token);
        var key = Guid.NewGuid();
        var rawBody = RawJson(NewDeposit(accountId, 50m));

        // Executed = the operation committed but the response was lost:
        // never re-execute, never invent a response.
        SeedRecord(userId, "POST api/transactions/deposit", key, HashOf(rawBody),
            IdempotencyStatus.Executed, ageMinutes: 30);

        var response = await PostRawAsync("/api/transactions/deposit", rawBody, key);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await ReadProblemAsync(response);
        problem.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.IdempotencyResultUnknown);
        (await GetBalanceAsync(accountId)).Should().Be(0m, "nothing may execute");
    }

    [Fact]
    public async Task Deposit_ExpiredCompletedRecord_ReExecutesFresh()
    {
        var (token, userId, accountId) = await RegisterTestUserAsync();
        SetAuthHeader(token);
        var key = Guid.NewGuid();
        var rawBody = RawJson(NewDeposit(accountId, 60m));

        // Past the 24h idempotency window the key is forgotten (documented semantics)
        SeedRecord(userId, "POST api/transactions/deposit", key, HashOf(rawBody),
            IdempotencyStatus.Completed, ageMinutes: 0, expired: true);

        var response = await PostRawAsync("/api/transactions/deposit", rawBody, key);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Contains(IdempotencyConstants.ReplayedHeaderName).Should().BeFalse(
            "expired records are treated as absent, not replayed");
        (await GetBalanceAsync(accountId)).Should().Be(60m);
    }

    #endregion

    #region Scope

    [Fact]
    public async Task SameKeyDifferentUsers_ExecuteIndependently()
    {
        var (tokenA, _, accountA) = await RegisterTestUserAsync();
        var (tokenB, _, accountB) = await RegisterTestUserAsync();
        var key = Guid.NewGuid();

        SetAuthHeader(tokenA);
        var responseA = await PostMonetaryAsync("/api/transactions/deposit", NewDeposit(accountA, 100m), key);
        responseA.StatusCode.Should().Be(HttpStatusCode.Created);

        SetAuthHeader(tokenB);
        var responseB = await PostMonetaryAsync("/api/transactions/deposit", NewDeposit(accountB, 100m), key);

        responseB.StatusCode.Should().Be(HttpStatusCode.Created,
            "keys are scoped per user: no cross-user replay, conflict, or leak");
        responseB.Headers.Contains(IdempotencyConstants.ReplayedHeaderName).Should().BeFalse();

        (await GetBalanceAsync(accountB)).Should().Be(100m);
    }

    [Fact]
    public async Task SameKeyDifferentEndpoint_ExecutesIndependently()
    {
        var (token, _, accountId) = await RegisterTestUserAsync();
        SetAuthHeader(token);
        await DepositAsync(token, accountId, 500m);
        await SetPinAsync(token);
        var key = Guid.NewGuid();

        var deposit = await PostMonetaryAsync("/api/transactions/deposit", NewDeposit(accountId, 100m), key);
        deposit.StatusCode.Should().Be(HttpStatusCode.Created);

        // Same key, different logical endpoint -> independent record (keys
        // are per-operation, documented in ADR-0009)
        var withdraw = await PostMonetaryAsync("/api/transactions/withdraw", new WithdrawRequest
        {
            AccountId = accountId,
            Amount = 50m,
            Pin = "123456",
            Description = "test"
        }, key);

        withdraw.StatusCode.Should().Be(HttpStatusCode.Created);
        (await GetBalanceAsync(accountId)).Should().Be(550m); // 500 + 100 - 50
    }

    #endregion

    #region Error handling

    [Fact]
    public async Task Withdraw_BusinessError_DoesNotBurnTheKey()
    {
        var (token, _, accountId) = await RegisterTestUserAsync();
        await SetPinAsync(token);
        SetAuthHeader(token);
        var key = Guid.NewGuid();
        var body = new WithdrawRequest
        {
            AccountId = accountId,
            Amount = 100m,
            Pin = "123456",
            Description = "retry me"
        };

        // Balance is 0 -> 422 INSUFFICIENT_FUNDS. Nothing committed, so the
        // claim must be released and the key stays usable.
        var failed = await PostMonetaryAsync("/api/transactions/withdraw", body, key);
        failed.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        await DepositAsync(token, accountId, 500m);

        var retry = await PostMonetaryAsync("/api/transactions/withdraw", body, key);
        retry.StatusCode.Should().Be(HttpStatusCode.Created,
            "an error response must not burn the idempotency key");
        retry.Headers.Contains(IdempotencyConstants.ReplayedHeaderName).Should().BeFalse();
        (await GetBalanceAsync(accountId)).Should().Be(400m);
    }

    [Fact]
    public async Task Deposit_ValidationError_DoesNotBurnTheKey()
    {
        var (token, _, accountId) = await RegisterTestUserAsync();
        SetAuthHeader(token);
        var key = Guid.NewGuid();

        var invalid = await PostMonetaryAsync("/api/transactions/deposit", NewDeposit(accountId, -5m), key);
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var retry = await PostMonetaryAsync("/api/transactions/deposit", NewDeposit(accountId, 5m), key);
        retry.StatusCode.Should().Be(HttpStatusCode.Created);
        (await GetBalanceAsync(accountId)).Should().Be(5m);
    }

    #endregion

    #region Payload size

    [Fact]
    public async Task MonetaryEndpoint_OversizedBody_Returns413_AndInsertsNoRecord()
    {
        var (token, userId, accountId) = await RegisterTestUserAsync();
        SetAuthHeader(token);
        var key = Guid.NewGuid();

        // ~40 KB body (> the 32 KB MaxRequestBodyBytes). StringContent sets a
        // real Content-Length, so the middleware's fast-path 413 guard fires
        // BEFORE any buffering, HMAC hashing, or claim INSERT.
        var oversized = RawJson(new DepositRequest
        {
            AccountId = accountId,
            Amount = 10m,
            Description = new string('x', 40_000)
        });

        var response = await PostRawAsync("/api/transactions/deposit", oversized, key);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        var problem = await ReadProblemAsync(response);
        problem.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.IdempotencyPayloadTooLarge);
        problem.TryGetProperty("traceId", out _).Should().BeTrue();

        // The 413 short-circuits before TryAcquireAsync: no claim row, no money moved.
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        db.IdempotencyRecords.Count(r => r.UserId == userId && r.Key == key)
            .Should().Be(0, "the 413 short-circuits before any claim INSERT");
        (await GetBalanceAsync(accountId)).Should().Be(0m);
    }

    #endregion

    #region Helpers

    private static DepositRequest NewDeposit(Guid accountId, decimal amount) => new()
    {
        AccountId = accountId,
        Amount = amount,
        Description = "Idempotency test"
    };

    private string RawJson<T>(T payload) => JsonSerializer.Serialize(payload, JsonOptions);

    /// <summary>
    /// Recomputes the middleware's request fingerprint: HMAC-SHA256 with the
    /// fixture's test key over the exact body bytes, lowercase hex.
    /// </summary>
    private static string HashOf(string rawBody)
    {
        using var hmac = new HMACSHA256(
            Encoding.UTF8.GetBytes(CustomWebApplicationFactory.IdempotencyHashKey));
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody)));
    }

    /// <summary>
    /// POSTs an exact raw JSON string (so its bytes match a fingerprint
    /// computed with <see cref="HashOf"/>) with the given idempotency key.
    /// </summary>
    private async Task<HttpResponseMessage> PostRawAsync(string url, string rawBody, Guid key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(rawBody, Encoding.UTF8, "application/json")
        };
        request.Headers.Add(IdempotencyConstants.HeaderName, key.ToString());
        return await Client.SendAsync(request);
    }

    /// <summary>
    /// Seeds an idempotency record directly, bypassing the middleware —
    /// simulates in-flight claims, crashes and expired windows. Also pins the
    /// endpoint-identity contract ("POST api/transactions/deposit"): if the
    /// middleware's endpoint naming drifted, these tests would break.
    /// </summary>
    private void SeedRecord(
        Guid userId, string endpoint, Guid key, string requestHash,
        IdempotencyStatus status, int ageMinutes, bool expired = false)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var now = DateTime.UtcNow;
        db.IdempotencyRecords.Add(new IdempotencyRecord
        {
            UserId = userId,
            Endpoint = endpoint,
            Key = key,
            ClaimId = Guid.NewGuid(),
            RequestHash = requestHash,
            Status = status,
            ResponseStatusCode = status == IdempotencyStatus.Completed ? 201 : null,
            ResponseBody = status == IdempotencyStatus.Completed ? """{"data":null,"message":"seeded"}""" : null,
            ResponseContentType = status == IdempotencyStatus.Completed ? "application/json" : null,
            CreatedAt = now.AddMinutes(-ageMinutes),
            ExpiresAt = expired ? now.AddMinutes(-1) : now.AddHours(24)
        });
        db.SaveChanges();
    }

    private async Task<decimal> GetBalanceAsync(Guid accountId)
    {
        var response = await Client.GetAsync($"/api/accounts/{accountId}/balance");
        response.EnsureSuccessStatusCode();
        var result = await response.Content
            .ReadFromJsonAsync<ApiResponse<AzureBank.Shared.DTOs.Account.BalanceResponse>>(JsonOptions);
        return result!.Data!.Balance;
    }

    private async Task<int> CountTransactionsAsync(string token, Guid accountId)
    {
        SetAuthHeader(token);
        var response = await Client.GetAsync($"/api/transactions?accountId={accountId}&pageSize=50");
        response.EnsureSuccessStatusCode();
        var result = await response.Content
            .ReadFromJsonAsync<PaginatedResponse<TransactionResponse>>(JsonOptions);
        return result!.Pagination.TotalItems;
    }

    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(text);
    }

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
