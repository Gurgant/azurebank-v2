using System.Net;
using System.Net.Http.Json;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transfer;
using AzureBank.Shared.Enums;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBank.Tests.Integration;

/// <summary>
/// What a transfer writes to the LEDGER, as opposed to what it returns.
///
/// <para>
/// These replace six tests that never existed. <c>TransferServiceTests</c> carried six
/// <c>[Fact(Skip = "Requires SQL Server - InMemory provider transaction behavior may vary")]</c>
/// entries — <c>UpdatesBothBalances</c>, <c>CreatesTwoLinkedTransactions</c> and
/// <c>ReturnsCorrectResponse</c>, once per transfer kind — whose bodies were a comment saying the
/// test should be written as an integration test. Nothing was ever written, and a skipped empty
/// method is indistinguishable in the runner from a skipped real one.
/// </para>
/// <para>
/// The premise was also wrong. Transfers run fine on the InMemory host — <c>TransferEndpointTests</c>
/// has been exercising both endpoints through it all along — so this needs no SQL Server gate and
/// runs on every developer machine and in every CI job, which the promised integration test would
/// not have.
/// </para>
/// <para>
/// <b>Balances are deliberately NOT re-asserted here.</b> They are already proven twice, from
/// re-read persisted state: <c>IdempotencyConcurrencyTests</c> on InMemory, and
/// <c>TransferTransientRetrySqlServerTests.AssertSingleTransferAsync</c> on real SQL Server under
/// injected transient failure. What no test anywhere asserted is the SHAPE of the pair — that two
/// rows exist, that they point at each other, that they carry opposite types, and that each one's
/// <c>BalanceBefore</c>/<c>BalanceAfter</c> bracket the amount. A ledger that moves the right money
/// through the wrong rows reconciles to the right total and is still wrong.
/// </para>
/// </summary>
public class TransferLedgerTests : IntegrationTestBase
{
    /// <summary>The PIN these tests enrol and then spend at the mint endpoint (ADR-0042).</summary>
    private const string TestPin = "123456";

