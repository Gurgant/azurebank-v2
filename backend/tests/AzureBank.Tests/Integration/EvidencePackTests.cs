using System.Net;
using System.Net.Http.Json;
using AzureBank.AuditVerifier.Commands;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Transfer;
using AzureBank.Shared.Enums;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The evidence pack, assembled against rows the REAL API wrote: a registered payer with a PIN, a
/// registered payee, a minted authorisation, a transfer that consumed it — then
/// <c>EvidenceCommand</c> run over the same store through the API's own composition root.
/// </summary>
/// <remarks>
/// <para>
/// THROUGH THE API, NOT A HAND-BUILT LEDGER, and the reason is the join itself. What the pack
/// asserts is that three things written by three different code paths agree — the ledger row from
/// <c>TransferService</c>, the consumed authorisation from <c>ConsumeAsync</c> inside the same
/// transaction, and the audit row from the <c>SaveChanges</c> funnel. A fixture that wrote those
/// rows by hand would be asserting that the fixture agrees with itself. Every case here gets its
/// rows from <c>POST /api/transfers</c> or <c>/deposit</c>, and only the cases about ABSENCE
/// reach into the store afterwards, to remove exactly one thing and see it named.
/// </para>
/// <para>
/// <c>Factory.Services</c> is the API's root, which registers <c>IAuditChain</c> and
/// <c>IAuditAnchorChain</c> the same way the verifier's does, and the InMemory database root is
/// shared, so the verb reads the rows the requests just wrote.
/// </para>
/// </remarks>
public class EvidencePackTests : IntegrationTestBase
{
    public EvidencePackTests(CustomWebApplicationFactory factory) : base(factory) { }

    private const decimal Amount = 40m;

