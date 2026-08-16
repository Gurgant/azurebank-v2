using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transaction;
using AzureBank.Shared.DTOs.Transfer;
using AzureBank.Shared.Enums;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Proves "accepted once" (RTS Art. 4(1)) holds under real contention on real SQL Server.
///
/// <para>
/// One authorisation, N concurrent transfers, each with its OWN idempotency key so nothing replays —
/// every one of them is a genuinely separate request that happens to present the same authorisation.
/// Exactly one may move money. A read-then-write consume would let several through here, which is
/// the shape ADR-0009 and the <c>PinAccessFailedCount</c> race in ADR-0010 both got wrong once
/// before; the single-statement <c>ExecuteUpdate</c> is what prevents it.
/// </para>
///
/// <para>
/// SQL-gated because InMemory has no row locks and cannot exhibit the race at all — a green run
/// there would prove nothing. Set <c>AZUREBANK_TEST_SQLSERVER</c> or these skip silently.
/// </para>
/// </summary>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class StepUpConcurrencySqlServerTests : IDisposable
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private const string Pin = "123456";
    private const decimal Amount = 10m;

    private readonly ITestOutputHelper _output;
    private CustomWebApplicationFactory? _factory;

    public StepUpConcurrencySqlServerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SqlServerFact]
    public async Task OneAuthorisation_SpentByEightConcurrentTransfers_MovesMoneyExactlyOnce()
    {
        const int concurrency = 8;

        var client = CreateSqlClient();
        var (token, accountId) = await RegisterFundedAsync(client, funding: 500m);
        var recipientTag = await RegisterRecipientAsync(client);
        var authorizationId = await MintAsync(client, token, accountId, recipientTag);

        var before = await BalanceOfAsync(client, token, accountId);

        // Every attempt carries a DIFFERENT idempotency key: this must be decided by the
        // authorisation's single-use guarantee, not by the idempotency layer replaying a result.
        var responses = await Task.WhenAll(
            Enumerable.Range(0, concurrency).Select(_ =>
                TransferAsync(client, token, accountId, recipientTag, authorizationId, Guid.NewGuid())));

        var statuses = responses.Select(r => r.StatusCode).ToArray();
        _output.WriteLine("concurrent statuses: " + string.Join(",", statuses.Select(s => (int)s)));

        statuses.Count(s => s == HttpStatusCode.Created).Should().Be(1,
            "exactly one concurrent transfer may spend a single authorisation");
        statuses.Count(s => s == HttpStatusCode.Unauthorized).Should().Be(concurrency - 1,
            "every loser must be refused, not silently dropped");

        var after = await BalanceOfAsync(client, token, accountId);
        (before - after).Should().Be(Amount,
            "the money must move exactly once, whatever the interleaving");

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var authorization = await db.StepUpAuthorizations.AsNoTracking()
            .SingleAsync(a => a.Id == authorizationId);
        authorization.Status.Should().Be(StepUpAuthorizationStatus.Consumed);
        authorization.ConsumedByTransactionId.Should().NotBeNull(
            "the winner must be identifiable in the evidence, not just counted");

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    /// <summary>
    /// ADR-0042 claims an authorisation is never spent by a transfer that rolled back. Until this
    /// test the claim rested on <c>ExecuteUpdateAsync</c> enlisting in the ambient transaction —
    /// true, but asserted nowhere.
    ///
    /// <para>
    /// Proven at the seam that carries the risk: consume inside an explicit transaction on a REAL
    /// relational context, roll it back, then read through a FRESH context. A fresh one matters —
    /// the consuming context's change tracker would happily report whatever it last wrote.
    /// SQL-gated because InMemory has no real transactions, so a green run there would prove the
    /// opposite of what it claims.
    /// </para>
    /// </summary>
    [SqlServerFact]
    public async Task ConsumeAsync_InsideARolledBackTransaction_LeavesTheAuthorisationSpendable()
    {
        var client = CreateSqlClient();
        var (token, accountId) = await RegisterFundedAsync(client, funding: 100m);
        var recipientTag = await RegisterRecipientAsync(client);
        var authorizationId = await MintAsync(client, token, accountId, recipientTag);

        Guid userId;
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
            userId = await db.StepUpAuthorizations.AsNoTracking()
                .Where(a => a.Id == authorizationId).Select(a => a.UserId).SingleAsync();
        }

        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
            var stepUp = scope.ServiceProvider.GetRequiredService<IStepUpAuthorizationService>();

            await using var transaction = await db.Database.BeginTransactionAsync();
            await stepUp.ConsumeAsync(userId, authorizationId, Guid.CreateVersion7());

            // The transfer this consumption belonged to fails after the row was marked spent.
            await transaction.RollbackAsync();
        }

        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
            var authorization = await db.StepUpAuthorizations.AsNoTracking()
                .SingleAsync(a => a.Id == authorizationId);

            authorization.Status.Should().Be(StepUpAuthorizationStatus.Pending,
                "a rolled-back transfer must leave the authorisation spendable — otherwise the user "
                + "cannot resend money that never moved");
            authorization.ConsumedAt.Should().BeNull();
            authorization.ConsumedByTransactionId.Should().BeNull();
        }

        // And it really is still spendable, not merely Pending on paper.
        var response = await TransferAsync(
            client, token, accountId, recipientTag, authorizationId, Guid.NewGuid());
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────

    private HttpClient CreateSqlClient()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        return _factory.CreateClient();
    }

    private static async Task<(string Token, Guid AccountId)> RegisterFundedAsync(
        HttpClient client, decimal funding)
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"stepupc_{unique}",
            Email = $"stepupc{unique}@example.com",
            Password = "TestPass123!",
            FirstName = "Step",
            LastName = "Up"
        }, Json);
        response.EnsureSuccessStatusCode();
        var registered = await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json);
        var token = registered!.Data!.Token.AccessToken;
        var accountId = registered.Data.Account.Id;

        await SendAsync(client, token, HttpMethod.Post, "/api/auth/pin",
            new SetPinRequest { Pin = Pin, Password = "TestPass123!" });

        using var deposit = new HttpRequestMessage(HttpMethod.Post, "/api/transactions/deposit")
        {
            Content = JsonContent.Create(
                new DepositRequest { AccountId = accountId, Amount = funding, Description = "funding" },
                options: Json)
        };
        deposit.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        deposit.Headers.Add(IdempotencyConstants.HeaderName, Guid.NewGuid().ToString());
        (await client.SendAsync(deposit)).EnsureSuccessStatusCode();

        return (token, accountId);
    }

    private static async Task<string> RegisterRecipientAsync(HttpClient client)
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var tag = $"stepupr_{unique}";
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = tag,
            Email = $"stepupr{unique}@example.com",
            Password = "TestPass123!",
            FirstName = "Payee",
            LastName = "User"
        }, Json);
        response.EnsureSuccessStatusCode();
        return tag;
    }

    private static async Task<Guid> MintAsync(
        HttpClient client, string token, Guid accountId, string recipientTag)
    {
        var response = await SendAsync(client, token, HttpMethod.Post, "/api/transfers/authorizations",
            new TransferAuthorizationRequest
            {
                FromAccountId = accountId,
                RecipientAzureTag = recipientTag,
                Amount = Amount,
                Pin = Pin
            });
        response.EnsureSuccessStatusCode();
        var body = await response.Content
            .ReadFromJsonAsync<ApiResponse<StepUpAuthorizationResponse>>(Json);
        return body!.Data!.AuthorizationId;
    }

    private static Task<HttpResponseMessage> TransferAsync(
        HttpClient client, string token, Guid accountId, string recipientTag,
        Guid authorizationId, Guid idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/transfers")
        {
            Content = JsonContent.Create(new TransferRequest
            {
                FromAccountId = accountId,
                RecipientAzureTag = recipientTag,
                Amount = Amount,
                Description = "concurrency proof",
                Pin = Pin
            }, options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(IdempotencyConstants.HeaderName, idempotencyKey.ToString());
        request.Headers.Add(StepUpConstants.HeaderName, authorizationId.ToString());
        return client.SendAsync(request);
    }

    private static async Task<decimal> BalanceOfAsync(HttpClient client, string token, Guid accountId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/accounts/{accountId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content
            .ReadFromJsonAsync<ApiResponse<AzureBank.Shared.DTOs.Account.AccountResponse>>(Json);
        return body!.Data!.Balance;
    }

    private static async Task<HttpResponseMessage> SendAsync<T>(
        HttpClient client, string token, HttpMethod method, string url, T payload)
    {
        using var request = new HttpRequestMessage(method, url)
        {
            Content = JsonContent.Create(payload, options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    public void Dispose()
    {
        _factory?.Dispose();
    }
}
