using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Api.Handlers;
using AzureBank.Api.Services;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transaction;
using AzureBank.Shared.DTOs.Transfer;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Exceptions;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Events;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Balance concurrency defense-in-depth on REAL SQL Server (independent of
/// idempotency — every request here carries a DIFFERENT key): parallel
/// operations on the SAME account race on its RowVersion. The optimistic
/// concurrency token + ConcurrencyRetry must yield:
/// - no lost updates (final balance == amount × successful requests)
/// - no 500s (conflicts are retried, not surfaced)
/// - past the retry's bound, a deposit is answered the 503 a client can retry, and is not applied
///
/// Requires a real database: the InMemory test path downgrades rowversion
/// tokens, so conflicts never occur there.
/// </summary>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class BalanceConcurrencySqlServerTests : IDisposable
{
    private const int Parallelism = 6;

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly ITestOutputHelper _output;
    private CustomWebApplicationFactory? _factory;

    public BalanceConcurrencySqlServerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SqlServerFact]
    public async Task ParallelDepositsWithDistinctKeys_LoseNoUpdates()
    {
        var client = CreateSqlClient();
        var (token, accountId) = await RegisterAsync(client);
        const decimal amount = 25m;

        var responses = await Task.WhenAll(
            Enumerable.Range(0, Parallelism).Select(async i =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/transactions/deposit")
                {
                    Content = JsonContent.Create(new DepositRequest
                    {
                        AccountId = accountId,
                        Amount = amount,
                        Description = $"Parallel deposit {i}"
                    }, options: Json)
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Add(IdempotencyConstants.HeaderName, Guid.NewGuid().ToString());
                var response = await client.SendAsync(request);
                return (response.StatusCode, Body: await response.Content.ReadAsStringAsync());
            }));

        var successes = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        _output.WriteLine($"successes: {successes}/{Parallelism}");

        // Availability: the RowVersion conflicts must be absorbed by the retry
        responses.Where(r => r.StatusCode != HttpStatusCode.Created).Should().BeEmpty(
            "parallel same-account deposits must be retried, not surfaced as errors; got: " +
            string.Join(" | ", responses.Where(r => r.StatusCode != HttpStatusCode.Created)
                .Select(r => $"{(int)r.StatusCode}: {r.Body[..Math.Min(150, r.Body.Length)]}")));

        // Correctness: NO lost updates
        (await GetBalanceAsync(client, token, accountId)).Should().Be(
            amount * successes, "every successful deposit must be reflected in the balance");
    }

    /// <summary>
    /// More parallel deposits to one account than the retry carries: each is answered 201 and
    /// applied once, or answered the busy 503 and not applied. None is a 500.
    /// </summary>
    [SqlServerFact]
    public async Task MoreParallelDepositsThanTheRetryCarries_AreAppliedOnce_OrAnsweredTheBusy503()
    {
        const int parallelism = 24;

        var client = CreateSqlClient(factory => factory.CaptureLog(LogEventLevel.Information));
        var (token, accountId) = await RegisterAsync(client);
        var start = await GetBalanceAsync(client, token, accountId);

        // An amount and a description of its own for each request, so that the balance and the
        // ledger say WHICH deposits were applied, not only how many.
        var clock = Stopwatch.StartNew();
        var answers = await Task.WhenAll(
            Enumerable.Range(0, parallelism).Select(i => SendDepositAsync(
                client, token, accountId, 10m + i, $"Busy deposit {i}", Guid.NewGuid())));
        clock.Stop();

        var applied = answers.Where(a => a.Status == HttpStatusCode.Created).ToArray();
        var refused = answers.Where(a => a.Status != HttpStatusCode.Created).ToArray();
        var conflicts = _factory!.CapturedEvents
            .Where(e => e.MessageTemplate.Text.StartsWith("Concurrency conflict on deposit", StringComparison.Ordinal))
            .Select(e => int.Parse(e.Properties["Attempt"].ToString(), CultureInfo.InvariantCulture))
            .ToArray();
        var gaveUp = _factory.CapturedLog.Count(l => l.Contains("lost the account to another write", StringComparison.Ordinal));
        var balance = await GetBalanceAsync(client, token, accountId);
        var ledger = await ReadDepositsAsync(client, token, accountId);
        var auditRows = await CountDepositAuditRowsAsync(accountId);

        _output.WriteLine(
            $"{parallelism} parallel deposits at {ConcurrencyRetry.MaxAttempts} attempts in {clock.ElapsedMilliseconds} ms: "
            + string.Join(", ", answers.GroupBy(a => (int)a.Status).OrderBy(g => g.Key).Select(g => $"{g.Count()} x {g.Key}"))
            + $"; busy with applied false: {refused.Count(a => a.AppliedIsFalse)}; gave up in the log: {gaveUp}; "
            + $"balance {start} -> {balance}, the 201s sum to {applied.Sum(a => a.Amount)}; "
            + $"ledger rows {ledger.Length}, audit rows {auditRows}; "
            + $"{conflicts.Length} conflicts retried, by attempt: "
            + string.Join(", ", conflicts.GroupBy(a => a).OrderBy(g => g.Key).Select(g => $"{g.Key}: {g.Count()}")));

        answers.Where(a => a.Status == HttpStatusCode.InternalServerError).Should().BeEmpty(
            "a deposit that cannot be made now is a deposit to send again, not a fault of the API; got: "
            + string.Join(" | ", answers.Where(a => a.Status == HttpStatusCode.InternalServerError)
                .Select(a => $"{(int)a.Status}: {a.Body[..Math.Min(150, a.Body.Length)]}")));

        foreach (var answer in refused)
        {
            AssertBusy503(answer);
        }

        gaveUp.Should().Be(refused.Length, "each busy answer is one deposit that spent its attempts");

        balance.Should().Be(
            start + applied.Sum(a => a.Amount),
            "the balance holds every deposit answered 201 and none answered 503");

        ledger.Should().BeEquivalentTo(
            applied.Select(a => (a.Description, a.Amount)),
            "each deposit answered 201 is in the ledger once, and no other deposit is");

        auditRows.Should().Be(
            applied.Length, "the audit trail records each deposit made once, and no attempt that lost");
    }

    /// <summary>
    /// A deposit that loses the race for its account on every attempt is answered the busy 503,
    /// changes nothing, and leaves its key free: the same request sent again is made.
    /// </summary>
    [SqlServerFact]
    public async Task ADepositThatLosesEveryAttempt_AnswersTheBusy503_ChangesNothing_AndIsMadeWhenSentAgain()
    {
        const decimal amount = 40m;
        const string description = "Outrun deposit";

        var race = new LostRowVersionRaceInterceptor(SqlServerFactAttribute.ConnectionString!);
        var client = CreateSqlClient(factory =>
        {
            factory.AddInterceptor(race);
            factory.CaptureLog();
        });
        var (token, accountId) = await RegisterAsync(client);
        var start = await GetBalanceAsync(client, token, accountId);
        var key = Guid.NewGuid();

        race.Arm(accountId);
        var refused = await SendDepositAsync(client, token, accountId, amount, description, key);
        race.Disarm();

        var balance = await GetBalanceAsync(client, token, accountId);
        var ledger = await ReadDepositsAsync(client, token, accountId);
        var auditRows = await CountDepositAuditRowsAsync(accountId);
        var records = await ReadIdempotencyRecordsAsync(key);

        // The 503 says to send the request again. The same key and the same bytes, sent again.
        var again = await SendDepositAsync(client, token, accountId, amount, description, key);

        _output.WriteLine(
            $"every attempt lost: fired {race.Fired} of {ConcurrencyRetry.MaxAttempts} attempts; "
            + $"{(int)refused.Status}, Retry-After {refused.RetryAfter}, body {refused.Body}; "
            + $"balance {start} -> {balance}, ledger rows {ledger.Length}, audit rows {auditRows}, "
            + $"idempotency records [{string.Join(",", records)}]; "
            + $"same key again: {(int)again.Status}, replayed: {again.Replayed}");
        foreach (var line in _factory!.CapturedLog)
        {
            _output.WriteLine("  log " + line[..Math.Min(300, line.Length)]);
        }

        race.Fired.Should().Be(
            ConcurrencyRetry.MaxAttempts, "the proof is void unless the deposit lost every attempt it is given");
        AssertBusy503(refused);
        balance.Should().Be(start, "a deposit answered 503 moved nothing");
        ledger.Should().BeEmpty("a deposit answered 503 left no ledger row");
        auditRows.Should().Be(0, "no attempt that lost left an audit row");
        records.Should().BeEmpty("the claim of a deposit answered 503 is released, so its key is free");

        again.Status.Should().Be(HttpStatusCode.Created, $"the key of a deposit answered 503 is free; the body was: {again.Body}");
        again.Replayed.Should().BeFalse("no earlier answer exists to replay");
        (await GetBalanceAsync(client, token, accountId)).Should().Be(start + amount);
        (await ReadDepositsAsync(client, token, accountId)).Should().Equal((description, amount));
        (await CountDepositAuditRowsAsync(accountId)).Should().Be(1, "the deposit that was made has its one audit row");
    }

    /// <summary>
    /// A deposit whose idempotency claim is taken away at its save did not lose a race for its
    /// account: it is not answered the busy 503, and it is not tried again.
    /// </summary>
    [SqlServerFact]
    public async Task ADepositWhoseClaimIsTakenAwayAtItsSave_IsNotToldToSendItAgain()
    {
        var fence = new FencedOutClaimInterceptor(SqlServerFactAttribute.ConnectionString!);
        var client = CreateSqlClient(factory =>
        {
            factory.AddInterceptor(fence);
            factory.CaptureLog();
        });
        var (token, accountId) = await RegisterAsync(client);
        var start = await GetBalanceAsync(client, token, accountId);
        var key = Guid.NewGuid();

        fence.Arm(key);
        var answer = await SendDepositAsync(client, token, accountId, 40m, "Fenced deposit", key);
        fence.Disarm();

        var balance = await GetBalanceAsync(client, token, accountId);
        var ledger = await ReadDepositsAsync(client, token, accountId);
        var auditRows = await CountDepositAuditRowsAsync(accountId);

        _output.WriteLine(
            $"claim taken away at the save: fired {fence.Fired}; {(int)answer.Status}, "
            + $"Retry-After {answer.RetryAfter?.ToString() ?? "none"}, body {answer.Body}; "
            + $"balance {start} -> {balance}, ledger rows {ledger.Length}, audit rows {auditRows}");
        foreach (var line in _factory!.CapturedLog)
        {
            _output.WriteLine("  log " + line[..Math.Min(300, line.Length)]);
        }

        fence.Fired.Should().Be(1, "the claim is taken away at the one save the deposit sends, and no second save follows");
        answer.Status.Should().Be(
            HttpStatusCode.InternalServerError,
            "a claim that is no longer the request's own is not a busy account: another request may hold the key");
        answer.RetryAfter.Should().BeNull("nothing invites the caller to send it again");
        var body = JsonSerializer.Deserialize<JsonElement>(answer.Body);
        body.TryGetProperty("retryAfterSeconds", out _).Should().BeFalse();
        body.TryGetProperty("applied", out _).Should().BeFalse("a request without its claim knows nothing about the key");
        balance.Should().Be(start);
        ledger.Should().BeEmpty();
        auditRows.Should().Be(0);
    }

    [SqlServerFact]
    public async Task ParallelWithdrawalsWithDistinctKeys_NeverOverdraw()
    {
        const int parallelism = 24;
        const decimal amount = 100m;
        const string pin = "123456";

        var client = CreateSqlClient();
        var (token, accountId) = await RegisterAsync(client);
        await SetPinAsync(client, token, pin);
        // Fund with EXACTLY enough for ONE withdrawal.
        await DepositAsync(client, token, accountId, amount);

        // One authorisation per racer, minted BEFORE the race and sequentially -- see the remark on
        // MintWithdrawalAsync for why sharing one would quietly change what this test proves.
        var authorizations = new Guid[parallelism];
        for (var i = 0; i < parallelism; i++)
        {
            authorizations[i] = await MintWithdrawalAsync(client, token, accountId, amount, pin);
        }

        var responses = await Task.WhenAll(
            Enumerable.Range(0, parallelism).Select(async i =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/transactions/withdraw")
                {
                    Content = JsonContent.Create(new WithdrawRequest
                    {
                        AccountId = accountId,
                        Amount = amount,
                        Description = $"Parallel withdrawal {i}"
                    }, options: Json)
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Add(IdempotencyConstants.HeaderName, Guid.NewGuid().ToString());
                request.Headers.Add(StepUpConstants.HeaderName, authorizations[i].ToString());
                var response = await client.SendAsync(request);
                return (response.StatusCode, Body: await response.Content.ReadAsStringAsync());
            }));

        var successes = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        var rejected = responses.Where(r => r.StatusCode == HttpStatusCode.UnprocessableEntity).ToArray();
        _output.WriteLine($"successes: {successes}/{parallelism}, rejected: {rejected.Length}");

        // Safety: a balance that covers ONE withdrawal must permit EXACTLY one.
        // Each loser's RowVersion conflict reloads a 0 balance and fails the funds
        // check (InsufficientFunds) — it must NEVER surface as a 500.
        successes.Should().Be(1,
            "a balance covering one withdrawal must permit exactly one; unexpected non-201/422 responses: " +
            string.Join(" | ", responses
                .Where(r => r.StatusCode is not HttpStatusCode.Created and not HttpStatusCode.UnprocessableEntity)
                .Select(r => $"{(int)r.StatusCode}: {r.Body[..Math.Min(150, r.Body.Length)]}")));

        rejected.Should().HaveCount(parallelism - 1, "every loser reloads a 0 balance and is refused");
        rejected.Should().OnlyContain(
            r => r.Body.Contains(ErrorCodes.InsufficientFunds, StringComparison.Ordinal),
            "losing withdrawals must fail as InsufficientFunds, not any other error");

        // Correctness: the balance settled at EXACTLY 0 and never went negative.
        (await GetBalanceAsync(client, token, accountId)).Should().Be(
            0m, "one withdrawal of the full balance must leave 0 - never negative");

        // Exactly ONE withdrawal transaction row was persisted.
        (await CountWithdrawalsAsync(client, token, accountId)).Should().Be(
            1, "only the single successful withdrawal may persist a transaction row");
    }

    /// <summary>
    /// Mints ONE withdrawal authorisation, bound to this account and amount.
    /// </summary>
    /// <remarks>
    /// ⚠️ ONE PER PARALLEL REQUEST, AND MINTED SEQUENTIALLY BEFORE THE RACE. An authorisation is
    /// single-use by construction (ADR-0042), so if the twenty-four racers below shared one
    /// reference, twenty-three would be refused AUTHORIZATION_INVALID and exactly one would
    /// succeed -- and the balance assertion would still pass. The test would go on being green
    /// while it had silently stopped being a BALANCE proof and become an authorisation proof.
    /// Distinct authorisations are what keep the overdraft guard the only thing under test.
    /// </remarks>
    private static async Task<Guid> MintWithdrawalAsync(
        HttpClient client, string token, Guid accountId, decimal amount, string pin)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, "/api/transactions/withdraw/authorizations")
        {
            Content = JsonContent.Create(
                new WithdrawalAuthorizationRequest { AccountId = accountId, Amount = amount, Pin = pin },
                options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<StepUpAuthorizationResponse>>(Json);
        return body!.Data!.AuthorizationId;
    }

    private static async Task SetPinAsync(HttpClient client, string token, string pin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/pin")
        {
            Content = JsonContent.Create(new SetPinRequest { Pin = pin, Password = "TestPass123!" }, options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private static async Task DepositAsync(HttpClient client, string token, Guid accountId, decimal amount)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/transactions/deposit")
        {
            Content = JsonContent.Create(new DepositRequest
            {
                AccountId = accountId,
                Amount = amount,
                Description = "Funding"
            }, options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(IdempotencyConstants.HeaderName, Guid.NewGuid().ToString());
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<int> CountWithdrawalsAsync(HttpClient client, string token, Guid accountId)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/transactions?accountId={accountId}&pageSize=50");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<PaginatedResponse<TransactionResponse>>(Json);
        return result!.Data.Count(t => t.Type == TransactionType.Withdrawal);
    }

    /// <summary>One deposit's answer, with what the request asked for.</summary>
    private sealed record DepositAnswer(
        decimal Amount, string Description, HttpStatusCode Status, TimeSpan? RetryAfter, bool NoStore,
        bool Replayed, string Body)
    {
        /// <summary>True when the body carries <c>applied</c> and it is <c>false</c>.</summary>
        public bool AppliedIsFalse =>
            JsonSerializer.Deserialize<JsonElement>(Body) is { ValueKind: JsonValueKind.Object } body
            && body.TryGetProperty("applied", out var applied)
            && applied.ValueKind == JsonValueKind.False;
    }

    private static async Task<DepositAnswer> SendDepositAsync(
        HttpClient client, string token, Guid accountId, decimal amount, string description, Guid key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/transactions/deposit")
        {
            Content = JsonContent.Create(new DepositRequest
            {
                AccountId = accountId,
                Amount = amount,
                Description = description
            }, options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(IdempotencyConstants.HeaderName, key.ToString());
        using var response = await client.SendAsync(request);
        return new DepositAnswer(
            amount,
            description,
            response.StatusCode,
            response.Headers.RetryAfter?.Delta,
            response.Headers.CacheControl?.NoStore == true,
            response.Headers.Contains(IdempotencyConstants.ReplayedHeaderName),
            await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The answer of a deposit whose account stayed busy: 503 <c>SERVICE_UNAVAILABLE</c>, never
    /// cached, a wait of one second in the body and in the <c>Retry-After</c> header, and
    /// <c>applied: false</c> with the sentence that says nothing was changed.
    /// </summary>
    private static void AssertBusy503(DepositAnswer answer)
    {
        answer.Status.Should().Be(
            HttpStatusCode.ServiceUnavailable,
            $"a deposit that was not made is answered the 503 a client can retry; the body was: {answer.Body}");
        answer.NoStore.Should().BeTrue("a 503 describes a moment and must not be cached");

        var body = JsonSerializer.Deserialize<JsonElement>(answer.Body);
        body.GetProperty("status").GetInt32().Should().Be(503);
        body.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.ServiceUnavailable);
        body.GetProperty("retryAfterSeconds").GetInt32().Should().Be(
            AccountBusyException.RetryAfterSeconds, "a busy account names its own wait, not the ten seconds of an outage");
        answer.RetryAfter.Should().Be(
            TimeSpan.FromSeconds(AccountBusyException.RetryAfterSeconds),
            "the Retry-After header carries the wait the body names");
        answer.AppliedIsFalse.Should().BeTrue($"nothing was written, and the answer says so; the body was: {answer.Body}");
        body.GetProperty("detail").GetString().Should().Be(ServiceUnavailableExceptionHandler.BusyNothingAppliedDetail);
    }

    /// <summary>The account's deposits as the ledger holds them: description and amount.</summary>
    private static async Task<(string Description, decimal Amount)[]> ReadDepositsAsync(
        HttpClient client, string token, Guid accountId)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/transactions?accountId={accountId}&pageSize=100");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<PaginatedResponse<TransactionResponse>>(Json);
        return result!.Data
            .Where(t => t.Type == TransactionType.Deposit)
            .Select(t => (t.Description!, t.Amount))
            .ToArray();
    }

    /// <summary>How many deposits the audit trail records for the user who holds the account.</summary>
    private async Task<int> CountDepositAuditRowsAsync(Guid accountId)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var userId = (await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == accountId)).UserId;
        return await db.AuditEvents.AsNoTracking()
            .CountAsync(e => e.Event == SecurityEvents.MoneyDeposited && e.ActorUserId == userId);
    }

    /// <summary>The status of every idempotency record under the key, read in a scope of its own.</summary>
    private async Task<List<IdempotencyStatus>> ReadIdempotencyRecordsAsync(Guid key)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return await db.IdempotencyRecords.AsNoTracking()
            .Where(r => r.Key == key)
            .Select(r => r.Status)
            .ToListAsync();
    }

    private HttpClient CreateSqlClient(Action<CustomWebApplicationFactory>? configure = null)
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        configure?.Invoke(_factory);
        return _factory.CreateClient();
    }

    private static async Task<(string Token, Guid AccountId)> RegisterAsync(HttpClient client)
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"balance_{uniqueId}",
            Email = $"balance{uniqueId}@example.com",
            Password = "TestPass123!",
            FirstName = "Balance",
            LastName = "Prover"
        }, Json);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json);
        return (result!.Data!.Token.AccessToken, result.Data.Account.Id);
    }

    private static async Task<decimal> GetBalanceAsync(HttpClient client, string token, Guid accountId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/accounts/{accountId}/balance");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<BalanceResponse>>(Json);
        return result!.Data!.Balance;
    }

    public void Dispose()
    {
        _factory?.Dispose();
    }
}
