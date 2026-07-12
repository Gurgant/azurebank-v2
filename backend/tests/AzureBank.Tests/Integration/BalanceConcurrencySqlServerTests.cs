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
