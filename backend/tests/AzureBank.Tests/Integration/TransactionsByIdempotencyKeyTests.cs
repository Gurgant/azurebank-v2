using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transaction;
using AzureBank.Shared.DTOs.Transfer;
using AzureBank.Shared.Enums;
using AzureBank.Tests.Fixtures;
using FluentAssertions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// <c>GET /api/transactions?IdempotencyKey=</c>: the rows a money request wrote, found by the key it
/// was sent with, on the caller's own accounts and on no one else's (ADR-0009).
/// </summary>
/// <remarks>
/// On the in-memory host, through the HTTP API. The column, its index, the payee's row read from
/// the table and a request retried inside the API are on SQL Server, in
/// <c>IdempotencyKeyOnTheLedgerSqlServerTests</c> and <c>TransferTransientRetrySqlServerTests</c>.
/// </remarks>
public class TransactionsByIdempotencyKeyTests : IntegrationTestBase
{
    public TransactionsByIdempotencyKeyTests(CustomWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task ADeposit_IsListedByItsKey_AsExactlyThatRow()
    {
        var (token, _, accountId) = await RegisterTestUserAsync();
        // Another deposit first, under a key of its own: a filter left unapplied lists both.
        await DepositAsync(token, accountId, 10m);

        var key = Guid.NewGuid();
        var deposited = await PostMonetaryAsync(
            "/api/transactions/deposit",
            new DepositRequest { AccountId = accountId, Amount = 25m, Description = "Found by its key" },
            key);
        deposited.StatusCode.Should().Be(HttpStatusCode.Created);
        var row = (await deposited.Content.ReadFromJsonAsync<ApiResponse<DepositResponse>>(JsonOptions))!
            .Data!.Transaction;

        var page = await ListAsync(token, key);

        page.Data.Select(t => t.Id).Should().Equal(row.Id);
        page.Data.Single().Amount.Should().Be(25m);
        page.Pagination.TotalItems.Should().Be(1);
        page.Pagination.TotalPages.Should().Be(1);
        // The row names the key that wrote it, in the listing and in the deposit's own answer.
        page.Data.Single().IdempotencyKey.Should().Be(key);
        row.IdempotencyKey.Should().Be(key);
        // The other deposit's row names its own key, never this one.
        (await ListAsync(token, key: null)).Data.Where(t => t.Id != row.Id)
            .Should().ContainSingle().Which.IdempotencyKey.Should().NotBeNull().And.NotBe(key);
    }

    /// <summary>
    /// The binder reads the key as a GUID, so every spelling of it that the binder takes is the
    /// same key. The mock is held to the same spellings in <c>src/mocks/ledger.test.ts</c>.
    /// </summary>
    [Theory]
    [InlineData("D-upper")]
    [InlineData("N")]
    [InlineData("B")]
    [InlineData("P")]
    [InlineData("X")]
    [InlineData("X-short")]
    [InlineData("X-plus")]
    public async Task AKey_IsFoundUnderEverySpellingTheBinderTakes(string spelling)
    {
        var (token, _, accountId) = await RegisterTestUserAsync();
        // Every component starts with a zero, so that the short X spelling is not the full one.
        var digits = Guid.NewGuid().ToString("N").ToCharArray();
        foreach (var at in new[] { 0, 8, 12, 16, 18, 20, 22, 24, 26, 28, 30 })
        {
            digits[at] = '0';
        }
        var key = Guid.ParseExact(new string(digits), "N");
        SetAuthHeader(token);
        (await PostMonetaryAsync(
            "/api/transactions/deposit",
            new DepositRequest { AccountId = accountId, Amount = 25m },
            key)).StatusCode.Should().Be(HttpStatusCode.Created);

        var sent = spelling switch
        {
            "D-upper" => key.ToString("D").ToUpperInvariant(),
            // Every component without its leading zeros: the binder pads them back.
            "X-short" => Regex.Replace(key.ToString("X"), "0x0+(?=[0-9a-f])", "0x"),
            // A + and a second 0x after each 0x: the parser skips both.
            "X-plus" => key.ToString("X").Replace("0x", "0x+0x"),
            _ => key.ToString(spelling),
        };
        var response = await Client.GetAsync($"/api/transactions?IdempotencyKey={Uri.EscapeDataString(sent)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK, sent);
        (await response.Content.ReadFromJsonAsync<PaginatedResponse<TransactionResponse>>(JsonOptions))!
            .Pagination.TotalItems.Should().Be(1, sent);
    }

    [Fact]
    public async Task AKeyNoRowCarries_ListsAnEmptyPage()
    {
        var (token, _, accountId) = await RegisterTestUserAsync();
        await DepositAsync(token, accountId, 10m);

        var page = await ListAsync(token, Guid.NewGuid());

        page.Data.Should().BeEmpty();
        page.Pagination.TotalItems.Should().Be(0);
        page.Pagination.TotalPages.Should().Be(0);
    }

    [Fact]
    public async Task AMoveBetweenTheCallersOwnAccounts_IsListedByItsKey_AsBothItsRows()
    {
        var (token, _, primaryAccountId) = await RegisterTestUserAsync();
        await SetPinAsync(token);
        var savingsId = await CreateSavingsAsync(token);
        await DepositAsync(token, primaryAccountId, 1000m);

        var key = Guid.NewGuid();
        SetAuthHeader(token);
        var moved = await PostMonetaryAsync(
            "/api/transfers/internal",
            new InternalTransferRequest
            {
                FromAccountId = primaryAccountId,
                ToAccountId = savingsId,
                Amount = 300m,
                Description = "Both rows by one key"
            },
            key,
            await AuthoriseInternalTransferAsync(primaryAccountId, savingsId, 300m));
        moved.StatusCode.Should().Be(HttpStatusCode.Created);
        var transferId = (await moved.Content
            .ReadFromJsonAsync<ApiResponse<InternalTransferResponse>>(JsonOptions))!.Data!.TransferId;

        var page = await ListAsync(token, key);

        page.Data.Select(t => t.Type).Should().BeEquivalentTo(
            new[] { TransactionType.TransferOut, TransactionType.TransferIn });
        page.Data.Should().ContainSingle(t => t.Type == TransactionType.TransferOut)
            .Which.Id.Should().Be(transferId, "the outgoing row is the one the answer names");
        page.Data.Should().OnlyContain(t => t.IdempotencyKey == key, "both rows name the key that wrote them");
        page.Pagination.TotalItems.Should().Be(2);

        // Each row on its own account: the listing by key, narrowed to one account, holds that
        // account's row and not the other's.
        (await ListAsync(token, key, savingsId)).Data.Should().ContainSingle()
            .Which.Type.Should().Be(TransactionType.TransferIn);
    }

    [Fact]
    public async Task ATransferToAnotherUser_IsListedByItsKey_ForTheSenderAlone()
    {
        var (senderToken, _, senderAccountId) = await RegisterTestUserAsync();
        await SetPinAsync(senderToken);
        var payee = await RegisterPayeeAsync();
        await DepositAsync(senderToken, senderAccountId, 1000m);

        var key = Guid.NewGuid();
        SetAuthHeader(senderToken);
        var sent = await PostMonetaryAsync(
            "/api/transfers",
            new TransferRequest
            {
                FromAccountId = senderAccountId,
                RecipientAzureTag = payee.AzureTag,
                Amount = 100m,
                Description = "Paid by one key"
            },
            key,
            await AuthoriseTransferAsync(senderAccountId, payee.AzureTag, 100m));
        sent.StatusCode.Should().Be(HttpStatusCode.Created);
        var number = (await sent.Content.ReadFromJsonAsync<ApiResponse<TransferResponse>>(JsonOptions))!
            .Data!.TransactionNumber;

        var senders = await ListAsync(senderToken, key);
        senders.Data.Should().ContainSingle().Which.TransactionNumber.Should().Be(number);
        senders.Data.Single().IdempotencyKey.Should().Be(key);

        // CONTROL: the payee's row is there, and the payee can list it; it names no key.
        var payeesRow = (await ListAsync(payee.Token, key: null)).Data.Should().ContainSingle().Subject;
        payeesRow.Type.Should().Be(TransactionType.TransferIn);
        payeesRow.IdempotencyKey.Should().BeNull("the payee is never handed the sender's key");

        var payees = await ListAsync(payee.Token, key);
        payees.Data.Should().BeEmpty("the payee's row does not carry the sender's key");
        payees.Pagination.TotalItems.Should().Be(0);
    }

    [Theory]
    [InlineData("not-a-key")]
    [InlineData("")]
    [InlineData(" ")]
    public async Task AKeyThatIsNotAUuid_IsRefusedByTheBinder(string sent)
    {
        var (token, _, _) = await RegisterTestUserAsync();
        SetAuthHeader(token);

        var response = await Client.GetAsync(
            $"/api/transactions?IdempotencyKey={Uri.EscapeDataString(sent)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("errors").GetProperty("IdempotencyKey").EnumerateArray()
            .Select(e => e.GetString()).Should().Equal($"The value '{sent}' is not valid for IdempotencyKey.");
    }

    /// <summary>The caller's listing, by key when one is given, and to one account when one is given.</summary>
    private async Task<PaginatedResponse<TransactionResponse>> ListAsync(
        string token, Guid? key, Guid? accountId = null)
    {
        SetAuthHeader(token);
        var query = "/api/transactions?PageSize=100";
        if (key is { } k)
        {
            query += $"&IdempotencyKey={k}";
        }

        if (accountId is { } a)
        {
            query += $"&AccountId={a}";
        }

        var response = await Client.GetAsync(query);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PaginatedResponse<TransactionResponse>>(JsonOptions))!;
    }

    private async Task<Guid> CreateSavingsAsync(string token)
    {
        SetAuthHeader(token);
        var created = await Client.PostAsJsonAsync("/api/accounts",
            new CreateAccountRequest { Name = "Savings", Type = AccountType.Savings }, JsonOptions);
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<ApiResponse<AccountResponse>>(JsonOptions))!.Data!.Id;
    }

    /// <summary>Registers another user, with the handle a transfer names.</summary>
    private async Task<(string Token, string AzureTag)> RegisterPayeeAsync()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var azureTag = $"payee_{unique}";
        var response = await Client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = azureTag,
            Email = $"payee{unique}@example.com",
            Password = TestUserPassword,
            FirstName = "Payee",
            LastName = "User"
        }, JsonOptions);
        response.EnsureSuccessStatusCode();
        var registered = await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(JsonOptions);
        return (registered!.Data!.Token.AccessToken, azureTag);
    }
}
