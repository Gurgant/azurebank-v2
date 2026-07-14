using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transaction;
using AzureBank.Shared.Enums;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Balance concurrency defense-in-depth on REAL SQL Server (independent of
/// idempotency — every request here carries a DIFFERENT key): parallel
/// operations on the SAME account race on its RowVersion. The optimistic
/// concurrency token + ConcurrencyRetry must yield:
/// - no lost updates (final balance == amount × successful requests)
/// - no 500s (conflicts are retried, not surfaced)
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

        var responses = await Task.WhenAll(
            Enumerable.Range(0, parallelism).Select(async i =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/transactions/withdraw")
                {
                    Content = JsonContent.Create(new WithdrawRequest
                    {
                        AccountId = accountId,
                        Amount = amount,
                        Pin = pin,
                        Description = $"Parallel withdrawal {i}"
                    }, options: Json)
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Add(IdempotencyConstants.HeaderName, Guid.NewGuid().ToString());
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

    private static async Task SetPinAsync(HttpClient client, string token, string pin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/pin")
        {
            Content = JsonContent.Create(new SetPinRequest { Pin = pin }, options: Json)
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

    private HttpClient CreateSqlClient()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
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