    public TransferLedgerTests(CustomWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task ExternalTransfer_WritesAMutuallyLinkedPairWithOppositeTypes()
    {
        var (senderToken, _, senderAccountId) = await RegisterTestUserAsync();
        // ADR-0042: the PIN is spent at the mint endpoint, so an un-enrolled user is refused
        // 422 PIN_REQUIRED there and never obtains the authorisation this transfer requires.
        await SetPinAsync(senderToken);
        var recipient = await RegisterRecipientAsync();
        await DepositAsync(senderToken, senderAccountId, 1000m);

        SetAuthHeader(senderToken);
        var response = await PostMonetaryAsync("/api/transfers", new TransferRequest
        {
            FromAccountId = senderAccountId,
            RecipientAzureTag = recipient.AzureTag,
            Amount = 100m,
            Description = "Ledger shape"
        }, stepUpAuthorizationId: await AuthoriseTransferAsync(senderAccountId, recipient.AzureTag, 100m));
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

        var outgoing = await db.Transactions.SingleAsync(
            t => t.AccountId == senderAccountId && t.Type == TransactionType.TransferOut);
        var incoming = await db.Transactions.SingleAsync(
            t => t.AccountId == recipient.AccountId && t.Type == TransactionType.TransferIn);

        // The link is what makes the pair a pair. Asserted in BOTH directions: a one-way link reads
        // as correct from the sender's side and orphans the recipient's row.
        outgoing.RelatedTransactionId.Should().Be(incoming.Id);
        incoming.RelatedTransactionId.Should().Be(outgoing.Id);

        // Same money, opposite direction, and the running balances bracket it. Amounts are stored
        // unsigned with the direction carried by Type — assert that, so a future signed-amount
        // change cannot quietly double the debit.
        outgoing.Amount.Should().Be(100m);
        incoming.Amount.Should().Be(100m);
        (outgoing.BalanceBefore - outgoing.BalanceAfter).Should().Be(100m, "the sender is debited");
        (incoming.BalanceAfter - incoming.BalanceBefore).Should().Be(100m, "the recipient is credited");

        // The payer's own words, on both rows: each side reads its own row.
        outgoing.Description.Should().Be("Ledger shape");
        incoming.Description.Should().Be("Ledger shape");
    }

    [Fact]
    public async Task ExternalTransfer_SentWithNoDescription_NamesTheOtherPartyOnEachRow()
    {
        // The text a statement shows when the payer wrote none: the payee's handle on the payer's
        // row, the payer's handle on the payee's. Only the seeder's rows were checked for it.
        var (senderToken, senderId, senderAccountId) = await RegisterTestUserAsync();
        await SetPinAsync(senderToken);
        var recipient = await RegisterRecipientAsync();
        await DepositAsync(senderToken, senderAccountId, 1000m);

        SetAuthHeader(senderToken);
        var response = await PostMonetaryAsync("/api/transfers", new TransferRequest
        {
            FromAccountId = senderAccountId,
            RecipientAzureTag = recipient.AzureTag,
            Amount = 100m
        }, stepUpAuthorizationId: await AuthoriseTransferAsync(senderAccountId, recipient.AzureTag, 100m));
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var senderTag = (await db.Users.AsNoTracking().SingleAsync(u => u.Id == senderId)).AzureTag;

        var outgoing = await db.Transactions.SingleAsync(
            t => t.AccountId == senderAccountId && t.Type == TransactionType.TransferOut);
        var incoming = await db.Transactions.SingleAsync(
            t => t.AccountId == recipient.AccountId && t.Type == TransactionType.TransferIn);

        outgoing.Description.Should().Be($"Transfer to @{recipient.AzureTag}");
        incoming.Description.Should().Be($"Transfer from @{senderTag}");
    }

    [Fact]
    public async Task InternalTransfer_LinksTheTwoRowsToTheRIGHTAccounts()
    {
        /*
          The internal case needs its own test rather than a parameterised twin, because both rows
          belong to ONE user. Every per-user count or total still balances if the service credits
          the wrong one of the caller's own accounts — the row-level account ids are the only thing
          that catches a swap, and nothing asserted them before.
        */
        var (token, _, primaryAccountId) = await RegisterTestUserAsync();
        // ADR-0042: the PIN is spent at the mint endpoint, so an un-enrolled user is refused
        // 422 PIN_REQUIRED there and never obtains the authorisation this transfer requires.
        await SetPinAsync(token);
        SetAuthHeader(token);

        var created = await Client.PostAsJsonAsync("/api/accounts",
            new CreateAccountRequest { Name = "Savings", Type = AccountType.Savings }, JsonOptions);
        var savings = (await created.Content.ReadFromJsonAsync<ApiResponse<AccountResponse>>(JsonOptions))!.Data!;

        await DepositAsync(token, primaryAccountId, 1000m);

        var response = await PostMonetaryAsync("/api/transfers/internal", new InternalTransferRequest
        {
            FromAccountId = primaryAccountId,
            ToAccountId = savings.Id,
            Amount = 300m,
            Description = "Ledger shape"
        }, stepUpAuthorizationId: await AuthoriseInternalTransferAsync(primaryAccountId, savings.Id, 300m));
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

        var outgoing = await db.Transactions.SingleAsync(
            t => t.AccountId == primaryAccountId && t.Type == TransactionType.TransferOut);
        var incoming = await db.Transactions.SingleAsync(
            t => t.AccountId == savings.Id && t.Type == TransactionType.TransferIn);

        outgoing.RelatedTransactionId.Should().Be(incoming.Id);
        incoming.RelatedTransactionId.Should().Be(outgoing.Id);
        (outgoing.BalanceBefore - outgoing.BalanceAfter).Should().Be(300m);
        (incoming.BalanceAfter - incoming.BalanceBefore).Should().Be(300m);

        // An internal move is between the caller's own accounts, so there is no counterparty to
        // record. Writing one would leak a tag into a place the UI reads as "someone else".
        outgoing.SenderAzureTag.Should().BeNull();
        outgoing.RecipientAzureTag.Should().BeNull();
        incoming.SenderAzureTag.Should().BeNull();
        incoming.RecipientAzureTag.Should().BeNull();

        // The user's own words, on both rows.
        outgoing.Description.Should().Be("Ledger shape");
        incoming.Description.Should().Be("Ledger shape");
    }

    [Fact]
    public async Task InternalTransfer_SentWithNoDescription_NamesTheOtherAccountOnEachRow()
    {
        // The text a statement shows when the user wrote none: the destination's name on the row
        // of the account the money left, the source's name on the row of the one it reached.
        var (token, _, primaryAccountId) = await RegisterTestUserAsync();
        await SetPinAsync(token);
        SetAuthHeader(token);

        var created = await Client.PostAsJsonAsync("/api/accounts",
            new CreateAccountRequest { Name = "Savings", Type = AccountType.Savings }, JsonOptions);
        var savings = (await created.Content.ReadFromJsonAsync<ApiResponse<AccountResponse>>(JsonOptions))!.Data!;

        await DepositAsync(token, primaryAccountId, 1000m);

        var response = await PostMonetaryAsync("/api/transfers/internal", new InternalTransferRequest
        {
            FromAccountId = primaryAccountId,
            ToAccountId = savings.Id,
            Amount = 300m
        }, stepUpAuthorizationId: await AuthoriseInternalTransferAsync(primaryAccountId, savings.Id, 300m));
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var primaryName = (await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == primaryAccountId)).Name;
        primaryName.Should().NotBeNullOrWhiteSpace().And.NotBe("Savings", "the two names must tell the rows apart");

        var outgoing = await db.Transactions.SingleAsync(
            t => t.AccountId == primaryAccountId && t.Type == TransactionType.TransferOut);
        var incoming = await db.Transactions.SingleAsync(
            t => t.AccountId == savings.Id && t.Type == TransactionType.TransferIn);

        outgoing.Description.Should().Be("Internal transfer to Savings");
        incoming.Description.Should().Be($"Internal transfer from {primaryName}");
    }

