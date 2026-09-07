using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transaction;
using AzureBank.Shared.DTOs.Transfer;
using AzureBank.Shared.Enums;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The host these tests run against: the real composition root with the day's ceiling set to 500
/// and the host's clock swapped for a fake, so the day boundary can be crossed on demand.
/// </summary>
public sealed class DailyLimitFactory : CustomWebApplicationFactory
{
    public const decimal Limit = 500m;

    public DailyLimitFactory()
    {
        SetDailyLimit(Limit);
        UseFakeClock();
    }
}

/// <summary>
/// The day's ceiling on outgoing external transfers (ADR-0050), through the real endpoints and the
/// real DI — the mint, the transfer, the exclusions, the idempotency interplay and the day
/// boundary.
/// </summary>
/// <remarks>
/// <para>
/// The scenario is deliberately ONE sequence rather than one fact per rung: the rungs only mean
/// something in relation to each other (a mint that does not reserve, followed by two spends of
/// which the second must lose), and the numbers each step asserts are what ADR-0050 quotes.
/// </para>
/// <para>
/// InMemory host, so the per-user application lock is skipped (the IsRelational guard) and the
/// concurrency property is NOT proven here — that is <c>DailyLimitConcurrencySqlServerTests</c>.
/// What this host does prove: every check exists, sits where the ADR says, and answers the body it
/// says.
/// </para>
/// </remarks>
public class DailyLimitEndpointTests : IntegrationTestBase, IClassFixture<DailyLimitFactory>
{
    private const string Pin = "123456";
    private readonly DailyLimitFactory _factory;

    public DailyLimitEndpointTests(DailyLimitFactory factory) : base(factory)
    {
        _factory = factory;
    }

    private sealed record Problem(
        HttpStatusCode Status, string? ErrorCode, string? Detail, JsonElement Body, HttpResponseMessage Response);

