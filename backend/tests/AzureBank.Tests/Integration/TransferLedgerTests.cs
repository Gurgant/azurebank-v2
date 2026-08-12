using System.Net;
using System.Net.Http.Json;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.DTOs.Auth;
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
    /// <summary>The PIN these tests enrol and then send in-band (ADR-0041).</summary>
    private const string TestPin = "123456";

    public TransferLedgerTests(CustomWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task ExternalTransfer_WritesAMutuallyLinkedPairWithOppositeTypes()
    {
        var (senderToken, _, senderAccountId) = await RegisterTestUserAsync();
        // ADR-0041: a transfer now carries the PIN in-band and the API verifies it,
        // so an un-enrolled user is refused 422 PIN_REQUIRED before any rule below.
        await SetPinAsync(senderToken);
        var recipient = await RegisterRecipientAsync();
        await DepositAsync(senderToken, senderAccountId, 1000m);

        SetAuthHeader(senderToken);
        var response = await PostMonetaryAsync("/api/transfers", new TransferRequest
        {
            FromAccountId = senderAccountId,
            RecipientAzureTag = recipient.AzureTag,
            Amount = 100m,
            Description = "Ledger shape",
        
            Pin = TestPin
        });
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
        // ADR-0041: a transfer now carries the PIN in-band and the API verifies it,
        // so an un-enrolled user is refused 422 PIN_REQUIRED before any rule below.
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
            Description = "Ledger shape",
        
            Pin = TestPin
        });
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
        // ADR-0041: a transfer now carries the PIN in-band and the API verifies it,
        // so an un-enrolled user is refused 422 PIN_REQUIRED before any rule below.
        await SetPinAsync(senderToken);
        var recipient = await RegisterRecipientAsync();
        await DepositAsync(senderToken, senderAccountId, 1000m);

        SetAuthHeader(senderToken);
        var response = await PostMonetaryAsync("/api/transfers", new TransferRequest
        {
            FromAccountId = senderAccountId,
            RecipientAzureTag = recipient.AzureTag,
            Amount = 100m,
            Description = "Receipt",
        
            Pin = TestPin
        });
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

    /// <summary>Registers a counterparty named "Recipient User", so the mask is "Recipient U.".</summary>
    private async Task<(string AzureTag, Guid UserId, Guid AccountId)> RegisterRecipientAsync()
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var azureTag = $"recipient_{uniqueId}";

        var response = await Client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = azureTag,
            Email = $"recipient{uniqueId}@example.com",
            Password = "TestPass123!",
            FirstName = "Recipient",
            LastName = "User",
        }, JsonOptions);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(JsonOptions);
        return (azureTag, result!.Data!.User.Id, result.Data.Account.Id);
    }
}
