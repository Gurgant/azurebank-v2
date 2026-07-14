using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Account;
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
/// Regression proof for the EF transient-retry defect on transfers (R2):
/// with EnableRetryOnFailure active (production wiring), a transient fault
/// inside <c>strategy.ExecuteAsync</c> re-runs the transfer delegate against
/// the SAME DbContext. Before the fix, the leftover Added transactions +
/// already-mutated balances (Case A) or an already-committed transfer
/// (Case B) were re-applied → duplicate transactions and a double debit.
///
/// This is the coverage that was missing: NO existing test forced the retry
/// (the InMemory / plain-SQL factories register the context WITHOUT retry, so
/// the delegate only ever ran once). Here a one-shot interceptor injects the
/// transient exactly once, on a context configured WITH retry.
///
/// Gated by AZUREBANK_TEST_SQLSERVER (SqlServerFactAttribute); serialized with
/// the other SQL proofs (fresh-database migration race).
/// </summary>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class TransferTransientRetrySqlServerTests : IDisposable
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly ITestOutputHelper _output;
    private CustomWebApplicationFactory? _factory;

    public TransferTransientRetrySqlServerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // ---- Case A: transient at the transfer's first SaveChanges ----

    [SqlServerFact]
    public async Task ExternalTransfer_TransientAtFirstSaveChanges_ExecutesExactlyOnce()
    {
        var fault = new TransferTransientFault(TransferFaultMode.AtFirstSaveChanges);
        var client = CreateRetryingClient(fault);

        var sender = await RegisterAsync(client, "sxa");
        var recipient = await RegisterAsync(client, "rxa");
        await DepositAsync(client, sender, 1000m);

        fault.Arm();
        var response = await TransferAsync(client, sender, new TransferRequest
        {
            FromAccountId = sender.AccountId,
            RecipientAzureTag = recipient.AzureTag,
            Amount = 100m,
            Description = "Transient-retry proof (external, Case A)"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created,
            "the injected transient must be absorbed by the execution strategy, not surfaced");
        fault.Fired.Should().BeTrue("the transient must actually have been injected (else the test proves nothing)");

        await AssertSingleTransferAsync(client, sender, recipient, funding: 1000m, amount: 100m);
        (await GetIdempotencyStatusAsync(sender.UserId, "POST api/transfers"))
            .Should().Be(IdempotencyStatus.Completed, "the successful transfer's record is stored for replay");
    }

    [SqlServerFact]
    public async Task InternalTransfer_TransientAtFirstSaveChanges_ExecutesExactlyOnce()
    {
        var fault = new TransferTransientFault(TransferFaultMode.AtFirstSaveChanges);
        var client = CreateRetryingClient(fault);

        var user = await RegisterAsync(client, "sia");
        var savingsId = await CreateSecondAccountAsync(client, user);
        await DepositAsync(client, user, 1000m);

        fault.Arm();
        var response = await InternalTransferAsync(client, user, new InternalTransferRequest
        {
            FromAccountId = user.AccountId,
            ToAccountId = savingsId,
            Amount = 300m,
            Description = "Transient-retry proof (internal, Case A)"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        fault.Fired.Should().BeTrue("the transient must actually have been injected");

        (await GetBalanceAsync(client, user, user.AccountId)).Should().Be(700m, "debited exactly once");
        (await GetBalanceAsync(client, user, savingsId)).Should().Be(300m, "credited exactly once");
        (await CountTransactionsAsync(client, user, user.AccountId)).Should().Be(2, "funding + ONE transfer-out");
        (await CountTransactionsAsync(client, user, savingsId)).Should().Be(1, "ONE transfer-in");
        (await GetIdempotencyStatusAsync(user.UserId, "POST api/transfers/internal"))
            .Should().Be(IdempotencyStatus.Completed);
    }

    // ---- Case B: transient right AFTER the commit (commit ack lost) ----

    [SqlServerFact]
    public async Task ExternalTransfer_TransientAfterCommit_DoesNotDoubleExecute()
    {
        var fault = new TransferTransientFault(TransferFaultMode.AfterCommit);
        var client = CreateRetryingClient(fault);

        var sender = await RegisterAsync(client, "sxb");
        var recipient = await RegisterAsync(client, "rxb");
        await DepositAsync(client, sender, 1000m);

        fault.Arm();
        var response = await TransferAsync(client, sender, new TransferRequest
        {
            FromAccountId = sender.AccountId,
            RecipientAzureTag = recipient.AzureTag,
            Amount = 100m,
            Description = "Transient-retry proof (external, Case B)"
        });

        // The transfer committed on attempt 1; the retry must recognise the
        // already-committed record and refuse to re-execute.
        await AssertResultUnknownAsync(response);
        fault.Fired.Should().BeTrue("the transient must actually have been injected");

        await AssertSingleTransferAsync(client, sender, recipient, funding: 1000m, amount: 100m);
        (await GetIdempotencyStatusAsync(sender.UserId, "POST api/transfers"))
            .Should().Be(IdempotencyStatus.Executed,
                "committed but the response was lost: never Completed, never re-executed");
    }

    [SqlServerFact]
    public async Task InternalTransfer_TransientAfterCommit_DoesNotDoubleExecute()
    {
        var fault = new TransferTransientFault(TransferFaultMode.AfterCommit);
        var client = CreateRetryingClient(fault);

        var user = await RegisterAsync(client, "sib");
        var savingsId = await CreateSecondAccountAsync(client, user);
        await DepositAsync(client, user, 1000m);

        fault.Arm();
        var response = await InternalTransferAsync(client, user, new InternalTransferRequest
        {
            FromAccountId = user.AccountId,
            ToAccountId = savingsId,
            Amount = 300m,
            Description = "Transient-retry proof (internal, Case B)"
        });

        await AssertResultUnknownAsync(response);
        fault.Fired.Should().BeTrue("the transient must actually have been injected");

        (await GetBalanceAsync(client, user, user.AccountId)).Should().Be(700m, "debited exactly once");
        (await GetBalanceAsync(client, user, savingsId)).Should().Be(300m, "credited exactly once");
        (await CountTransactionsAsync(client, user, user.AccountId)).Should().Be(2, "funding + ONE transfer-out");
        (await CountTransactionsAsync(client, user, savingsId)).Should().Be(1, "ONE transfer-in");
        (await GetIdempotencyStatusAsync(user.UserId, "POST api/transfers/internal"))
            .Should().Be(IdempotencyStatus.Executed);
    }

    // ---- infrastructure ----

    private HttpClient CreateRetryingClient(TransferTransientFault fault)
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        _factory.EnableSqlRetryOnFailure();
        _factory.AddInterceptor(new TransferCommandFaultInterceptor(fault));
        _factory.AddInterceptor(new TransferCommitFaultInterceptor(fault));
        return _factory.CreateClient();
    }

    private sealed record TestUser(string Token, Guid UserId, Guid AccountId, string AzureTag);

    private static async Task<TestUser> RegisterAsync(HttpClient client, string prefix)
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var azureTag = $"{prefix}_{uniqueId}";
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = azureTag,
            Email = $"{prefix}{uniqueId}@example.com",
            Password = "TestPass123!",
            FirstName = "Retry",
            LastName = "Prover"
        }, Json);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json);
        return new TestUser(
            result!.Data!.Token.AccessToken, result.Data.User.Id, result.Data.Account.Id, azureTag);
    }

    private static async Task<Guid> CreateSecondAccountAsync(HttpClient client, TestUser user)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/accounts")
        {
            Content = JsonContent.Create(new CreateAccountRequest
            {
                Name = "Savings",
                Type = AccountType.Savings
            }, options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<AccountResponse>>(Json);
        return result!.Data!.Id;
    }

    private static async Task DepositAsync(HttpClient client, TestUser user, decimal amount)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/transactions/deposit")
        {
            Content = JsonContent.Create(new DepositRequest
            {
                AccountId = user.AccountId,
                Amount = amount,
                Description = "Funding"
            }, options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
        request.Headers.Add(IdempotencyConstants.HeaderName, Guid.NewGuid().ToString());
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private static Task<HttpResponseMessage> TransferAsync(
        HttpClient client, TestUser sender, TransferRequest body) =>
        PostMonetaryAsync(client, sender.Token, "/api/transfers", body);

    private static Task<HttpResponseMessage> InternalTransferAsync(
        HttpClient client, TestUser user, InternalTransferRequest body) =>
        PostMonetaryAsync(client, user.Token, "/api/transfers/internal", body);

    private static async Task<HttpResponseMessage> PostMonetaryAsync<T>(
        HttpClient client, string token, string url, T body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body, options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(IdempotencyConstants.HeaderName, Guid.NewGuid().ToString());
        return await client.SendAsync(request);
    }

    private async Task AssertSingleTransferAsync(
        HttpClient client, TestUser sender, TestUser recipient, decimal funding, decimal amount)
    {
        (await GetBalanceAsync(client, sender, sender.AccountId))
            .Should().Be(funding - amount, "the sender must be debited EXACTLY once");
        (await GetBalanceAsync(client, recipient, recipient.AccountId))
            .Should().Be(amount, "the recipient must be credited EXACTLY once");
        (await CountTransactionsAsync(client, sender, sender.AccountId))
            .Should().Be(2, "funding deposit + EXACTLY ONE outgoing transfer");
        (await CountTransactionsAsync(client, recipient, recipient.AccountId))
            .Should().Be(1, "EXACTLY ONE incoming transfer");
    }

    private static async Task AssertResultUnknownAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "an already-committed transfer must not re-execute; the retry yields RESULT_UNKNOWN");
        var problem = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        problem.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.IdempotencyResultUnknown);
    }

    private static async Task<decimal> GetBalanceAsync(HttpClient client, TestUser user, Guid accountId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/accounts/{accountId}/balance");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<BalanceResponse>>(Json);
        return result!.Data!.Balance;
    }

    private static async Task<int> CountTransactionsAsync(HttpClient client, TestUser user, Guid accountId)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/transactions?accountId={accountId}&pageSize=50");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<PaginatedResponse<TransactionResponse>>(Json);
        return result!.Pagination.TotalItems;
    }

    private async Task<IdempotencyStatus> GetIdempotencyStatusAsync(Guid userId, string endpoint)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var record = await db.IdempotencyRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.UserId == userId && r.Endpoint == endpoint);
        record.Should().NotBeNull($"the transfer must have claimed an idempotency record for {endpoint}");
        return record!.Status;
    }

    public void Dispose()
    {
        _factory?.Dispose();
    }
}
