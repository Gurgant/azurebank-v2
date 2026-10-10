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
/// What the two transfers and the withdrawal answer when one of their accounts is closed: before
/// the request (the control) and while the request runs (the race).
/// </summary>
/// <remarks>
/// <para>
/// A race closes the account from a second connection on the request's own
/// <c>UPDATE [Accounts]</c>. The save then loses the account's <c>RowVersion</c>, the request
/// reloads, and the reload returns the closed row: a reload does not apply the filter that hides
/// closed accounts from every query. The request must then give the answer the control beside it
/// measures, and move nothing: no balance, no ledger row, no money audit row, and the step-up
/// authorisation it presented stays unspent.
/// </para>
/// <para>
/// The API closes only an empty account that is not the primary one
/// (<c>AccountService.RefuseIfNotClosable</c>). So it cannot close an account while that account
/// holds the money a request is about to take from it, and it cannot close a payee's primary
/// account. Those races close the row directly: they hold what the retry does with a closed row,
/// however it came to be closed. The races named "emptied and closed" leave the row the API can
/// produce for a source account, a withdrawal of everything and then a closure.
/// </para>
/// <para>
/// Gated by AZUREBANK_TEST_SQLSERVER and serialised with the other SQL proofs: the in-memory
/// provider has no <c>rowversion</c>, so no request there ever goes round its retry.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class TransferOrWithdrawalOnClosedAccountSqlServerTests : IDisposable
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private const string Pin = "123456";
    private const string Password = "TestPass123!";
    private const decimal Funding = 20m;
    private const decimal Amount = 5m;

    /// <summary>The read of the authorisation: after the request looked at its accounts, before it reloads them.</summary>
    private const string AuthorisationRead = "FROM [StepUpAuthorizations]";

    private const string NoActiveAccount = "Recipient does not have an active account.";

    private readonly ITestOutputHelper _output;
    private CustomWebApplicationFactory? _factory;

    public TransferOrWithdrawalOnClosedAccountSqlServerTests(ITestOutputHelper output)
    {
        _output = output;
    }

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
        ShouldHaveFired(race);
        ShouldBeRefused(seen, HttpStatusCode.UnprocessableEntity, ErrorCodes.RecipientNoAccount, NoActiveAccount);
        ShouldHaveMovedNothing(seen, new AccountRow(false, Funding, 1), new AccountRow(true, 0m, 0));
    }

    [SqlServerFact]
    public async Task Collision_TransferWhosePayeeAccountClosesBeforeItsFirstReload_IsRefused_AndMovesNothing()
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
        ShouldHaveFired(race);
        ShouldBeRefused(seen, HttpStatusCode.UnprocessableEntity, ErrorCodes.RecipientNoAccount, NoActiveAccount);
        ShouldHaveMovedNothing(seen, new AccountRow(false, Funding, 1), new AccountRow(true, 0m, 0));
    }

    [SqlServerFact]
    public async Task Collision_TransferRacingThePayeeAccountClosure_WhenThePayeeHoldsAnotherAccount_IsRefused_AndSentAgainPaysTheOpenOne()
    {
        var client = CreateSqlClient();
        var sender = await RegisterWithPinAsync(client, "xdo");
        var payee = await RegisterAsync(client, "xdop");
        var payeeSpare = await CreateSpareAsync(client, payee);
        await FundAsync(client, sender, sender.PrimaryAccountId);
        var authorizationId = await MintTransferAsync(client, sender, sender.PrimaryAccountId, payee.AzureTag);

        var race = ArmClosureOf(payee.PrimaryAccountId);

        var key = Guid.NewGuid();
        var response = await TransferAsync(
            client, sender, sender.PrimaryAccountId, payee.AzureTag, authorizationId, key);
        var seen = await ObserveAsync(
            response, sender, SecurityEvents.MoneyTransferred, authorizationId, key, race,
            sender.PrimaryAccountId, payee.PrimaryAccountId, payeeSpare);

        /*
          The retry does not choose another account for the payee: it refuses. The refusal spends
          nothing and releases the key, so the same request sent again starts from the top, where
          the payee's open account is the one a first look picks.
        */
        var again = await TransferAsync(
            client, sender, sender.PrimaryAccountId, payee.AzureTag, authorizationId, key);
        var after = await ObserveAsync(
            again, sender, SecurityEvents.MoneyTransferred, authorizationId, key, race: null,
            sender.PrimaryAccountId, payee.PrimaryAccountId, payeeSpare);

        using var scope = new AssertionScope();
        ShouldHaveFired(race);
        ShouldBeRefused(seen, HttpStatusCode.UnprocessableEntity, ErrorCodes.RecipientNoAccount, NoActiveAccount);
        ShouldHaveMovedNothing(
            seen, new AccountRow(false, Funding, 1), new AccountRow(true, 0m, 0), new AccountRow(false, 0m, 0));

        after.Status.Should().Be(HttpStatusCode.Created, "the same request, sent again, is a new execution");
        after.Replayed.Should().BeFalse("the refusal stored no answer to replay");
        after.Accounts.Should().Equal(
            new AccountRow(false, Funding - Amount, 2),
            new AccountRow(true, 0m, 0),
            new AccountRow(false, Amount, 1));
        after.MoneyAuditRows.Should().Be(1);
        after.Authorization.Should().Be(StepUpAuthorizationStatus.Consumed);
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
        ShouldHaveFired(race);
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
        ShouldHaveFired(race);
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
        ShouldHaveFired(race);
        ShouldBeRefused(seen, HttpStatusCode.NotFound, ErrorCodes.AccountNotFound, NotFound(destination));
        ShouldHaveMovedNothing(seen, new AccountRow(false, Funding, 1), new AccountRow(true, 0m, 0));
    }

    [SqlServerFact]
    public async Task Collision_InternalTransferWhoseDestinationClosesBeforeItsFirstReload_IsRefused_AndMovesNothing()
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
        ShouldHaveFired(race);
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
        ShouldHaveFired(race);
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
        ShouldHaveFired(race);
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
        ShouldHaveFired(race);
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
        ShouldHaveFired(race);
        ShouldBeRefused(seen, HttpStatusCode.NotFound, ErrorCodes.AccountNotFound, NotFound(account));
        ShouldHaveMovedNothing(seen, new AccountRow(true, 0m, 1));
    }

    // ─────────────────────────────────────────────────────────────────────────

    private sealed record TestUser(string Token, Guid UserId, Guid PrimaryAccountId, string AzureTag);

    /// <summary>One account as the database holds it, closed or not, with its ledger rows counted.</summary>
    private sealed record AccountRow(bool IsDeleted, decimal Balance, int TransactionRows);

    private sealed record Observed(
        HttpStatusCode Status, string? ErrorCode, string? Detail, bool Replayed,
        IReadOnlyList<AccountRow> Accounts, int MoneyAuditRows,
        StepUpAuthorizationStatus Authorization, DateTime? ConsumedAt, int IdempotencyRows,
        bool ChainIntact, string? ChainReason);

    private static string NotFound(Guid accountId) => $"Account with identifier '{accountId}' was not found.";

    private static void ShouldHaveFired(OutOfBandClosureInterceptor race)
    {
        race.Fired.Should().BeTrue("the out-of-band closure must actually have run");
        race.OutOfBandRowsAffected.Should().Be(1, "and it must have closed an account that was still open");
    }

    private static void ShouldBeRefused(Observed seen, HttpStatusCode status, string errorCode, string detail)
    {
        seen.Status.Should().Be(status, "a closed account gets one answer, whenever it closed");
        seen.ErrorCode.Should().Be(errorCode);
        seen.Detail.Should().Be(detail);
        seen.Replayed.Should().BeFalse();
    }

    private static void ShouldHaveMovedNothing(Observed seen, params AccountRow[] accounts)
    {
        seen.Accounts.Should().Equal(accounts, "no balance moves and no ledger row is written");
        seen.MoneyAuditRows.Should().Be(0, "a movement that did not happen leaves no money audit row");
        seen.Authorization.Should().Be(StepUpAuthorizationStatus.Pending, "a refusal spends no authorisation");
        seen.ConsumedAt.Should().BeNull();
        seen.IdempotencyRows.Should().Be(0, "a refusal before the commit releases the key");
        seen.ChainIntact.Should().BeTrue(because: seen.ChainReason);
    }

    /// <summary>
    /// Reads the answer and then the database, in a scope of its own so nothing comes from a
    /// tracker, and writes both to the test output before anything is asserted.
    /// </summary>
    private async Task<Observed> ObserveAsync(
        HttpResponseMessage response, TestUser actor, string moneyEvent, Guid authorizationId,
        Guid idempotencyKey, OutOfBandClosureInterceptor? race, params Guid[] accountIds)
    {
        var body = await response.Content.ReadAsStringAsync();
        string? errorCode = null;
        string? detail = null;
        if (!response.IsSuccessStatusCode)
        {
            var problem = JsonSerializer.Deserialize<JsonElement>(body);
            errorCode = problem.TryGetProperty("errorCode", out var code) ? code.GetString() : null;
            detail = problem.TryGetProperty("detail", out var text) ? text.GetString() : null;
        }

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var chain = scope.ServiceProvider.GetRequiredService<IAuditChain>();

        var accounts = new List<AccountRow>();
        foreach (var accountId in accountIds)
        {
            var account = await db.Accounts.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == accountId);
            var rows = await db.Transactions.IgnoreQueryFilters().AsNoTracking()
                .CountAsync(t => t.AccountId == accountId);
            accounts.Add(new AccountRow(account.IsDeleted, account.Balance, rows));
        }

        var moneyAuditRows = await db.AuditEvents.AsNoTracking()
            .CountAsync(e => e.Event == moneyEvent && e.ActorUserId == actor.UserId);
        var authorization = await db.StepUpAuthorizations.AsNoTracking().SingleAsync(a => a.Id == authorizationId);
        var idempotencyRows = await db.IdempotencyRecords.AsNoTracking()
            .CountAsync(r => r.UserId == actor.UserId && r.Key == idempotencyKey);
        var verification = await chain.VerifyAsync(db);

        var seen = new Observed(
            response.StatusCode, errorCode, detail,
            response.Headers.Contains(IdempotencyConstants.ReplayedHeaderName),
            accounts, moneyAuditRows, authorization.Status, authorization.ConsumedAt, idempotencyRows,
            verification.IsIntact, verification.Reason);

        _output.WriteLine(
            $"status: {(int)seen.Status}, errorCode: {seen.ErrorCode ?? "none"}, "
            + (race is null
                ? "race: none, "
                : $"race.Fired: {race.Fired}, race.OutOfBandRowsAffected: {race.OutOfBandRowsAffected}, ")
            + "accounts: "
            + string.Join(" ", accounts.Select(
                a => $"[IsDeleted: {a.IsDeleted}, balance: {a.Balance}, transaction rows: {a.TransactionRows}]"))
            + $", {moneyEvent} audit rows: {seen.MoneyAuditRows}, authorisation: {seen.Authorization}, "
            + $"idempotency rows: {seen.IdempotencyRows}, chain intact: {seen.ChainIntact}");

        return seen;
    }

    private HttpClient CreateSqlClient()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        return _factory.CreateClient();
    }

    /// <summary>Registered now and armed now, so every write of the setup is behind it.</summary>
    private OutOfBandClosureInterceptor ArmClosureOf(
        Guid accountId, string trigger = "UPDATE [Accounts]", bool emptyFirst = false)
    {
        var race = new OutOfBandClosureInterceptor(
            SqlServerFactAttribute.ConnectionString!, accountId, trigger, emptyFirst);
        _factory!.AddInterceptor(race);
        race.Arm();
        return race;
    }

    private static async Task<TestUser> RegisterAsync(HttpClient client, string prefix)
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var tag = $"{prefix}_{unique}";
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = tag,
            Email = $"{prefix}{unique}@example.com",
            Password = Password,
            FirstName = "Closed",
            LastName = "Account"
        }, Json);
        response.EnsureSuccessStatusCode();
        var registered = await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json);
        return new TestUser(
            registered!.Data!.Token.AccessToken, registered.Data.User.Id, registered.Data.Account.Id, tag);
    }

    private static async Task<TestUser> RegisterWithPinAsync(HttpClient client, string prefix)
    {
        var user = await RegisterAsync(client, prefix);
        (await SendAsync(client, user, HttpMethod.Post, "/api/auth/pin",
            new SetPinRequest { Pin = Pin, Password = Password })).EnsureSuccessStatusCode();
        return user;
    }

    private static async Task<Guid> CreateSpareAsync(HttpClient client, TestUser user)
    {
        var response = await SendAsync(client, user, HttpMethod.Post, "/api/accounts",
            new CreateAccountRequest { Name = "Spare", Type = AccountType.Savings });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ApiResponse<AccountResponse>>(Json))!.Data!.Id;
    }

    private static async Task FundAsync(HttpClient client, TestUser user, Guid accountId)
    {
        (await SendAsync(client, user, HttpMethod.Post, "/api/transactions/deposit",
            new DepositRequest { AccountId = accountId, Amount = Funding, Description = "funding" },
            idempotencyKey: Guid.NewGuid())).EnsureSuccessStatusCode();
    }

    /// <summary>The bank's own closure: the PIN mints an authorisation and the DELETE spends it.</summary>
    private static async Task CloseThroughTheApiAsync(HttpClient client, TestUser user, Guid accountId)
    {
        var authorizationId = await MintAsync(
            client, user, $"/api/accounts/{accountId}/deletion-authorizations",
            new AccountDeletionAuthorizationRequest { Pin = Pin });
        var response = await SendAsync<object?>(
            client, user, HttpMethod.Delete, $"/api/accounts/{accountId}", null, authorizationId);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the control's account must really be closed");
    }

    private static Task<Guid> MintTransferAsync(HttpClient client, TestUser user, Guid fromAccountId, string recipientTag) =>
        MintAsync(client, user, "/api/transfers/authorizations", new TransferAuthorizationRequest
        {
            FromAccountId = fromAccountId,
            RecipientAzureTag = recipientTag,
            Amount = Amount,
            Pin = Pin
        });

    private static Task<Guid> MintInternalAsync(HttpClient client, TestUser user, Guid fromAccountId, Guid toAccountId) =>
        MintAsync(client, user, "/api/transfers/internal/authorizations", new InternalTransferAuthorizationRequest
        {
            FromAccountId = fromAccountId,
            ToAccountId = toAccountId,
            Amount = Amount,
            Pin = Pin
        });

    private static Task<Guid> MintWithdrawalAsync(HttpClient client, TestUser user, Guid accountId) =>
        MintAsync(client, user, "/api/transactions/withdraw/authorizations",
            new WithdrawalAuthorizationRequest { AccountId = accountId, Amount = Amount, Pin = Pin });

    private static async Task<Guid> MintAsync<T>(HttpClient client, TestUser user, string url, T payload)
    {
        var response = await SendAsync(client, user, HttpMethod.Post, url, payload);
        response.EnsureSuccessStatusCode();
        return (await response.Content
            .ReadFromJsonAsync<ApiResponse<StepUpAuthorizationResponse>>(Json))!.Data!.AuthorizationId;
    }

    private static Task<HttpResponseMessage> TransferAsync(
        HttpClient client, TestUser user, Guid fromAccountId, string recipientTag,
        Guid authorizationId, Guid idempotencyKey) =>
        SendAsync(client, user, HttpMethod.Post, "/api/transfers", new TransferRequest
        {
            FromAccountId = fromAccountId,
            RecipientAzureTag = recipientTag,
            Amount = Amount,
            Description = "closed account proof"
        }, authorizationId, idempotencyKey);

    private static Task<HttpResponseMessage> InternalTransferAsync(
        HttpClient client, TestUser user, Guid fromAccountId, Guid toAccountId,
        Guid authorizationId, Guid idempotencyKey) =>
        SendAsync(client, user, HttpMethod.Post, "/api/transfers/internal", new InternalTransferRequest
        {
            FromAccountId = fromAccountId,
            ToAccountId = toAccountId,
            Amount = Amount,
            Description = "closed account proof"
        }, authorizationId, idempotencyKey);

    private static Task<HttpResponseMessage> WithdrawAsync(
        HttpClient client, TestUser user, Guid accountId, Guid authorizationId, Guid idempotencyKey) =>
        SendAsync(client, user, HttpMethod.Post, "/api/transactions/withdraw", new WithdrawRequest
        {
            AccountId = accountId,
            Amount = Amount,
            Description = "closed account proof"
        }, authorizationId, idempotencyKey);

    private static async Task<HttpResponseMessage> SendAsync<T>(
        HttpClient client, TestUser user, HttpMethod method, string url, T payload,
        Guid? authorizationId = null, Guid? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload, options: Json);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
        if (authorizationId is { } authorization)
        {
            request.Headers.Add(StepUpConstants.HeaderName, authorization.ToString());
        }

        if (idempotencyKey is { } key)
        {
            request.Headers.Add(IdempotencyConstants.HeaderName, key.ToString());
        }

        return await client.SendAsync(request);
    }

    public void Dispose()
    {
        _factory?.Dispose();
    }
}
