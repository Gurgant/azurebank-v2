using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Proves the PIN failure counter is ATOMIC on real SQL Server: exactly
/// MaxPinAttempts wrong PINs fired in PARALLEL must still lock the account. A
/// read-modify-write counter would lose updates under this contention and leave
/// the account unlocked — letting an attacker exceed the threshold. The set-based
/// ExecuteUpdate increment prevents that. InMemory can't exhibit the race (it has
/// no real concurrency), so this proof is SQL-gated.
/// </summary>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class PinLockoutConcurrencySqlServerTests : IDisposable
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly ITestOutputHelper _output;
    private CustomWebApplicationFactory? _factory;

    public PinLockoutConcurrencySqlServerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SqlServerFact]
    public async Task ExactlyThresholdParallelWrongPins_LockTheAccount()
    {
        const string pin = "123456";
        var client = CreateSqlClient();
        var (token, _) = await RegisterAsync(client);
        await SetPinAsync(client, token, pin);

        // Fire EXACTLY MaxPinAttempts wrong PINs in PARALLEL. With an atomic
        // counter every increment lands, so the threshold is reached and the PIN
        // locks; a lost-update read-modify-write could leave the count below it.
        var statuses = await Task.WhenAll(
            Enumerable.Range(0, ValidationRules.MaxPinAttempts)
                .Select(_ => VerifyPinAsync(client, token, "000000")));
        _output.WriteLine("parallel wrong-PIN statuses: " + string.Join(",", statuses.Select(s => (int)s)));

        // The PIN is now locked: even the CORRECT PIN is refused with 429.
        var afterLock = await VerifyPinAsync(client, token, pin);
        afterLock.Should().Be(HttpStatusCode.TooManyRequests,
            "exactly MaxPinAttempts parallel wrong PINs must lock the account (atomic counter, no lost updates)");
    }

    [SqlServerFact]
    public async Task BurstOverThreshold_StaysLocked_NoRaceBypass()
    {
        const string pin = "123456";
        var client = CreateSqlClient();

        // A fresh user each round exercises the unlocked -> burst -> locked path.
        // Firing 3x the threshold in parallel would, before the single-statement
        // atomic fix, let a late increment clear a just-applied lock and leave the
        // account UNLOCKED. It must stay locked every round.
        for (var round = 0; round < 3; round++)
        {
            var (token, tag) = await RegisterAsync(client);
            await SetPinAsync(client, token, pin);

            await Task.WhenAll(Enumerable.Range(0, ValidationRules.MaxPinAttempts * 3)
                .Select(_ => VerifyPinAsync(client, token, "000000")));

            (await VerifyPinAsync(client, token, pin)).Should().Be(HttpStatusCode.TooManyRequests,
                $"round {round}: a burst of parallel wrong PINs must leave the account LOCKED, never bypassed");

            // The atomic WHERE-guard leaves NO residual count on a just-locked account,
            // so the next window (after expiry) gets a full budget (ADR-0010, symmetric
            // with the login lockout proof).
            (await ReadPinAccessFailedCountAsync(tag)).Should().Be(0,
                $"round {round}: a late concurrent increment must not survive onto a locked row");
        }
    }

    [SqlServerFact]
    public async Task CorrectPinAfterFailures_ResetsWindow_OnSqlServer()
    {
        const string pin = "123456";
        var client = CreateSqlClient();
        var (token, _) = await RegisterAsync(client);
        await SetPinAsync(client, token, pin);

        // (Max-1) wrong PINs stay under the threshold; a correct PIN then resets the
        // counter (proving the relational ResetLockoutAsync ExecuteUpdate persists),
        // so a subsequent wrong PIN is only attempt #1 and does NOT lock (429).
        for (var i = 0; i < ValidationRules.MaxPinAttempts - 1; i++)
        {
            (await VerifyPinAsync(client, token, "000000")).Should().Be(HttpStatusCode.OK);
        }
        (await VerifyPinAsync(client, token, pin)).Should().Be(HttpStatusCode.OK, "correct PIN resets the window");
        (await VerifyPinAsync(client, token, "000000")).Should().Be(HttpStatusCode.OK,
            "after a reset the next wrong PIN is attempt #1, not a lockout");
    }

    private HttpClient CreateSqlClient()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        return _factory.CreateClient();
    }

    private static async Task<(string Token, string AzureTag)> RegisterAsync(HttpClient client)
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var azureTag = $"pinlock_{uniqueId}";
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = azureTag,
            Email = $"pinlock{uniqueId}@example.com",
            Password = "TestPass123!",
            FirstName = "Pin",
            LastName = "Lock"
        }, Json);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json);
        return (result!.Data!.Token.AccessToken, azureTag);
    }

    private async Task<int> ReadPinAccessFailedCountAsync(string azureTag)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return await db.Users.Where(u => u.AzureTag == azureTag)
            .Select(u => u.PinAccessFailedCount).SingleAsync();
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

    private static async Task<HttpStatusCode> VerifyPinAsync(HttpClient client, string token, string pin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/pin/verify")
        {
            Content = JsonContent.Create(new VerifyPinRequest { Pin = pin }, options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    public void Dispose()
    {
        _factory?.Dispose();
    }
}
