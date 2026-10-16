using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
using FluentAssertions.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The <c>Idempotency-Key</c> of a money request on the ledger rows it wrote, on SQL Server and
/// through the HTTP API: on the caller's own rows and never on the payee's, listed by
/// <c>GET /api/transactions?IdempotencyKey=</c>, and written once however often the key is sent
/// (ADR-0009).
/// </summary>
/// <remarks>
/// One user makes one of each money request, each under a key of its own: a deposit, a
/// withdrawal, a move to their own second account and a transfer to another user. The rows are
/// then read from the table, where the column is, and listed through the API, where the client
/// asks. A request retried inside the API is in <c>TransferTransientRetrySqlServerTests</c>.
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class IdempotencyKeyOnTheLedgerSqlServerTests : IDisposable
{
    private const string Pin = "123456";
    private const string Password = "TestPass123!";

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly ITestOutputHelper _output;
    private CustomWebApplicationFactory? _factory;

    public IdempotencyKeyOnTheLedgerSqlServerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SqlServerFact]
    public async Task EachMoneyRequest_PutsItsKeyOnTheCallersRows_AndNeverOnThePayees()
    {
        var client = CreateSqlClient();
        var made = await MakeOneOfEachAsync(client);

        var rows = await ReadRowsAsync(made.Sender.AccountId, made.SavingsId, made.Payee.AccountId);
        _output.WriteLine(string.Join(Environment.NewLine, rows));

        // Every row the four requests wrote, and the key each one carries. The external transfer's
        // two rows are the same pair of a debit and a credit as the internal one's; only the
        // payee's carries no key.
        rows.Select(r => (r.AccountId, r.Type, r.IdempotencyKey)).Should().BeEquivalentTo(new[]
        {
            (made.Sender.AccountId, TransactionType.Deposit, (Guid?)made.DepositKey),
            (made.Sender.AccountId, TransactionType.Withdrawal, (Guid?)made.WithdrawalKey),
            (made.Sender.AccountId, TransactionType.TransferOut, (Guid?)made.InternalKey),
            (made.SavingsId, TransactionType.TransferIn, (Guid?)made.InternalKey),
            (made.Sender.AccountId, TransactionType.TransferOut, (Guid?)made.ExternalKey),
            (made.Payee.AccountId, TransactionType.TransferIn, (Guid?)null),
        });
    }

    [SqlServerFact]
    public async Task TheListingByKey_FindsTheRowsOfThatRequest_AndAKeyNoRowCarriesFindsNone()
    {
        var client = CreateSqlClient();
        var made = await MakeOneOfEachAsync(client);
        var rows = await ReadRowsAsync(made.Sender.AccountId, made.SavingsId, made.Payee.AccountId);

        var byDeposit = await ListAsync(client, made.Sender.Token, made.DepositKey);
        var byWithdrawal = await ListAsync(client, made.Sender.Token, made.WithdrawalKey);
        var byInternal = await ListAsync(client, made.Sender.Token, made.InternalKey);
        var byExternal = await ListAsync(client, made.Sender.Token, made.ExternalKey);
        var byNone = await ListAsync(client, made.Sender.Token, Guid.NewGuid());

        using var assertions = new AssertionScope();
        byDeposit.Data.Select(t => t.Id).Should().Equal(made.DepositId);
        byWithdrawal.Data.Select(t => t.Id).Should().Equal(made.WithdrawalId);
        byInternal.Data.Select(t => t.Id).Should().BeEquivalentTo(
            rows.Where(r => r.IdempotencyKey == made.InternalKey).Select(r => r.Id));
        byInternal.Data.Select(t => t.Id).Should().Contain(made.InternalTransferId).And.HaveCount(2);
        byExternal.Data.Select(t => t.TransactionNumber).Should().Equal(made.ExternalNumber);
        byExternal.Data.Single().Type.Should().Be(TransactionType.TransferOut);

        foreach (var (page, rowsOfTheKey) in new[] { (byDeposit, 1), (byWithdrawal, 1), (byInternal, 2), (byExternal, 1) })
        {
            page.Pagination.TotalItems.Should().Be(rowsOfTheKey);
            page.Pagination.TotalPages.Should().Be(1);
        }

        byNone.Data.Should().BeEmpty();
        byNone.Pagination.TotalItems.Should().Be(0);
        byNone.Pagination.TotalPages.Should().Be(0);
    }

    [SqlServerFact]
    public async Task ThePayee_ListingByTheSendersKeys_GetsAnEmptyPage()
    {
        var client = CreateSqlClient();
        var made = await MakeOneOfEachAsync(client);

        var all = await ListAsync(client, made.Payee.Token, key: null);
        var byTheTransfersKey = await ListAsync(client, made.Payee.Token, made.ExternalKey);
        var byTheDepositsKey = await ListAsync(client, made.Payee.Token, made.DepositKey);

        using var assertions = new AssertionScope();
        // CONTROL: the payee's row is there, and the payee lists it.
        all.Data.Should().ContainSingle().Which.Type.Should().Be(TransactionType.TransferIn);
        byTheTransfersKey.Data.Should().BeEmpty("the payee's row does not carry the sender's key");
        byTheTransfersKey.Pagination.TotalItems.Should().Be(0);
        byTheDepositsKey.Data.Should().BeEmpty("a key of another user is a key no row of the caller carries");
        byTheDepositsKey.Pagination.TotalItems.Should().Be(0);
    }

    [SqlServerFact]
    public async Task TheSameKeySentAgain_IsReplayed_AndWritesNoSecondRow()
    {
        var client = CreateSqlClient();
        var user = await RegisterAsync(client, "ikrep", withPin: false);
        var key = Guid.NewGuid();

        var first = await DepositAsync(client, user, 40m, key);
        var again = await DepositAsync(client, user, 40m, key);
        var rowsOfTheKey = await CountRowsCarryingAsync(key);

        _output.WriteLine(
            $"first: {(int)first.StatusCode}, again: {(int)again.StatusCode}, "
            + $"replayed: {again.Headers.Contains(IdempotencyConstants.ReplayedHeaderName)}, rows carrying the key: {rowsOfTheKey}");

        using var assertions = new AssertionScope();
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        again.StatusCode.Should().Be(HttpStatusCode.Created);
        again.Headers.Contains(IdempotencyConstants.ReplayedHeaderName).Should().BeTrue(
            "the same key and the same bytes are answered from the stored response");
        rowsOfTheKey.Should().Be(1, "a replay writes nothing");
        (await ListAsync(client, user.Token, key)).Pagination.TotalItems.Should().Be(1);
    }

    // ---- infrastructure ----

    private sealed record TestUser(string Token, Guid UserId, Guid AccountId, string AzureTag);

    /// <summary>The four requests, the keys they were sent with, and what their answers name.</summary>
    private sealed record Made(
        TestUser Sender, Guid SavingsId, TestUser Payee,
        Guid DepositKey, Guid DepositId,
        Guid WithdrawalKey, Guid WithdrawalId,
        Guid InternalKey, Guid InternalTransferId,
        Guid ExternalKey, string ExternalNumber);

    /// <summary>One row of the table, with the column the API does not return.</summary>
    private sealed record Row(Guid Id, Guid AccountId, TransactionType Type, Guid? IdempotencyKey, string TransactionNumber);

    private HttpClient CreateSqlClient()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        return _factory.CreateClient();
    }

    private static async Task<Made> MakeOneOfEachAsync(HttpClient client)
    {
        var sender = await RegisterAsync(client, "iksnd", withPin: true);
        var payee = await RegisterAsync(client, "ikpay", withPin: false);
        var savingsId = await CreateSavingsAsync(client, sender);

        var depositKey = Guid.NewGuid();
        var deposited = await DepositAsync(client, sender, 500m, depositKey);
        deposited.StatusCode.Should().Be(HttpStatusCode.Created);
        var depositId = (await deposited.Content.ReadFromJsonAsync<ApiResponse<DepositResponse>>(Json))!
            .Data!.Transaction.Id;

        var withdrawalKey = Guid.NewGuid();
        var withdrawal = new WithdrawRequest { AccountId = sender.AccountId, Amount = 50m, Description = "Out by key" };
        var withdrawn = await PostMonetaryAsync(
            client, sender.Token, "/api/transactions/withdraw", withdrawal, withdrawalKey,
            await AuthoriseAsync(client, sender.Token, "/api/transactions/withdraw/authorizations",
                new WithdrawalAuthorizationRequest { AccountId = sender.AccountId, Amount = 50m, Pin = Pin }));
        withdrawn.StatusCode.Should().Be(HttpStatusCode.Created, await withdrawn.Content.ReadAsStringAsync());
        var withdrawalId = (await withdrawn.Content.ReadFromJsonAsync<ApiResponse<WithdrawResponse>>(Json))!
            .Data!.Transaction.Id;

        var internalKey = Guid.NewGuid();
        var move = new InternalTransferRequest
        {
            FromAccountId = sender.AccountId,
            ToAccountId = savingsId,
            Amount = 100m,
            Description = "Moved by key"
        };
        var moved = await PostMonetaryAsync(
            client, sender.Token, "/api/transfers/internal", move, internalKey,
            await AuthoriseAsync(client, sender.Token, "/api/transfers/internal/authorizations",
                new InternalTransferAuthorizationRequest
                {
                    FromAccountId = move.FromAccountId,
                    ToAccountId = move.ToAccountId,
                    Amount = move.Amount,
                    Pin = Pin
                }));
        moved.StatusCode.Should().Be(HttpStatusCode.Created, await moved.Content.ReadAsStringAsync());
        var internalTransferId = (await moved.Content.ReadFromJsonAsync<ApiResponse<InternalTransferResponse>>(Json))!
            .Data!.TransferId;

        var externalKey = Guid.NewGuid();
        var payment = new TransferRequest
        {
            FromAccountId = sender.AccountId,
            RecipientAzureTag = payee.AzureTag,
            Amount = 30m,
            Description = "Paid by key"
        };
        var paid = await PostMonetaryAsync(
            client, sender.Token, "/api/transfers", payment, externalKey,
            await AuthoriseAsync(client, sender.Token, "/api/transfers/authorizations",
                new TransferAuthorizationRequest
                {
                    FromAccountId = payment.FromAccountId,
                    RecipientAzureTag = payment.RecipientAzureTag,
                    Amount = payment.Amount,
                    Pin = Pin
                }));
        paid.StatusCode.Should().Be(HttpStatusCode.Created, await paid.Content.ReadAsStringAsync());
        var externalNumber = (await paid.Content.ReadFromJsonAsync<ApiResponse<TransferResponse>>(Json))!
            .Data!.TransactionNumber;

        return new Made(
            sender, savingsId, payee,
            depositKey, depositId,
            withdrawalKey, withdrawalId,
            internalKey, internalTransferId,
            externalKey, externalNumber);
    }

    /// <summary>Every row of the accounts named, read from the table in a scope of its own.</summary>
    private async Task<List<Row>> ReadRowsAsync(params Guid[] accountIds)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return await db.Transactions.AsNoTracking()
            .Where(t => accountIds.Contains(t.AccountId))
            .Select(t => new Row(t.Id, t.AccountId, t.Type, t.IdempotencyKey, t.TransactionNumber))
            .ToListAsync();
    }

    /// <summary>How many rows of the whole table carry the key.</summary>
    private async Task<int> CountRowsCarryingAsync(Guid key)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return await db.Transactions.AsNoTracking().CountAsync(t => t.IdempotencyKey == key);
    }

    private static async Task<PaginatedResponse<TransactionResponse>> ListAsync(
        HttpClient client, string token, Guid? key)
    {
        var url = key is { } k ? $"/api/transactions?PageSize=100&IdempotencyKey={k}" : "/api/transactions?PageSize=100";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PaginatedResponse<TransactionResponse>>(Json))!;
    }

    private static async Task<TestUser> RegisterAsync(HttpClient client, string prefix, bool withPin)
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var azureTag = $"{prefix}_{unique}";
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = azureTag,
            Email = $"{prefix}{unique}@example.com",
            Password = Password,
            FirstName = "Keyed",
            LastName = "Ledger"
        }, Json);
        response.EnsureSuccessStatusCode();
        var registered = await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json);
        var user = new TestUser(
            registered!.Data!.Token.AccessToken, registered.Data.User.Id, registered.Data.Account.Id, azureTag);

        if (withPin)
        {
            using var setPin = new HttpRequestMessage(HttpMethod.Post, "/api/auth/pin")
            {
                Content = JsonContent.Create(new SetPinRequest { Pin = Pin, Password = Password }, options: Json)
            };
            setPin.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
            (await client.SendAsync(setPin)).EnsureSuccessStatusCode();
        }

        return user;
    }

    private static async Task<Guid> CreateSavingsAsync(HttpClient client, TestUser user)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/accounts")
        {
            Content = JsonContent.Create(
                new CreateAccountRequest { Name = "Savings", Type = AccountType.Savings }, options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ApiResponse<AccountResponse>>(Json))!.Data!.Id;
    }

    private static Task<HttpResponseMessage> DepositAsync(HttpClient client, TestUser user, decimal amount, Guid key) =>
        PostMonetaryAsync(
            client, user.Token, "/api/transactions/deposit",
            new DepositRequest { AccountId = user.AccountId, Amount = amount, Description = "In by key" },
            key, stepUpAuthorizationId: null);

    private static async Task<Guid> AuthoriseAsync<T>(HttpClient client, string token, string url, T body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body, options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ApiResponse<StepUpAuthorizationResponse>>(Json))!
            .Data!.AuthorizationId;
    }

    private static async Task<HttpResponseMessage> PostMonetaryAsync<T>(
        HttpClient client, string token, string url, T body, Guid key, Guid? stepUpAuthorizationId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body, options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(IdempotencyConstants.HeaderName, key.ToString());
        if (stepUpAuthorizationId is { } authorizationId)
        {
            request.Headers.Add(StepUpConstants.HeaderName, authorizationId.ToString());
        }

        return await client.SendAsync(request);
    }

    public void Dispose()
    {
        _factory?.Dispose();
    }
}