    private async Task<(string Token, Guid UserId, Guid Account, string RecipientTag)> ScenarioAsync()
    {
        var (token, userId, account) = await RegisterTestUserAsync();

        var unique = Guid.NewGuid().ToString("N")[..8];
        var recipientTag = $"payee_{unique}";
        var registered = await Client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = recipientTag,
            Email = $"payee{unique}@example.com",
            Password = TestUserPassword,
            FirstName = "Payee",
            LastName = "User",
        }, JsonOptions);
        registered.EnsureSuccessStatusCode();

        await DepositAsync(token, account, 500m);
        await SetPinAsync(token);
        SetAuthHeader(token);
        return (token, userId, account, recipientTag);
    }

    /// <summary>A real transfer, and the number the API handed back for it.</summary>
    private async Task<string> TransferAsync(Guid from, string recipientTag)
    {
        var authorisation = await AuthoriseTransferAsync(from, recipientTag, Amount);
        var response = await PostMonetaryAsync(
            "/api/transfers",
            new TransferRequest
            {
                FromAccountId = from,
                RecipientAzureTag = recipientTag,
                Amount = Amount,
                Description = "evidence pack",
            },
            stepUpAuthorizationId: authorisation);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<TransferResponse>>(JsonOptions);
        return body!.Data!.TransactionNumber;
    }

    private Task<(int ExitCode, string[] Lines)> EvidenceAsync(string number) =>
        EvidenceCommand.RunAsync(Factory.Services, number, CancellationToken.None);

    private AzureBankDbContext Store(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

    [Fact]
    public async Task ATransferThatConsumedAnAuthorisation_IsSTRONGLYAUTHENTICATED_AndNamesRealRows()
    {
        var (_, userId, account, recipient) = await ScenarioAsync();
        var number = await TransferAsync(account, recipient);

        var (exitCode, lines) = await EvidenceAsync(number);
        var text = string.Join("\n", lines);

        exitCode.Should().Be(
            VerifyCommand.Intact, "the chain is untouched, and the pack exits with the chain's verdict");
        lines[0].Should().Be($"EVIDENCE PACK for {number}");
        text.Should().Contain("STRONGLY AUTHENTICATED: authorisation ");
        text.Should().Contain($"{SecurityEvents.MoneyTransferred} -> Succeeded");
        text.Should().Contain("CHAIN INTACT");

        /*
          THE IDS IN THE PACK ARE THE IDS IN THE STORE, checked rather than trusted. A pack that
          printed A consumed authorisation would pass a Contains on the headline; this asserts it
          printed THE one, by reading the row the way the verb read it and comparing the identifier
          the two lines carry.
        */
        using var scope = Factory.Services.CreateScope();
        var store = Store(scope);
        var movement = await store.Transactions.AsNoTracking()
            .SingleAsync(t => t.TransactionNumber == number);
        var consumed = await store.StepUpAuthorizations.AsNoTracking()
            .SingleAsync(a => a.UserId == userId && a.ConsumedByTransactionId == movement.Id);

        text.Should().Contain($"authorisation {consumed.Id:D} paid for this transfer");
        text.Should().Contain(
            "This row is evidence the application wrote, and it is NOT inside the",
            "the limit travels with every positive answer, not only with the docs");
    }

    [Fact]
    public async Task ADeposit_HasNoAuthorisationToShow_AndSaysWhichVerbWouldHave()
    {
        var (_, _, account, _) = await ScenarioAsync();

        using var scope = Factory.Services.CreateScope();
        var deposit = await Store(scope).Transactions.AsNoTracking()
            .Where(t => t.AccountId == account && t.Type == TransactionType.Deposit)
            .OrderByDescending(t => t.CreatedAt)
            .FirstAsync();

        var (exitCode, lines) = await EvidenceAsync(deposit.TransactionNumber);
        var text = string.Join("\n", lines);

        exitCode.Should().Be(VerifyCommand.Intact);
        text.Should().Contain("NO AUTHORISATION APPLIES: a Deposit carries no step-up authorisation.");
        text.Should().NotContain("NOT STRONGLY AUTHENTICATED",
            "a deposit is not a transfer that lost its authorisation; the two must not read alike");
        text.Should().Contain($"{SecurityEvents.MoneyDeposited} -> Succeeded");
    }

    [Fact]
    public async Task TheIncomingLegOfATransfer_IsNotTheOneMintedAgainst_AndThePackPointsAtTheOtherLeg()
    {
        var (_, _, account, recipient) = await ScenarioAsync();
        var number = await TransferAsync(account, recipient);

        using var scope = Factory.Services.CreateScope();
        var store = Store(scope);
        var outgoing = await store.Transactions.AsNoTracking()
            .SingleAsync(t => t.TransactionNumber == number);
        var incoming = await store.Transactions.AsNoTracking()
            .SingleAsync(t => t.Id == outgoing.RelatedTransactionId);

        var (exitCode, lines) = await EvidenceAsync(incoming.TransactionNumber);
        var text = string.Join("\n", lines);

        exitCode.Should().Be(VerifyCommand.Intact);
        text.Should().Contain("NO AUTHORISATION APPLIES: a TransferIn carries no step-up");
        text.Should().Contain(
            $"Other leg: {number}", "the operator is sent to the leg that WAS minted against");
    }

    [Fact]
    public async Task ATransferWhoseAuthorisationRowIsGone_IsNOTSTRONGLYAUTHENTICATED_ChainBlind()
    {
        /*
          THE HONEST HALF OF THE DESIGN, PINNED. The authorisation table is not chained, so removing
          the row that paid for a transfer leaves the chain intact -- the pack must say the transfer
          is not strongly authenticated AND must still report CHAIN INTACT, because both are true,
          and a pack that let the second imply the first would be the green-and-false this tool was
          built to refuse.
        */
        var (_, userId, account, recipient) = await ScenarioAsync();
        var number = await TransferAsync(account, recipient);

        using (var scope = Factory.Services.CreateScope())
        {
            var store = Store(scope);
            var movement = await store.Transactions.AsNoTracking()
                .SingleAsync(t => t.TransactionNumber == number);
            var consumed = await store.StepUpAuthorizations
                .SingleAsync(a => a.UserId == userId && a.ConsumedByTransactionId == movement.Id);
            store.StepUpAuthorizations.Remove(consumed);
            await store.SaveChangesAsync();
        }

        var (exitCode, lines) = await EvidenceAsync(number);
        var text = string.Join("\n", lines);

        exitCode.Should().Be(
            VerifyCommand.Intact,
            "the chain does not cover that table, and the pack must not pretend it does");
        text.Should().Contain(
            "NOT STRONGLY AUTHENTICATED: no consumed authorisation names this transaction.");
        text.Should().Contain("CHAIN INTACT");
        // A phrase that sits on ONE printed line: the sentence it belongs to wraps, and a Contains
        // across the wrap is the rewrap trap UncoveredWindowTests already caught once.
        text.Should().Contain("the table it lived in is NOT chained");
    }

    [Fact]
    public async Task ANumberTheStoreDoesNotHold_IsAUsageError_AndTheChainIsNotWalked()
    {
        await ScenarioAsync();

        var (exitCode, lines) = await EvidenceAsync("TXN-20260101-0000000000Z");
        var text = string.Join("\n", lines);

        exitCode.Should().Be(
            VerifyCommand.UsageError,
            "a number this store does not hold is a fact about the command line");
        exitCode.Should().NotBe(VerifyCommand.Broken, "and must never read as a tampered chain");
        text.Should().Contain("NOT ASSEMBLED: no transaction is numbered TXN-20260101-0000000000Z.");
        text.Should().NotContain("CHAIN", "the walk is not run for a movement that does not exist");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("TXN\0oops")]
    public async Task ABlankOrNULArgument_IsRefusedBeforeTheStoreIsAsked(string argument)
    {
        var (exitCode, lines) = await EvidenceAsync(argument);

        exitCode.Should().Be(VerifyCommand.UsageError);
        lines[0].Should().Be("NOT ASSEMBLED: that is not a transaction number.");
    }
}