    private static async Task<Problem> ReadAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);
        var root = doc.RootElement.Clone();
        return new Problem(
            response.StatusCode,
            root.TryGetProperty("errorCode", out var c) ? c.GetString() : null,
            root.TryGetProperty("detail", out var d) ? d.GetString() : null,
            root,
            response);
    }

    private async Task<string> RegisterPayeeTagAsync()
    {
        var (_, payeeId, _) = await RegisterTestUserAsync();
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return (await db.Users.AsNoTracking().SingleAsync(u => u.Id == payeeId)).AzureTag;
    }

    private Task<HttpResponseMessage> MintAsync(Guid fromAccountId, string payeeTag, decimal amount, string pin = Pin)
        => Client.PostAsJsonAsync(
            "/api/transfers/authorizations",
            new TransferAuthorizationRequest
            {
                FromAccountId = fromAccountId, RecipientAzureTag = payeeTag, Amount = amount, Pin = pin
            },
            JsonOptions);

    private static async Task<Guid> AuthorizationIdOf(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<ApiResponse<StepUpAuthorizationResponse>>(JsonOptions))!
            .Data!.AuthorizationId;

    private Task<HttpResponseMessage> SpendAsync(
        Guid fromAccountId, string payeeTag, decimal amount, Guid authorization, Guid? idempotencyKey = null)
        => PostMonetaryAsync(
            "/api/transfers",
            new TransferRequest { FromAccountId = fromAccountId, RecipientAzureTag = payeeTag, Amount = amount },
            idempotencyKey,
            authorization);

    private async Task<decimal> BalanceAsync(Guid accountId)
    {
        var response = await Client.GetAsync($"/api/accounts/{accountId}/balance");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ApiResponse<BalanceResponse>>(JsonOptions))!.Data!.Balance;
    }

    private async Task<int> FailedPinAttemptsAsync(Guid userId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return (await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId)).PinAccessFailedCount;
    }

    private async Task<StepUpAuthorizationStatus> StatusOfAsync(Guid authorization)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return (await db.StepUpAuthorizations.AsNoTracking().SingleAsync(a => a.Id == authorization)).Status;
    }

    private static void ShouldBeTheRefusal(
        Problem problem, decimal limit, decimal used, decimal requested, DateTime resetsAt)
    {
        problem.Status.Should().Be(HttpStatusCode.UnprocessableEntity);
        problem.ErrorCode.Should().Be(ErrorCodes.DailyLimitExceeded);
        problem.Detail.Should().Be("Daily transfer limit exceeded.");

        // Top-level NUMBERS, not strings, exactly as available/requested ride on INSUFFICIENT_FUNDS
        // (measured 2026-09-07, B2: extra={'available': 250.0, 'requested': 400}); resetsAt is a
        // string.
        problem.Body.GetProperty("limit").ValueKind.Should().Be(JsonValueKind.Number);
        problem.Body.GetProperty("limit").GetDecimal().Should().Be(limit);
        problem.Body.GetProperty("used").GetDecimal().Should().Be(used);
        problem.Body.GetProperty("requested").GetDecimal().Should().Be(requested);
        problem.Body.GetProperty("resetsAt").ValueKind.Should().Be(JsonValueKind.String);
        problem.Body.GetProperty("resetsAt").GetDateTime().ToUniversalTime().Should().Be(resetsAt);
        problem.Body.GetProperty("resetsAt").GetString().Should().EndWith("Z", "a UTC instant, with its Z");
        problem.Body.TryGetProperty("traceId", out _).Should().BeTrue();
    }

    [Fact]
    public async Task TheDay_ThroughTheCompositionRoot()
    {
        var limit = DailyLimitFactory.Limit;
        var today = _factory.Clock.GetUtcNow().UtcDateTime.Date;
        var resetsAt = today.AddDays(1);

        var payeeTag = await RegisterPayeeTagAsync();
        var (token, userId, accountId) = await RegisterTestUserAsync();
        SetAuthHeader(token);
        await SetPinAsync(token);
        await DepositAsync(token, accountId, 2_000m);

        // ── 1. mint + transfer 300 → 201: a first external transfer inside the day
        var first = await MintAsync(accountId, payeeTag, 300m);
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        (await SpendAsync(accountId, payeeTag, 300m, await AuthorizationIdOf(first)))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        // ── 2. mint 200.01 with a WRONG PIN → 422 DAILY_LIMIT_EXCEEDED, not 401: the ceiling is
        //       checked before the PIN is consulted, so no attempt is spent
        var doomed = await ReadAsync(await MintAsync(accountId, payeeTag, 200.01m, pin: "999999"));
        ShouldBeTheRefusal(doomed, limit, used: 300m, requested: 200.01m, resetsAt);
        (await FailedPinAttemptsAsync(userId)).Should().Be(0, "the wrong PIN was never looked at");

        //       …and a correct-PIN mint of 100 right after → 201, no lockout, no attempt spent
        var hundred = await MintAsync(accountId, payeeTag, 100m);
        hundred.StatusCode.Should().Be(HttpStatusCode.Created);
        (await FailedPinAttemptsAsync(userId)).Should().Be(0);

        // ── 3. two mints of 200 → both 201: the mint does not reserve (used is still 300)
        var a = await MintAsync(accountId, payeeTag, 200m);
        var b = await MintAsync(accountId, payeeTag, 200m);
        a.StatusCode.Should().Be(HttpStatusCode.Created);
        b.StatusCode.Should().Be(HttpStatusCode.Created);
        var authA = await AuthorizationIdOf(a);
        var authB = await AuthorizationIdOf(b);

        // ── 4. spend the first → 201 (used 500, the day is exactly full)
        (await SpendAsync(accountId, payeeTag, 200m, authA)).StatusCode.Should().Be(HttpStatusCode.Created);
        var balanceAtTheCeiling = await BalanceAsync(accountId);
        balanceAtTheCeiling.Should().Be(1_500m);

        // ── 5. spend the second → 422: the authorisation stays Pending, the balance does not move,
        //       and the same Idempotency-Key retried answers 422 again with no replay header
        var key = Guid.NewGuid();
        var refused = await ReadAsync(await SpendAsync(accountId, payeeTag, 200m, authB, key));
        ShouldBeTheRefusal(refused, limit, used: 500m, requested: 200m, resetsAt);
        (await StatusOfAsync(authB)).Should().Be(StepUpAuthorizationStatus.Pending);
        (await BalanceAsync(accountId)).Should().Be(balanceAtTheCeiling);

        var retried = await SpendAsync(accountId, payeeTag, 200m, authB, key);
        retried.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, "a 422 releases the claim (ADR-0009)");
        retried.Headers.Contains(IdempotencyConstants.ReplayedHeaderName).Should().BeFalse(
            "a refusal is re-evaluated, never replayed");
        (await ReadAsync(retried)).ErrorCode.Should().Be(ErrorCodes.DailyLimitExceeded);

        // ── 6. after the day is exhausted, the excluded rails still answer 201 (ADR-0050 D2)
        var created = await Client.PostAsJsonAsync(
            "/api/accounts", new CreateAccountRequest { Name = "Second", Type = AccountType.Savings }, JsonOptions);
        created.EnsureSuccessStatusCode();
        var secondAccountId = (await created.Content.ReadFromJsonAsync<ApiResponse<AccountResponse>>(JsonOptions))!.Data!.Id;

        var internalAuth = await AuthoriseInternalTransferAsync(accountId, secondAccountId, 100m);
        (await PostMonetaryAsync(
                "/api/transfers/internal",
                new InternalTransferRequest { FromAccountId = accountId, ToAccountId = secondAccountId, Amount = 100m },
                stepUpAuthorizationId: internalAuth))
            .StatusCode.Should().Be(HttpStatusCode.Created, "an internal move is not counted");

        (await PostMonetaryAsync(
                "/api/transactions/withdraw",
                new WithdrawRequest { AccountId = accountId, Amount = 100m, Pin = Pin, Description = "cash" }))
            .StatusCode.Should().Be(HttpStatusCode.Created, "a withdrawal is a different rail");

        // ── 7. cross midnight on the host's one clock → a mint of 500 → 201: the day boundary
        //       through the composition root, on the same clock that stamped every row above
        _factory.Clock.SetUtcNow(new DateTimeOffset(resetsAt, TimeSpan.Zero).AddSeconds(1));

        var tomorrow = await MintAsync(accountId, payeeTag, 500m);
        tomorrow.StatusCode.Should().Be(HttpStatusCode.Created, "yesterday's 500 no longer count");
    }

    [Fact]
    public async Task ARequestAboveTheCeilingOnItsOwn_IsRefusedAtTheMint_WithZeroUsed()
    {
        // The cheap contract row the real-stack suite will use: one per-transaction-valid request
        // of limit + 0.01 refuses with nothing moved and nothing spent.
        var payeeTag = await RegisterPayeeTagAsync();
        var (token, _, accountId) = await RegisterTestUserAsync();
        SetAuthHeader(token);
        await SetPinAsync(token);

        var refusal = await ReadAsync(await MintAsync(accountId, payeeTag, DailyLimitFactory.Limit + 0.01m));

        refusal.Status.Should().Be(HttpStatusCode.UnprocessableEntity);
        refusal.ErrorCode.Should().Be(ErrorCodes.DailyLimitExceeded);
        refusal.Body.GetProperty("used").GetDecimal().Should().Be(0m);
        (await BalanceAsync(accountId)).Should().Be(0m);
    }

    [Fact]
    public async Task TheMint_StillRefusesAnUnknownPayee_BeforeTheDaysCeiling()
    {
        // ORDER at the mint: the payee resolution stays above the daily check, so the mint refuses
        // in the transfer's order (ADR-0050's rung table) — a doomed-for-the-day request to a
        // handle that does not exist answers 404, not the daily code. This says NOTHING about
        // enumeration, and the earlier wording here claimed it did: the enumeration argument at the
        // top of TransferService is the TRANSFER's (RequireAuthorization sits above its payee
        // resolution), while the mint answers that 404 before the PIN — on main too. See the
        // comment beside AuthoriseTransferAsync's daily call.
        var (token, _, accountId) = await RegisterTestUserAsync();
        SetAuthHeader(token);
        await SetPinAsync(token);

        var response = await MintAsync(accountId, "nobody_here_at_all", DailyLimitFactory.Limit + 1m);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
