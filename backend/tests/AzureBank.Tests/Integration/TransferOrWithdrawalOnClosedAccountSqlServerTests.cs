using System.Net;
using AzureBank.Shared.Constants;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// What the two transfers and the withdrawal answer when one of their accounts is closed: before
/// the request (the control) and while the request runs (the race). Every proof here ends in a
/// refusal that moves nothing: no balance, no ledger row, no money audit row, and the step-up
/// authorisation the request presented stays unspent.
/// </summary>
/// <remarks>
/// <para>
/// TWO PLACES FOR THE RACE. "At the save" closes the account from a second connection on the
/// request's own <c>UPDATE [Accounts]</c>: the save loses the account's <c>RowVersion</c>, the
/// request reloads, and the reload returns the closed row, because a reload does not apply the
/// filter that hides closed accounts from every query. "Before its first reload" closes the
/// account on the read of the step-up authorisation, after the request looked at its accounts and
/// before the first attempt reloads them: no save is lost and nothing goes round a retry, the
/// first attempt itself holds a closed row with a current <c>RowVersion</c>. Each race asserts
/// which of the two it is, from what the request sent to the accounts table.
/// </para>
/// <para>
/// The API closes only an empty account that is not the primary one
/// (<c>AccountService.RefuseIfNotClosable</c>). So it cannot close an account while that account
/// holds the money a request is about to take from it, and it cannot close a payee's primary
/// account. Those races close the row directly: they hold what the request does with a closed
/// row, however it came to be closed. The races named "emptied and closed" leave the account
/// columns the API can produce for a source account, a withdrawal of everything and then a
/// closure; the ledger is not touched.
/// </para>
/// <para>
/// A payee's account that closes while another account of that payee is open is paid on that
/// other account: <see cref="PayeeAccountClosedMeanwhileSqlServerTests"/>. Here the payee has
/// no other.
/// </para>
/// <para>
/// Gated by AZUREBANK_TEST_SQLSERVER and serialised with the other SQL proofs, for two reasons.
/// The races at the save need a <c>rowversion</c>, which the in-memory provider does not have, so
/// no request there ever loses a save. And every race, at either place, is written by a raw
/// <c>SqlConnection</c> beside the request, which only a real server can take.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class TransferOrWithdrawalOnClosedAccountSqlServerTests(ITestOutputHelper output)
    : ClosedAccountSqlServerProofs(output)
{
    // ── External transfer, the payee's account ───────────────────────────────

    [SqlServerFact]
    public async Task Control_TransferToAPayeeWhoseOnlyAccountClosedBeforeTheRequest_IsRefused()
    {
        var client = CreateSqlClient();
        var sender = await RegisterWithPinAsync(client, "xdc");
        var payee = await RegisterAsync(client, "xdcp");
        await FundAsync(client, sender, sender.PrimaryAccountId);
        var authorizationId = await MintTransferAsync(client, sender, sender.PrimaryAccountId, payee.AzureTag);

        // The API refuses to close a primary account, so the control closes the row directly.
        (await OutOfBandClosureInterceptor.CloseAsync(
            SqlServerFactAttribute.ConnectionString!, payee.PrimaryAccountId)).Should().Be(1);

        var key = Guid.NewGuid();
        var response = await TransferAsync(
            client, sender, sender.PrimaryAccountId, payee.AzureTag, authorizationId, key);
        var seen = await ObserveAsync(
            response, sender, SecurityEvents.MoneyTransferred, authorizationId, key, race: null,
            sender.PrimaryAccountId, payee.PrimaryAccountId);

        using var scope = new AssertionScope();
        ShouldBeRefused(seen, HttpStatusCode.UnprocessableEntity, ErrorCodes.RecipientNoAccount, NoActiveAccount);
        ShouldHaveMovedNothing(seen, new AccountRow(false, Funding, 1), new AccountRow(true, 0m, 0));
    }

    [SqlServerFact]
    public async Task Collision_TransferRacingThePayeeAccountClosure_IsRefused_AndMovesNothing()
    {
        var client = CreateSqlClient();
        var sender = await RegisterWithPinAsync(client, "xdr");
        var payee = await RegisterAsync(client, "xdrp");
        await FundAsync(client, sender, sender.PrimaryAccountId);
        var authorizationId = await MintTransferAsync(client, sender, sender.PrimaryAccountId, payee.AzureTag);

        var race = ArmClosureOf(payee.PrimaryAccountId);

        var key = Guid.NewGuid();
        var response = await TransferAsync(
            client, sender, sender.PrimaryAccountId, payee.AzureTag, authorizationId, key);
        var seen = await ObserveAsync(
            response, sender, SecurityEvents.MoneyTransferred, authorizationId, key, race,
            sender.PrimaryAccountId, payee.PrimaryAccountId);

        using var scope = new AssertionScope();
        ShouldHaveClosedAtTheSave(race, seen, savesAfterwards: 0);
        ShouldBeRefused(seen, HttpStatusCode.UnprocessableEntity, ErrorCodes.RecipientNoAccount, NoActiveAccount);
        ShouldHaveMovedNothing(seen, new AccountRow(false, Funding, 1), new AccountRow(true, 0m, 0));
    }

    [SqlServerFact]
    public async Task Closure_OfThePayeeOnlyAccountBeforeTheTransferFirstReload_IsRefused_AndMovesNothing()
    {
        var client = CreateSqlClient();
        var sender = await RegisterWithPinAsync(client, "xdf");
        var payee = await RegisterAsync(client, "xdfp");
        await FundAsync(client, sender, sender.PrimaryAccountId);
        var authorizationId = await MintTransferAsync(client, sender, sender.PrimaryAccountId, payee.AzureTag);

        /*
          No save is lost here. The closure lands on the read of the authorisation, after the
          transfer resolved the payee and before the attempt reloads both accounts, so the first
          attempt itself holds a closed row with a current RowVersion and nothing sends it round
          the retry.
        */
        var race = ArmClosureOf(payee.PrimaryAccountId, trigger: AuthorisationRead);

        var key = Guid.NewGuid();
        var response = await TransferAsync(
            client, sender, sender.PrimaryAccountId, payee.AzureTag, authorizationId, key);
        var seen = await ObserveAsync(
            response, sender, SecurityEvents.MoneyTransferred, authorizationId, key, race,
            sender.PrimaryAccountId, payee.PrimaryAccountId);

        using var scope = new AssertionScope();
        ShouldHaveClosedBeforeTheFirstReload(race, seen, savesAfterwards: 0);
        ShouldBeRefused(seen, HttpStatusCode.UnprocessableEntity, ErrorCodes.RecipientNoAccount, NoActiveAccount);
        ShouldHaveMovedNothing(seen, new AccountRow(false, Funding, 1), new AccountRow(true, 0m, 0));
    }

    // ── External transfer, the sender's account ──────────────────────────────

    [SqlServerFact]
    public async Task Control_TransferFromAnAccountClosedBeforeTheRequest_IsRefused()
    {
        var client = CreateSqlClient();
        var sender = await RegisterWithPinAsync(client, "xsc");
        var payee = await RegisterAsync(client, "xscp");
        var source = await CreateSpareAsync(client, sender);
        var authorizationId = await MintTransferAsync(client, sender, source, payee.AzureTag);
        await CloseThroughTheApiAsync(client, sender, source);

        var key = Guid.NewGuid();
        var response = await TransferAsync(client, sender, source, payee.AzureTag, authorizationId, key);
        var seen = await ObserveAsync(
            response, sender, SecurityEvents.MoneyTransferred, authorizationId, key, race: null,
            source, payee.PrimaryAccountId);

        using var scope = new AssertionScope();
        ShouldBeRefused(seen, HttpStatusCode.NotFound, ErrorCodes.AccountNotFound, NotFound(source));
        ShouldHaveMovedNothing(seen, new AccountRow(true, 0m, 0), new AccountRow(false, 0m, 0));
    }

    [SqlServerFact]
    public async Task Collision_TransferRacingItsSourceAccountClosure_IsRefused_AndMovesNothing()
    {
        var client = CreateSqlClient();
        var sender = await RegisterWithPinAsync(client, "xsr");
        var payee = await RegisterAsync(client, "xsrp");
        var source = await CreateSpareAsync(client, sender);
        await FundAsync(client, sender, source);
        var authorizationId = await MintTransferAsync(client, sender, source, payee.AzureTag);

        var race = ArmClosureOf(source);

        var key = Guid.NewGuid();
        var response = await TransferAsync(client, sender, source, payee.AzureTag, authorizationId, key);
        var seen = await ObserveAsync(
            response, sender, SecurityEvents.MoneyTransferred, authorizationId, key, race,
            source, payee.PrimaryAccountId);

        using var scope = new AssertionScope();
        ShouldHaveClosedAtTheSave(race, seen, savesAfterwards: 0);
        ShouldBeRefused(seen, HttpStatusCode.NotFound, ErrorCodes.AccountNotFound, NotFound(source));
        ShouldHaveMovedNothing(seen, new AccountRow(true, Funding, 1), new AccountRow(false, 0m, 0));
    }

    [SqlServerFact]
    public async Task Collision_TransferRacingItsSourceBeingEmptiedAndClosed_IsRefusedAsAClosedAccount()
    {
        var client = CreateSqlClient();
        var sender = await RegisterWithPinAsync(client, "xse");
        var payee = await RegisterAsync(client, "xsep");
        var source = await CreateSpareAsync(client, sender);
        await FundAsync(client, sender, source);
        var authorizationId = await MintTransferAsync(client, sender, source, payee.AzureTag);

        var race = ArmClosureOf(source, emptyFirst: true);

        var key = Guid.NewGuid();
        var response = await TransferAsync(client, sender, source, payee.AzureTag, authorizationId, key);
        var seen = await ObserveAsync(
            response, sender, SecurityEvents.MoneyTransferred, authorizationId, key, race,
            source, payee.PrimaryAccountId);

        using var scope = new AssertionScope();
        ShouldHaveClosedAtTheSave(race, seen, savesAfterwards: 0);
        ShouldBeRefused(seen, HttpStatusCode.NotFound, ErrorCodes.AccountNotFound, NotFound(source));
        ShouldHaveMovedNothing(seen, new AccountRow(true, 0m, 1), new AccountRow(false, 0m, 0));
    }

    [SqlServerFact]
    public async Task Closure_OfTheSourceEmptiedBeforeTheTransferFirstReload_IsRefusedAsAClosedAccount()
    {
        var client = CreateSqlClient();
        var sender = await RegisterWithPinAsync(client, "xsf");
        var payee = await RegisterAsync(client, "xsfp");
        var source = await CreateSpareAsync(client, sender);
        await FundAsync(client, sender, source);
        var authorizationId = await MintTransferAsync(client, sender, source, payee.AzureTag);

        // The first attempt reloads an account that is closed and empty. Its funds check is the
        // next line, and the answer is the closed account's, not 422 INSUFFICIENT_FUNDS.
        var race = ArmClosureOf(source, trigger: AuthorisationRead, emptyFirst: true);

        var key = Guid.NewGuid();
        var response = await TransferAsync(client, sender, source, payee.AzureTag, authorizationId, key);
        var seen = await ObserveAsync(
            response, sender, SecurityEvents.MoneyTransferred, authorizationId, key, race,
            source, payee.PrimaryAccountId);

        using var scope = new AssertionScope();
        ShouldHaveClosedBeforeTheFirstReload(race, seen, savesAfterwards: 0);
        ShouldBeRefused(seen, HttpStatusCode.NotFound, ErrorCodes.AccountNotFound, NotFound(source));
        ShouldHaveMovedNothing(seen, new AccountRow(true, 0m, 1), new AccountRow(false, 0m, 0));
    }

    // ── Internal transfer, the destination account ───────────────────────────

    [SqlServerFact]
    public async Task Control_InternalTransferIntoAnAccountClosedBeforeTheRequest_IsRefused()
    {
        var client = CreateSqlClient();
        var user = await RegisterWithPinAsync(client, "idc");
        var destination = await CreateSpareAsync(client, user);
        await FundAsync(client, user, user.PrimaryAccountId);
        var authorizationId = await MintInternalAsync(client, user, user.PrimaryAccountId, destination);
        await CloseThroughTheApiAsync(client, user, destination);

        var key = Guid.NewGuid();
        var response = await InternalTransferAsync(
            client, user, user.PrimaryAccountId, destination, authorizationId, key);
        var seen = await ObserveAsync(
            response, user, SecurityEvents.MoneyTransferredInternally, authorizationId, key, race: null,
            user.PrimaryAccountId, destination);

        using var scope = new AssertionScope();
        ShouldBeRefused(seen, HttpStatusCode.NotFound, ErrorCodes.AccountNotFound, NotFound(destination));
        ShouldHaveMovedNothing(seen, new AccountRow(false, Funding, 1), new AccountRow(true, 0m, 0));
    }

    [SqlServerFact]
    public async Task Collision_InternalTransferRacingItsDestinationClosure_IsRefused_AndMovesNothing()
    {
        var client = CreateSqlClient();
        var user = await RegisterWithPinAsync(client, "idr");
        var destination = await CreateSpareAsync(client, user);
        await FundAsync(client, user, user.PrimaryAccountId);
        var authorizationId = await MintInternalAsync(client, user, user.PrimaryAccountId, destination);

        var race = ArmClosureOf(destination);

        var key = Guid.NewGuid();
        var response = await InternalTransferAsync(
            client, user, user.PrimaryAccountId, destination, authorizationId, key);
        var seen = await ObserveAsync(
            response, user, SecurityEvents.MoneyTransferredInternally, authorizationId, key, race,
            user.PrimaryAccountId, destination);

        using var scope = new AssertionScope();
        ShouldHaveClosedAtTheSave(race, seen, savesAfterwards: 0);
        ShouldBeRefused(seen, HttpStatusCode.NotFound, ErrorCodes.AccountNotFound, NotFound(destination));
        ShouldHaveMovedNothing(seen, new AccountRow(false, Funding, 1), new AccountRow(true, 0m, 0));
    }

    [SqlServerFact]
    public async Task Closure_OfTheDestinationBeforeTheInternalTransferFirstReload_IsRefused_AndMovesNothing()
    {
        var client = CreateSqlClient();
        var user = await RegisterWithPinAsync(client, "idf");
        var destination = await CreateSpareAsync(client, user);
        await FundAsync(client, user, user.PrimaryAccountId);
        var authorizationId = await MintInternalAsync(client, user, user.PrimaryAccountId, destination);

        // No save is lost here either: the closure lands on the read of the authorisation, after
        // the request looked at both accounts and before the first attempt reloads them.
        var race = ArmClosureOf(destination, trigger: AuthorisationRead);

        var key = Guid.NewGuid();
        var response = await InternalTransferAsync(
            client, user, user.PrimaryAccountId, destination, authorizationId, key);
        var seen = await ObserveAsync(
            response, user, SecurityEvents.MoneyTransferredInternally, authorizationId, key, race,
            user.PrimaryAccountId, destination);

        using var scope = new AssertionScope();
        ShouldHaveClosedBeforeTheFirstReload(race, seen, savesAfterwards: 0);
        ShouldBeRefused(seen, HttpStatusCode.NotFound, ErrorCodes.AccountNotFound, NotFound(destination));
        ShouldHaveMovedNothing(seen, new AccountRow(false, Funding, 1), new AccountRow(true, 0m, 0));
    }

    // ── Internal transfer, the source account ────────────────────────────────

    [SqlServerFact]
    public async Task Control_InternalTransferFromAnAccountClosedBeforeTheRequest_IsRefused()
    {
        var client = CreateSqlClient();
        var user = await RegisterWithPinAsync(client, "isc");
        var source = await CreateSpareAsync(client, user);
        var authorizationId = await MintInternalAsync(client, user, source, user.PrimaryAccountId);
        await CloseThroughTheApiAsync(client, user, source);

        var key = Guid.NewGuid();
        var response = await InternalTransferAsync(
            client, user, source, user.PrimaryAccountId, authorizationId, key);
        var seen = await ObserveAsync(
            response, user, SecurityEvents.MoneyTransferredInternally, authorizationId, key, race: null,
            source, user.PrimaryAccountId);

        using var scope = new AssertionScope();
        ShouldBeRefused(seen, HttpStatusCode.NotFound, ErrorCodes.AccountNotFound, NotFound(source));
        ShouldHaveMovedNothing(seen, new AccountRow(true, 0m, 0), new AccountRow(false, 0m, 0));
    }

    [SqlServerFact]
    public async Task Collision_InternalTransferRacingItsSourceClosure_IsRefused_AndMovesNothing()
    {
        var client = CreateSqlClient();
        var user = await RegisterWithPinAsync(client, "isr");
        var source = await CreateSpareAsync(client, user);
        await FundAsync(client, user, source);
        var authorizationId = await MintInternalAsync(client, user, source, user.PrimaryAccountId);

        var race = ArmClosureOf(source);

        var key = Guid.NewGuid();
        var response = await InternalTransferAsync(
            client, user, source, user.PrimaryAccountId, authorizationId, key);
        var seen = await ObserveAsync(
            response, user, SecurityEvents.MoneyTransferredInternally, authorizationId, key, race,
            source, user.PrimaryAccountId);

        using var scope = new AssertionScope();
        ShouldHaveClosedAtTheSave(race, seen, savesAfterwards: 0);
        ShouldBeRefused(seen, HttpStatusCode.NotFound, ErrorCodes.AccountNotFound, NotFound(source));
        ShouldHaveMovedNothing(seen, new AccountRow(true, Funding, 1), new AccountRow(false, 0m, 0));
    }

    [SqlServerFact]
    public async Task Collision_InternalTransferRacingItsSourceBeingEmptiedAndClosed_IsRefusedAsAClosedAccount()
    {
        var client = CreateSqlClient();
        var user = await RegisterWithPinAsync(client, "ise");
        var source = await CreateSpareAsync(client, user);
        await FundAsync(client, user, source);
        var authorizationId = await MintInternalAsync(client, user, source, user.PrimaryAccountId);

        var race = ArmClosureOf(source, emptyFirst: true);

        var key = Guid.NewGuid();
        var response = await InternalTransferAsync(
            client, user, source, user.PrimaryAccountId, authorizationId, key);
        var seen = await ObserveAsync(
            response, user, SecurityEvents.MoneyTransferredInternally, authorizationId, key, race,
            source, user.PrimaryAccountId);

        using var scope = new AssertionScope();
        ShouldHaveClosedAtTheSave(race, seen, savesAfterwards: 0);
        ShouldBeRefused(seen, HttpStatusCode.NotFound, ErrorCodes.AccountNotFound, NotFound(source));
        ShouldHaveMovedNothing(seen, new AccountRow(true, 0m, 1), new AccountRow(false, 0m, 0));
    }

    [SqlServerFact]
    public async Task Closure_OfTheSourceEmptiedBeforeTheInternalTransferFirstReload_IsRefusedAsAClosedAccount()
    {
        var client = CreateSqlClient();
        var user = await RegisterWithPinAsync(client, "isf");
        var source = await CreateSpareAsync(client, user);
        await FundAsync(client, user, source);
        var authorizationId = await MintInternalAsync(client, user, source, user.PrimaryAccountId);

        var race = ArmClosureOf(source, trigger: AuthorisationRead, emptyFirst: true);

        var key = Guid.NewGuid();
        var response = await InternalTransferAsync(
            client, user, source, user.PrimaryAccountId, authorizationId, key);
        var seen = await ObserveAsync(
            response, user, SecurityEvents.MoneyTransferredInternally, authorizationId, key, race,
            source, user.PrimaryAccountId);

        using var scope = new AssertionScope();
        ShouldHaveClosedBeforeTheFirstReload(race, seen, savesAfterwards: 0);
        ShouldBeRefused(seen, HttpStatusCode.NotFound, ErrorCodes.AccountNotFound, NotFound(source));
        ShouldHaveMovedNothing(seen, new AccountRow(true, 0m, 1), new AccountRow(false, 0m, 0));
    }

    // ── Withdrawal ───────────────────────────────────────────────────────────

    [SqlServerFact]
    public async Task Control_WithdrawalFromAnAccountClosedBeforeTheRequest_IsRefused()
    {
        var client = CreateSqlClient();
        var user = await RegisterWithPinAsync(client, "wc");
        var account = await CreateSpareAsync(client, user);
        var authorizationId = await MintWithdrawalAsync(client, user, account);
        await CloseThroughTheApiAsync(client, user, account);

        var key = Guid.NewGuid();
        var response = await WithdrawAsync(client, user, account, authorizationId, key);
        var seen = await ObserveAsync(
            response, user, SecurityEvents.MoneyWithdrawn, authorizationId, key, race: null, account);

        using var scope = new AssertionScope();
        ShouldBeRefused(seen, HttpStatusCode.NotFound, ErrorCodes.AccountNotFound, NotFound(account));
        ShouldHaveMovedNothing(seen, new AccountRow(true, 0m, 0));
    }

    [SqlServerFact]
    public async Task Collision_WithdrawalRacingTheAccountClosure_IsRefused_AndMovesNothing()
    {
        var client = CreateSqlClient();
        var user = await RegisterWithPinAsync(client, "wr");
        var account = await CreateSpareAsync(client, user);
        await FundAsync(client, user, account);
        var authorizationId = await MintWithdrawalAsync(client, user, account);

        var race = ArmClosureOf(account);

        var key = Guid.NewGuid();
        var response = await WithdrawAsync(client, user, account, authorizationId, key);
        var seen = await ObserveAsync(
            response, user, SecurityEvents.MoneyWithdrawn, authorizationId, key, race, account);

        using var scope = new AssertionScope();
        ShouldHaveClosedAtTheSave(race, seen, savesAfterwards: 0);
        ShouldBeRefused(seen, HttpStatusCode.NotFound, ErrorCodes.AccountNotFound, NotFound(account));
        ShouldHaveMovedNothing(seen, new AccountRow(true, Funding, 1));
    }

    [SqlServerFact]
    public async Task Collision_WithdrawalRacingTheAccountBeingEmptiedAndClosed_IsRefusedAsAClosedAccount()
    {
        var client = CreateSqlClient();
        var user = await RegisterWithPinAsync(client, "we");
        var account = await CreateSpareAsync(client, user);
        await FundAsync(client, user, account);
        var authorizationId = await MintWithdrawalAsync(client, user, account);

        var race = ArmClosureOf(account, emptyFirst: true);

        var key = Guid.NewGuid();
        var response = await WithdrawAsync(client, user, account, authorizationId, key);
        var seen = await ObserveAsync(
            response, user, SecurityEvents.MoneyWithdrawn, authorizationId, key, race, account);

        using var scope = new AssertionScope();
        ShouldHaveClosedAtTheSave(race, seen, savesAfterwards: 0);
        ShouldBeRefused(seen, HttpStatusCode.NotFound, ErrorCodes.AccountNotFound, NotFound(account));
        ShouldHaveMovedNothing(seen, new AccountRow(true, 0m, 1));
    }

    [SqlServerFact]
    public async Task Closure_OfTheAccountEmptiedBeforeTheWithdrawalFirstReload_IsRefusedAsAClosedAccount()
    {
        var client = CreateSqlClient();
        var user = await RegisterWithPinAsync(client, "wf");
        var account = await CreateSpareAsync(client, user);
        await FundAsync(client, user, account);
        var authorizationId = await MintWithdrawalAsync(client, user, account);

        // The withdrawal checks its funds once before it reads the authorisation, on the open
        // account, and again in the attempt, on the row the reload brings back.
        var race = ArmClosureOf(account, trigger: AuthorisationRead, emptyFirst: true);

        var key = Guid.NewGuid();
        var response = await WithdrawAsync(client, user, account, authorizationId, key);
        var seen = await ObserveAsync(
            response, user, SecurityEvents.MoneyWithdrawn, authorizationId, key, race, account);

        using var scope = new AssertionScope();
        ShouldHaveClosedBeforeTheFirstReload(race, seen, savesAfterwards: 0);
        ShouldBeRefused(seen, HttpStatusCode.NotFound, ErrorCodes.AccountNotFound, NotFound(account));
        ShouldHaveMovedNothing(seen, new AccountRow(true, 0m, 1));
    }
}