    [Fact]
    public async Task ExternalTransfer_ReturnsTheRecipientNameMaskedAndAResolvableTransferId()
    {
        /*
          The response half of the deleted stubs. Two members were asserted in no backend test:

          - `RecipientName` is deliberately abbreviated to "First L." (TransferService), so the
            sender can confirm who they paid without the app disclosing a full name from an
            azuretag. Only the frontend mock asserted the shape, and a mock is not an oracle.
          - `TransactionNumber` is the receipt's handle. It is only useful if it identifies the
            SENDER's row — a number that resolves to the recipient's row would show the payer a
            credit. Note this DTO carries no `TransferId`; that member is on the INTERNAL response,
            which is why the two are asserted differently.
        */
        var (senderToken, _, senderAccountId) = await RegisterTestUserAsync();
        // ADR-0042: the PIN is spent at the mint endpoint, so an un-enrolled user is refused
        // 422 PIN_REQUIRED there and never obtains the authorisation this transfer requires.
        await SetPinAsync(senderToken);
        var recipient = await RegisterRecipientAsync();
        await DepositAsync(senderToken, senderAccountId, 1000m);

        SetAuthHeader(senderToken);
        var response = await PostMonetaryAsync("/api/transfers", new TransferRequest
        {
            FromAccountId = senderAccountId,
            RecipientAzureTag = recipient.AzureTag,
            Amount = 100m,
            Description = "Receipt"
        }, stepUpAuthorizationId: await AuthoriseTransferAsync(senderAccountId, recipient.AzureTag, 100m));
        var body = (await response.Content.ReadFromJsonAsync<ApiResponse<TransferResponse>>(JsonOptions))!.Data!;

        body.RecipientName.Should().MatchRegex(@"^\S+ \S\.$",
            "the recipient is shown as \"First L.\", never a full surname");
        body.RecipientName.Should().Be("Recipient U.").And.NotContain("User");

        body.TransactionNumber.Should().NotBeNullOrWhiteSpace();
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var referenced = await db.Transactions
            .SingleAsync(t => t.TransactionNumber == body.TransactionNumber);
        referenced.AccountId.Should().Be(senderAccountId, "the receipt points at the SENDER's row");
        referenced.Type.Should().Be(TransactionType.TransferOut);
        body.NewBalance.Should().Be(referenced.BalanceAfter,
            "the balance shown on the receipt is the one written to the ledger");
    }
}