/// <summary>
/// The one case that MUTATES an audit row, isolated so its break cannot leak into the intact cases:
/// a class of its own is a fixture of its own, which is an InMemory root of its own.
/// </summary>
public class EvidencePackBrokenChainTests : IntegrationTestBase
{
    public EvidencePackBrokenChainTests(CustomWebApplicationFactory factory) : base(factory) { }

    private const decimal Amount = 40m;

    private async Task<(Guid Account, string RecipientTag)> ScenarioAsync()
    {
        var (token, _, account) = await RegisterTestUserAsync();
        var unique = Guid.NewGuid().ToString("N")[..8];
        var recipientTag = $"payee_{unique}";
        var registered = await Client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = recipientTag,
            Email = $"payee{unique}@example.com",
            Password = TestUserPassword,
            FirstName = "Payee",
            LastName = "User",
        }, JsonOptions);
        registered.EnsureSuccessStatusCode();
        await DepositAsync(token, account, 500m);
        await SetPinAsync(token);
        SetAuthHeader(token);
        return (account, recipientTag);
    }

    [Fact]
    public async Task ATamperedAuditRow_PrintsThePackAndExitsWithTheChainsVerdict()
    {
        /*
          THE VERDICT ABOUT THE CHAIN IS STILL THE VERDICT -- export's rule, inherited. The pack is
          assembled and printed in full ABOVE the broken verdict, because a regulator asking about
          one transfer during an incident needs both halves, and the exit code carries the half
          automation must not miss.

          ⚠️ IN ITS OWN CLASS BECAUSE IT BREAKS THE CHAIN FOR EVERY TEST AFTER IT. IClassFixture
          shares ONE factory -- one InMemory root -- across a class, and the first version of this
          file put this test beside the others: measured, two intact-chain assertions failed with
          exit 1 in the same run, in whichever order xUnit chose. A tamper is not undone by the test
          that made it, so it gets a root nobody else reads.
        */
        var (account, recipient) = await ScenarioAsync();
        var authorisation = await AuthoriseTransferAsync(account, recipient, Amount);
        var response = await PostMonetaryAsync(
            "/api/transfers",
            new TransferRequest
            {
                FromAccountId = account,
                RecipientAzureTag = recipient,
                Amount = Amount,
                Description = "evidence pack, broken chain",
            },
            stepUpAuthorizationId: authorisation);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<TransferResponse>>(JsonOptions);
        var number = body!.Data!.TransactionNumber;

        using (var scope = Factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
            var movement = await store.Transactions.AsNoTracking()
                .SingleAsync(t => t.TransactionNumber == number);
            var row = await store.AuditEvents.SingleAsync(e => e.SubjectId == movement.Id);
            row.Outcome = AuditOutcome.Refused;
            await store.SaveChangesAsync();
        }

        var (exitCode, lines) = await EvidenceCommand.RunAsync(
            Factory.Services, number, CancellationToken.None);
        var text = string.Join("\n", lines);

        exitCode.Should().Be(VerifyCommand.Broken);
        lines[0].Should().Be(
            $"EVIDENCE PACK for {number}", "the pack is still printed; the code is what changes");
        text.Should().Contain("STRONGLY AUTHENTICATED: authorisation ");
        text.Should().Contain("CHAIN BROKEN at sequence");
    }

}
