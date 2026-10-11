using System.Diagnostics;
using System.Net;
using System.Text.Json;
using AzureBank.Api.Services;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Transaction;
using AzureBank.Shared.DTOs.Transfer;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Utilities;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// A withdrawal, an external transfer and an internal transfer that lose the race for an account's
/// row on every attempt they are given, on SQL Server: each answers the busy 503 a deposit
/// answers, by the deposit's rule, writes nothing, leaves its authorisation unspent and its key
/// free, and is made when the same request is sent again.
/// </summary>
/// <remarks>
/// <para>
/// BUSY ONLY WHEN NOTHING AN ATTEMPT LOOKS AT REFUSES THE WRITE. Six proofs change one of those
/// things as the last attempt loses, and none is answered the busy 503. Five change what an
/// attempt looks at before its save, and are answered what an attempt answers: the caller's own
/// account closes, the payee's only account closes, a destination's row goes, another write takes
/// the funds, another transfer fills the day. The sixth changes the authorisation, which the
/// validation that runs before the first attempt reads again, and is answered that validation's
/// 401: <c>AUTHORIZATION_INVALID</c> for one another request spent, the code an attempt's spend
/// gives, and <c>AUTHORIZATION_EXPIRED</c> for one that expired, where an attempt's spend gives
/// <c>AUTHORIZATION_INVALID</c>.
/// </para>
/// <para>
/// The other proofs, counted. One closes the payee's account while the payee holds another open
/// one: the transfer is still busy, and pays the other account when it is sent again. Three are
/// about the claim and the commit: a claim taken over and a commit let start that failed are
/// answered 500, and a commit that landed and lost its acknowledgement is answered the 409 of a
/// write that was applied, before the busy question is asked. Two send 24 writes at once to one
/// account. Not held: a payee's handle renamed during the attempts, since a handle is resolved
/// once per request and not looked at again.
/// </para>
/// <para>
/// A transfer saves two accounts, so each of its proofs says which of the two is lost
/// (<see cref="Write"/>): the race is for that account's row alone.
/// </para>
/// <para>
/// Each proof writes what it measured to the test output before it asserts, and asserts that its
/// fixtures fired: a race that did not run on every attempt proves nothing.
/// <c>BalanceConcurrencySqlServerTests</c> holds the same for a deposit.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class BusyMoneyWriteSqlServerTests(ITestOutputHelper output) : ClosedAccountSqlServerProofs(output)
{
    private const string AppliedDetail =
        "The operation sent with this idempotency key was applied, but this request cannot return "
        + "its result. Do not send it again with a new key: look for it with GET /api/transactions.";

    private static readonly AccountRow Funded = new(false, Funding, 1);
    private static readonly AccountRow Debited = new(false, Funding - Amount, 2);
    private static readonly AccountRow Empty = new(false, 0m, 0);
    private static readonly AccountRow Credited = new(false, Amount, 1);

    /// <summary>What a debit that wins the last attempt leaves: less than the amount asked for.</summary>
    private const decimal LeftByTheDebit = Amount - 1m;

    private readonly ITestOutputHelper _output = output;

    /// <summary>A keyed money write, and the account whose row it loses on every attempt.</summary>
    public enum Write
    {
        Withdrawal,
        TransferLosingItsSource,
        TransferLosingThePayeeAccount,
        InternalTransferLosingItsSource,
        InternalTransferLosingItsDestination,
    }

    [SqlServerTheory]
    [InlineData(Write.Withdrawal)]
    [InlineData(Write.TransferLosingItsSource)]
    [InlineData(Write.TransferLosingThePayeeAccount)]
    [InlineData(Write.InternalTransferLosingItsSource)]
    [InlineData(Write.InternalTransferLosingItsDestination)]
    public async Task AWriteThatLosesEveryAttempt_AnswersTheBusy503_ChangesNothing_AndIsMadeWhenSentAgain(Write write)
    {
        var race = new LostRowVersionRaceInterceptor(SqlServerFactAttribute.ConnectionString!);
        var refused = new RefusedEntriesInterceptor();
        var client = CreateSqlClient(host =>
        {
            host.AddInterceptor(race);
            host.AddInterceptor(refused);
            host.CaptureLog();
        });
        var scene = await SetUpAsync(client, write, "bz");
        var authorizationId = await scene.MintAsync();
        var key = Guid.NewGuid();

        race.Arm(scene.Lost);
        using var response = await scene.SendAsync(authorizationId, key);
        race.Disarm();
        var seen = await ObserveAsync(
            response, scene.Actor, scene.MoneyEvent, authorizationId, key, race: null, scene.Accounts);
        var usedToday = await UsedTodayAsync(scene.Actor);

        // The 503 says to send the request again: the same key, the same bytes, the same authorisation.
        using var sentAgain = await scene.SendAsync(authorizationId, key);
        var again = await ObserveAsync(
            sentAgain, scene.Actor, scene.MoneyEvent, authorizationId, key, race: null, scene.Accounts);
        var usedTodayAfterwards = await UsedTodayAsync(scene.Actor);

        _output.WriteLine(
            $"{write}: fired {race.Fired} of {ConcurrencyRetry.MaxAttempts} attempts; "
            + $"the conflicts name: [{string.Join(", ", refused.Conflicts)}]; "
            + $"Retry-After {response.Headers.RetryAfter?.Delta?.ToString() ?? "none"}; "
            + $"external transfers counted today: {usedToday}, after the same request again: {usedTodayAfterwards}");
        WriteTheLog();

        using var scope = new AssertionScope();
        race.Fired.Should().Be(
            ConcurrencyRetry.MaxAttempts, "the proof is void unless the write lost every attempt it is given");
        refused.Conflicts.Should().HaveCount(ConcurrencyRetry.MaxAttempts, "one conflict for each attempt, and no other");

        ShouldBeTheBusy503(response, seen.Body, saysNothingWasApplied: true);
        seen.Replayed.Should().BeFalse();
        ShouldHaveMovedNothing(seen, scene.Accounts.Select((_, i) => i == 0 ? Funded : Empty).ToArray());
        usedToday.Should().Be(0m, "a transfer answered 503 counts for nothing toward the day's ceiling");

        again.Status.Should().Be(
            HttpStatusCode.Created, $"the key of a write answered 503 is free; the body was: {again.Body}");
        again.Replayed.Should().BeFalse("no earlier answer exists to replay");
        again.Accounts.Should().Equal(scene.Accounts.Select((_, i) => i == 0 ? Debited : Credited).ToArray());
        again.MoneyAuditRows.Should().Be(1, "the write that was made has its one audit row");
        again.Authorization.Should().Be(StepUpAuthorizationStatus.Consumed, "the authorisation is spent by the write that was made");
        again.ConsumedAt.Should().NotBeNull();
        again.IdempotencyRecords.Should().Equal([IdempotencyStatus.Completed]);
        again.ChainIntact.Should().BeTrue(because: again.ChainReason);
        usedTodayAfterwards.Should().Be(
            scene.CountsTowardTheDay ? Amount : 0m, "an external transfer that was made counts once");
    }

    /// <summary>
    /// A write that loses its account on every attempt, and whose claim is taken over before its
    /// last save, is not answered as a busy account: the key is another request's by then.
    /// </summary>
    [SqlServerTheory]
    [InlineData(Write.Withdrawal)]
    [InlineData(Write.TransferLosingItsSource)]
    [InlineData(Write.InternalTransferLosingItsSource)]
    public async Task AWriteThatLosesItsLastAttempt_AndWhoseClaimWasTakenOver_IsNotAnsweredBusy(Write write)
    {
        var race = new LostRowVersionRaceInterceptor(SqlServerFactAttribute.ConnectionString!);
        var fence = new FencedOutClaimInterceptor(SqlServerFactAttribute.ConnectionString!);
        var refused = new RefusedEntriesInterceptor();
        var client = CreateSqlClient(host =>
        {
            host.AddInterceptor(race);
            host.AddInterceptor(fence);
            host.AddInterceptor(refused);
            host.CaptureLog();
        });
        var scene = await SetUpAsync(client, write, "bt");
        var authorizationId = await scene.MintAsync();
        var key = Guid.NewGuid();

        // One save loses both rows: the account's, as every save of this write does, and on the
        // last attempt the record's too, taken over from a second connection before the batch runs.
        race.Arm(scene.Lost);
        fence.Arm(key, onSave: ConcurrencyRetry.MaxAttempts);
        using var response = await scene.SendAsync(authorizationId, key);
        race.Disarm();
        fence.Disarm();
        var seen = await ObserveAsync(
            response, scene.Actor, scene.MoneyEvent, authorizationId, key, race: null, scene.Accounts);
        var claims = await ReadClaimsAsync(key);

        _output.WriteLine(
            $"{write}: account lost on {race.Fired} saves, claim taken over {fence.Fired} time(s) before save {fence.Saves}; "
            + $"the conflicts name: [{string.Join(", ", refused.Conflicts)}]; "
            + $"Retry-After {response.Headers.RetryAfter?.Delta?.ToString() ?? "none"}; "
            + $"records under the key: [{string.Join(", ", claims.Select(c => $"{c.Status} held by {(c.ClaimId == fence.TakenBy ? "the other request" : "another claim")}"))}]");
        WriteTheLog();

        using var scope = new AssertionScope();
        race.Fired.Should().Be(ConcurrencyRetry.MaxAttempts, "the proof is void unless the write lost every attempt it is given");
        fence.Fired.Should().Be(1, "and unless its claim was taken over");
        fence.Saves.Should().Be(ConcurrencyRetry.MaxAttempts, "before its last save, and no save followed");
        refused.Conflicts.Should().Equal(
            Enumerable.Repeat("Account", ConcurrencyRetry.MaxAttempts),
            "and unless every conflict names the account alone: only then does the last one reach the read of the record");

        ShouldBeAFaultThatInvitesNoRetry(response, seen);
        seen.Accounts.Should().Equal(scene.Accounts.Select((_, i) => i == 0 ? Funded : Empty).ToArray());
        seen.MoneyAuditRows.Should().Be(0);
        seen.Authorization.Should().Be(StepUpAuthorizationStatus.Pending);
        seen.ChainIntact.Should().BeTrue(because: seen.ChainReason);
        claims.Should().Equal(
            [(IdempotencyStatus.Processing, fence.TakenBy)],
            "the other request's record stays as it was: the release leaves a claim that is not its own");
    }

    /// <summary>
    /// A write that loses its account on every attempt but the last, commits on the last and loses
    /// the commit's acknowledgement: the attempt that runs again reads its record as committed and
    /// answers that the write was applied, answered before the busy question is asked. No conflict
    /// reaches the last attempt, so the code that answers a busy account does not run: it is not a
    /// busy account, and the money moved once.
    /// </summary>
    [SqlServerTheory]
    [InlineData(Write.Withdrawal)]
    [InlineData(Write.TransferLosingItsSource)]
    [InlineData(Write.InternalTransferLosingItsSource)]
    public async Task AWriteWhoseLastAttemptLanded_AndLostItsAcknowledgement_IsNotAnsweredBusy(Write write)
    {
        var fault = new TransferTransientFault(TransferFaultMode.AfterCommit);
        var race = new LostRowVersionRaceInterceptor(SqlServerFactAttribute.ConnectionString!);
        var refused = new RefusedEntriesInterceptor();
        var client = CreateSqlClient(fault);
        AddInterceptor(race);
        AddInterceptor(refused);
        var scene = await SetUpAsync(client, write, "bl");
        var authorizationId = await scene.MintAsync();
        var key = Guid.NewGuid();

        // Armed only now: the funding deposit also inserts a ledger row.
        fault.Arm();
        race.Arm(scene.Lost, saves: ConcurrencyRetry.MaxAttempts - 1);
        using var response = await scene.SendAsync(authorizationId, key);
        race.Disarm();
        var seen = await ObserveAsync(
            response, scene.Actor, scene.MoneyEvent, authorizationId, key, race: null, scene.Accounts);

        _output.WriteLine(
            $"{write}: account lost on {race.Fired} saves, then a commit whose acknowledgement was lost: {fault.Fired}; "
            + $"the conflicts name: [{string.Join(", ", refused.Conflicts)}]; "
            + $"Retry-After {response.Headers.RetryAfter?.Delta?.ToString() ?? "none"}");

        using var scope = new AssertionScope();
        race.Fired.Should().Be(
            ConcurrencyRetry.MaxAttempts - 1, "the proof is void unless the commit is the last attempt's");
        fault.Fired.Should().BeTrue("and unless its acknowledgement was lost");
        refused.Conflicts.Should().Equal(
            Enumerable.Repeat("Account", ConcurrencyRetry.MaxAttempts - 1),
            "and unless each save before it lost the account alone, and the save that landed lost nothing");

        seen.Status.Should().Be(
            HttpStatusCode.Conflict, $"a write that is made is not to be sent again as if the account were busy; the body was: {seen.Body}");
        seen.ErrorCode.Should().Be(ErrorCodes.IdempotencyResultUnknown);
        seen.Detail.Should().Be(AppliedDetail);
        response.Headers.RetryAfter.Should().BeNull("nothing invites the caller to send it again");
        var problem = JsonSerializer.Deserialize<JsonElement>(seen.Body);
        problem.TryGetProperty("applied", out var applied).Should().BeTrue("the claim was read as committed");
        applied.ValueKind.Should().Be(JsonValueKind.True);

        seen.Accounts.Should().Equal(
            scene.Accounts.Select((_, i) => i == 0 ? Debited : Credited).ToArray(), "the money moved once");
        seen.MoneyAuditRows.Should().Be(1);
        seen.Authorization.Should().Be(StepUpAuthorizationStatus.Consumed);
        seen.IdempotencyRecords.Should().Equal([IdempotencyStatus.Executed], "the record rode the commit that landed, and is kept");
        seen.ChainIntact.Should().BeTrue(because: seen.ChainReason);
    }

    /// <summary>
    /// The caller's own account closes as the write loses its last attempt: the write that won is
    /// a closure, and a closed account is not a busy one. The answer is the 404 of an account
    /// closed before the request.
    /// </summary>
    [SqlServerTheory]
    [InlineData(Write.Withdrawal)]
    [InlineData(Write.TransferLosingItsSource)]
    [InlineData(Write.InternalTransferLosingItsSource)]
    [InlineData(Write.InternalTransferLosingItsDestination)]
    public async Task AWriteWhoseOwnAccountClosesAsItLosesItsLastAttempt_AnswersNotFound_NotBusy(Write write)
    {
        var race = new LostRowVersionRaceInterceptor(SqlServerFactAttribute.ConnectionString!);
        var client = CreateSqlClient(host => host.AddInterceptor(race));
        var scene = await SetUpAsync(client, write, "bc");
        var authorizationId = await scene.MintAsync();
        var key = Guid.NewGuid();

        race.Arm(scene.Lost, closingOn: ConcurrencyRetry.MaxAttempts);
        using var response = await scene.SendAsync(authorizationId, key);
        race.Disarm();
        var seen = await ObserveAsync(
            response, scene.Actor, scene.MoneyEvent, authorizationId, key, race: null, scene.Accounts);
        _output.WriteLine($"{write}: fired {race.Fired} of {ConcurrencyRetry.MaxAttempts} attempts, the last a closure: {race.Closed}");

        using var scope = new AssertionScope();
        race.Fired.Should().Be(ConcurrencyRetry.MaxAttempts, "the proof is void unless the write lost every attempt it is given");
        race.Closed.Should().BeTrue("and unless the write that won the last one closed the account");
        ShouldBeRefused(seen, HttpStatusCode.NotFound, ErrorCodes.AccountNotFound, NotFound(scene.Lost));
        response.Headers.RetryAfter.Should().BeNull("a closed account is not one to come back to");
        ShouldHaveMovedNothing(seen, scene.Accounts
            .Select((id, i) => (i == 0 ? Funded : Empty) with { IsDeleted = id == scene.Lost })
            .ToArray());
    }

    /// <summary>
    /// The payee's account closes as an external transfer loses its last attempt, and the payee
    /// has another open account: the transfer is busy, since the same request sent again chooses
    /// that account, and sent again it pays into it.
    /// </summary>
    [SqlServerFact]
    public async Task ATransferWhosePayeeAccountClosesAsItLosesItsLastAttempt_IsBusy_AndPaysAnotherOpenAccountWhenSentAgain()
    {
        var race = new LostRowVersionRaceInterceptor(SqlServerFactAttribute.ConnectionString!);
        var client = CreateSqlClient(host => host.AddInterceptor(race));
        var sender = await RegisterWithPinAsync(client, "bp");
        var payee = await RegisterAsync(client, "bpp");
        var other = await CreateSpareAsync(client, payee);
        await FundAsync(client, sender, sender.PrimaryAccountId);
        var authorizationId = await MintTransferAsync(client, sender, sender.PrimaryAccountId, payee.AzureTag);
        var key = Guid.NewGuid();
        Guid[] accounts = [sender.PrimaryAccountId, payee.PrimaryAccountId, other];

        race.Arm(payee.PrimaryAccountId, closingOn: ConcurrencyRetry.MaxAttempts);
        using var response = await TransferAsync(client, sender, sender.PrimaryAccountId, payee.AzureTag, authorizationId, key);
        race.Disarm();
        var seen = await ObserveAsync(
            response, sender, SecurityEvents.MoneyTransferred, authorizationId, key, race: null, accounts);

        using var sentAgain = await TransferAsync(client, sender, sender.PrimaryAccountId, payee.AzureTag, authorizationId, key);
        var again = await ObserveAsync(
            sentAgain, sender, SecurityEvents.MoneyTransferred, authorizationId, key, race: null, accounts);
        _output.WriteLine($"fired {race.Fired} of {ConcurrencyRetry.MaxAttempts} attempts, the last a closure: {race.Closed}");

        using var scope = new AssertionScope();
        race.Fired.Should().Be(ConcurrencyRetry.MaxAttempts, "the proof is void unless the transfer lost every attempt it is given");
        race.Closed.Should().BeTrue("and unless the write that won the last one closed the payee's account");
        ShouldBeTheBusy503(response, seen.Body, saysNothingWasApplied: true);
        ShouldHaveMovedNothing(seen, Funded, Empty with { IsDeleted = true }, Empty);

        again.Status.Should().Be(HttpStatusCode.Created, $"the payee has an open account to pay; the body was: {again.Body}");
        again.Replayed.Should().BeFalse();
        again.Accounts.Should().Equal(Debited, Empty with { IsDeleted = true }, Credited);
        again.MoneyAuditRows.Should().Be(1);
        again.Authorization.Should().Be(StepUpAuthorizationStatus.Consumed);
        again.ChainIntact.Should().BeTrue(because: again.ChainReason);
    }

    /// <summary>
    /// The payee's only account closes as an external transfer loses its last attempt: no account
    /// is left to pay, and the answer is the 422 of a payee with no account, not the busy 503.
    /// </summary>
    [SqlServerFact]
    public async Task ATransferWhosePayeeOnlyAccountClosesAsItLosesItsLastAttempt_AnswersNoAccount_NotBusy()
    {
        var race = new LostRowVersionRaceInterceptor(SqlServerFactAttribute.ConnectionString!);
        var client = CreateSqlClient(host => host.AddInterceptor(race));
        var scene = await SetUpAsync(client, Write.TransferLosingThePayeeAccount, "bn");
        var authorizationId = await scene.MintAsync();
        var key = Guid.NewGuid();

        race.Arm(scene.Lost, closingOn: ConcurrencyRetry.MaxAttempts);
        using var response = await scene.SendAsync(authorizationId, key);
        race.Disarm();
        var seen = await ObserveAsync(
            response, scene.Actor, scene.MoneyEvent, authorizationId, key, race: null, scene.Accounts);
        _output.WriteLine($"fired {race.Fired} of {ConcurrencyRetry.MaxAttempts} attempts, the last a closure: {race.Closed}");

        using var scope = new AssertionScope();
        race.Fired.Should().Be(ConcurrencyRetry.MaxAttempts, "the proof is void unless the transfer lost every attempt it is given");
        race.Closed.Should().BeTrue("and unless the write that won the last one closed the payee's account");
        ShouldBeRefused(seen, HttpStatusCode.UnprocessableEntity, ErrorCodes.RecipientNoAccount, NoActiveAccount);
        response.Headers.RetryAfter.Should().BeNull();
        ShouldHaveMovedNothing(seen, Funded, Empty with { IsDeleted = true });
    }

    /// <summary>
    /// Another write takes money from the debited account as the write loses its last attempt, and
    /// what it leaves no longer covers the amount: the answer is the 422 every attempt gives an
    /// account that holds too little, not the busy 503.
    /// </summary>
    [SqlServerTheory]
    [InlineData(Write.Withdrawal)]
    [InlineData(Write.TransferLosingItsSource)]
    [InlineData(Write.InternalTransferLosingItsSource)]
    public async Task AWriteWhoseAccountIsDebitedAsItLosesItsLastAttempt_AnswersInsufficientFunds_NotBusy(Write write)
    {
        var race = new LostRowVersionRaceInterceptor(SqlServerFactAttribute.ConnectionString!);
        var client = CreateSqlClient(host => host.AddInterceptor(race));
        var scene = await SetUpAsync(client, write, "bd");
        var authorizationId = await scene.MintAsync();
        var key = Guid.NewGuid();

        race.Arm(scene.Lost, debitingOn: ConcurrencyRetry.MaxAttempts, debit: Funding - LeftByTheDebit);
        using var response = await scene.SendAsync(authorizationId, key);
        race.Disarm();
        var seen = await ObserveAsync(
            response, scene.Actor, scene.MoneyEvent, authorizationId, key, race: null, scene.Accounts);
        _output.WriteLine(
            $"{write}: fired {race.Fired} of {ConcurrencyRetry.MaxAttempts} attempts, the last a debit that left "
            + $"{LeftByTheDebit} for an amount of {Amount}: {race.Debited}; answered {(int)seen.Status} {seen.ErrorCode}; "
            + $"Retry-After {response.Headers.RetryAfter?.Delta?.ToString() ?? "none"}");

        using var scope = new AssertionScope();
        race.Fired.Should().Be(ConcurrencyRetry.MaxAttempts, "the proof is void unless the write lost every attempt it is given");
        race.Debited.Should().BeTrue("and unless the write that won the last one took the money");
        seen.Status.Should().Be(
            HttpStatusCode.UnprocessableEntity,
            $"an account that no longer covers the amount is not one to come back to; the body was: {seen.Body}");
        seen.ErrorCode.Should().Be(ErrorCodes.InsufficientFunds);
        response.Headers.RetryAfter.Should().BeNull("nothing invites the caller to send it again");
        ShouldHaveMovedNothing(seen, scene.Accounts
            .Select((_, i) => i == 0 ? Funded with { Balance = LeftByTheDebit } : Empty)
            .ToArray());
    }

    /// <summary>
    /// The authorisation stops being good as the write loses its last attempt: another request
    /// spends it, or it expires. The answer is the 401 the authorisation gets, not the busy 503.
    /// </summary>
    [SqlServerTheory]
    [InlineData(Write.Withdrawal, false)]
    [InlineData(Write.TransferLosingItsSource, false)]
    [InlineData(Write.InternalTransferLosingItsSource, false)]
    [InlineData(Write.Withdrawal, true)]
    [InlineData(Write.TransferLosingItsSource, true)]
    [InlineData(Write.InternalTransferLosingItsSource, true)]
    public async Task AWriteWhoseAuthorisationIsSpentOrExpiresAsItLosesItsLastAttempt_AnswersThe401_NotBusy(
        Write write, bool expires)
    {
        var race = new LostRowVersionRaceInterceptor(SqlServerFactAttribute.ConnectionString!);
        var client = CreateSqlClient(host => host.AddInterceptor(race));
        var scene = await SetUpAsync(client, write, expires ? "bx" : "bs");
        var authorizationId = await scene.MintAsync();
        var key = Guid.NewGuid();
        var rows = 0;

        race.Arm(
            scene.Lost, meanwhileOn: ConcurrencyRetry.MaxAttempts,
            meanwhile: async ct => rows = await SpendOrExpireAsync(authorizationId, expires, ct));
        using var response = await scene.SendAsync(authorizationId, key);
        race.Disarm();
        var seen = await ObserveAsync(
            response, scene.Actor, scene.MoneyEvent, authorizationId, key, race: null, scene.Accounts);
        _output.WriteLine(
            $"{write}: fired {race.Fired} of {ConcurrencyRetry.MaxAttempts} attempts; on the last, the authorisation "
            + $"{(expires ? "expired" : "was spent by another request")}: {rows} row(s); answered {(int)seen.Status} "
            + $"{seen.ErrorCode}; Retry-After {response.Headers.RetryAfter?.Delta?.ToString() ?? "none"}");

        using var scope = new AssertionScope();
        race.Fired.Should().Be(ConcurrencyRetry.MaxAttempts, "the proof is void unless the write lost every attempt it is given");
        rows.Should().Be(1, "and unless the authorisation changed as the last attempt lost");
        seen.Status.Should().Be(
            HttpStatusCode.Unauthorized,
            $"an authorisation that is no longer good is not one to present again; the body was: {seen.Body}");
        seen.ErrorCode.Should().Be(expires ? ErrorCodes.AuthorizationExpired : ErrorCodes.AuthorizationInvalid);
        response.Headers.RetryAfter.Should().BeNull("nothing invites the caller to send it again");
        seen.Replayed.Should().BeFalse();
        seen.Accounts.Should().Equal(
            scene.Accounts.Select((_, i) => i == 0 ? Funded : Empty).ToArray(), "no balance moves and no ledger row is written");
        seen.MoneyAuditRows.Should().Be(0);
        seen.Authorization.Should().Be(
            expires ? StepUpAuthorizationStatus.Pending : StepUpAuthorizationStatus.Consumed,
            "the request itself spends nothing");
        seen.IdempotencyRecords.Should().BeEmpty("a refusal before the commit releases the key");
        seen.ChainIntact.Should().BeTrue(because: seen.ChainReason);
    }

    /// <summary>
    /// Another external transfer of the same user lands as the transfer loses its last attempt, and
    /// with it the day's ceiling no longer has room for the amount: the answer is the 422 of the
    /// ceiling, not the busy 503.
    /// </summary>
    [SqlServerFact]
    public async Task ATransferWhoseDayIsFilledAsItLosesItsLastAttempt_AnswersDailyLimitExceeded_NotBusy()
    {
        var race = new LostRowVersionRaceInterceptor(SqlServerFactAttribute.ConnectionString!);
        var client = CreateSqlClient(host =>
        {
            // The ceiling has room for this transfer and for nothing more.
            host.SetDailyLimit(Amount);
            host.AddInterceptor(race);
        });
        var scene = await SetUpAsync(client, Write.TransferLosingItsSource, "by");
        var other = await CreateSpareAsync(client, scene.Actor);
        var authorizationId = await scene.MintAsync();
        var key = Guid.NewGuid();
        var usedBefore = await UsedTodayAsync(scene.Actor);

        race.Arm(
            scene.Lost, meanwhileOn: ConcurrencyRetry.MaxAttempts,
            meanwhile: ct => AddAnExternalTransferOutAsync(other, 1m, ct));
        using var response = await scene.SendAsync(authorizationId, key);
        race.Disarm();
        var seen = await ObserveAsync(
            response, scene.Actor, scene.MoneyEvent, authorizationId, key, race: null, scene.Accounts);
        var usedAfter = await UsedTodayAsync(scene.Actor);
        _output.WriteLine(
            $"fired {race.Fired} of {ConcurrencyRetry.MaxAttempts} attempts; on the last, another external transfer's "
            + $"ledger row landed: {race.MeanwhileRan}; counted today {usedBefore} -> {usedAfter} of a ceiling of {Amount}; "
            + $"answered {(int)seen.Status} {seen.ErrorCode}; Retry-After {response.Headers.RetryAfter?.Delta?.ToString() ?? "none"}");

        using var scope = new AssertionScope();
        race.Fired.Should().Be(ConcurrencyRetry.MaxAttempts, "the proof is void unless the transfer lost every attempt it is given");
        race.MeanwhileRan.Should().BeTrue("and unless the other transfer's row landed as the last attempt lost");
        usedBefore.Should().Be(0m);
        usedAfter.Should().Be(1m, "the day holds the other transfer and nothing of this one");
        seen.Status.Should().Be(
            HttpStatusCode.UnprocessableEntity,
            $"a day with no room left for the amount is not one to come back to within a second; the body was: {seen.Body}");
        seen.ErrorCode.Should().Be(ErrorCodes.DailyLimitExceeded);
        response.Headers.RetryAfter.Should().BeNull("nothing invites the caller to send it again");
        ShouldHaveMovedNothing(seen, Funded, Empty);
    }

    /// <summary>
    /// A write whose commit was let start and failed, and which then loses its account on every
    /// attempt left, is not answered busy: a commit that started may have landed, whatever the
    /// record says afterwards. The conflict propagates, says nothing of <c>applied</c>, and no
    /// money moves.
    /// </summary>
    [SqlServerTheory]
    [InlineData(Write.Withdrawal)]
    [InlineData(Write.TransferLosingItsSource)]
    [InlineData(Write.InternalTransferLosingItsSource)]
    public async Task AWriteThatLetACommitStart_AndThenLosesEveryAttempt_IsNotAnsweredBusy(Write write)
    {
        var race = new LostRowVersionRaceInterceptor(SqlServerFactAttribute.ConnectionString!);
        var client = CreateSqlClient(host =>
        {
            host.EnableSqlRetryOnFailure();
            host.AddInterceptor(race);
        });
        var scene = await SetUpAsync(client, write, "bf");
        var authorizationId = await scene.MintAsync();
        var key = Guid.NewGuid();

        // The first attempt's commit passes the gate and fails with a fault EF retries; from then
        // on every save of the write loses the account.
        var fault = new FailedCommitInterceptor(
            () =>
            {
                race.Arm(scene.Lost);
                return Task.CompletedTask;
            },
            "UPDATE [Accounts]");
        AddInterceptor(fault);
        using var response = await scene.SendAsync(authorizationId, key);
        race.Disarm();
        var seen = await ObserveAsync(
            response, scene.Actor, scene.MoneyEvent, authorizationId, key, race: null, scene.Accounts);
        _output.WriteLine(
            $"{write}: a commit let start and failed: {fault.Fired}; then fired {race.Fired} of "
            + $"{ConcurrencyRetry.MaxAttempts} attempts; answered {(int)seen.Status}; "
            + $"Retry-After {response.Headers.RetryAfter?.Delta?.ToString() ?? "none"}");

        using var scope = new AssertionScope();
        fault.Fired.Should().BeTrue("the proof is void unless a commit the gate let start then failed");
        race.Fired.Should().Be(
            ConcurrencyRetry.MaxAttempts, "and unless the write then lost its account on the run that followed and on every later attempt");
        seen.Status.Should().Be(
            HttpStatusCode.InternalServerError,
            $"a request that let a commit start cannot say it wrote nothing; the body was: {seen.Body}");
        response.Headers.RetryAfter.Should().BeNull("nothing invites the caller to send it again");
        var problem = JsonSerializer.Deserialize<JsonElement>(seen.Body);
        problem.TryGetProperty("retryAfterSeconds", out _).Should().BeFalse();
        problem.TryGetProperty("applied", out _).Should().BeFalse("a commit that started is not known to have landed nothing");
        seen.Accounts.Should().Equal(
            scene.Accounts.Select((_, i) => i == 0 ? Funded : Empty).ToArray(), "the commit that failed landed nothing, and no later attempt wrote");
        seen.MoneyAuditRows.Should().Be(0);
        seen.Authorization.Should().Be(StepUpAuthorizationStatus.Pending);
        seen.ChainIntact.Should().BeTrue(because: seen.ChainReason);
    }

    /// <summary>
    /// The destination's row is deleted as an internal transfer loses its last attempt: a row that
    /// is gone is the 404 of a closed one, not a busy account.
    /// </summary>
    [SqlServerFact]
    public async Task AnInternalTransferWhoseDestinationRowGoesAsItLosesItsLastAttempt_AnswersNotFound_NotBusy()
    {
        var race = new LostRowVersionRaceInterceptor(SqlServerFactAttribute.ConnectionString!);
        var client = CreateSqlClient(host => host.AddInterceptor(race));
        var scene = await SetUpAsync(client, Write.InternalTransferLosingItsDestination, "bg");
        var authorizationId = await scene.MintAsync();
        var key = Guid.NewGuid();

        // The destination holds no ledger row, so nothing refers to its row and it can be deleted.
        race.Arm(scene.Lost, removingOn: ConcurrencyRetry.MaxAttempts);
        using var response = await scene.SendAsync(authorizationId, key);
        race.Disarm();
        var seen = await ObserveAsync(
            response, scene.Actor, scene.MoneyEvent, authorizationId, key, race: null, scene.Accounts);
        _output.WriteLine(
            $"fired {race.Fired} of {ConcurrencyRetry.MaxAttempts} attempts, the last a delete of the row: {race.Removed}; "
            + $"answered {(int)seen.Status} {seen.ErrorCode}");

        using var scope = new AssertionScope();
        race.Fired.Should().Be(ConcurrencyRetry.MaxAttempts, "the proof is void unless the transfer lost every attempt it is given");
        race.Removed.Should().BeTrue("and unless the write that won the last one deleted the destination's row");
        ShouldBeRefused(seen, HttpStatusCode.NotFound, ErrorCodes.AccountNotFound, NotFound(scene.Lost));
        response.Headers.RetryAfter.Should().BeNull("an account that is gone is not one to come back to");
        ShouldHaveMovedNothing(seen, Funded, null);
    }

    /// <summary>
    /// More parallel withdrawals than the retry carries, from an account that holds less than they
    /// ask for together, a key, an amount and an authorisation of its own each: each is answered
    /// 201 and made once, or the busy 503 and not made, or 422 <c>INSUFFICIENT_FUNDS</c> and not
    /// made, and at least one is answered busy. The balance is what the 201s leave and never
    /// below zero. None is a 500.
    /// </summary>
    [SqlServerFact]
    public async Task MoreParallelWithdrawalsThanTheAccountCovers_AreMadeOnce_AnsweredBusy_OrRefusedForFunds()
    {
        var client = CreateSqlClient(host => host.CaptureLog());
        var user = await RegisterWithPinAsync(client, "bw");
        var account = user.PrimaryAccountId;

        await RunInParallelAsync(
            client, user, account, credited: null, start: 1100m, AmountsTheAccountDoesNotCover,
            TransactionType.Withdrawal, SecurityEvents.MoneyWithdrawn,
            amount => MintWithdrawalAsync(client, user, account, amount),
            (amount, authorizationId, key) => WithdrawAsync(client, user, account, authorizationId, key, amount));
    }

    /// <summary>
    /// More parallel internal transfers than the retry carries, out of one account that covers them
    /// all into another of the same user: each is answered 201 and made once, or the busy 503 and
    /// not made, and at least one is answered busy. Each transfer writes both rows, so the
    /// destination's balance holds the same transfers.
    /// </summary>
    [SqlServerFact]
    public async Task MoreParallelInternalTransfersThanTheRetryCarries_AreMadeOnce_OrAnsweredTheBusy503()
    {
        var client = CreateSqlClient(host => host.CaptureLog());
        var user = await RegisterWithPinAsync(client, "bi");
        var source = user.PrimaryAccountId;
        var destination = await CreateSpareAsync(client, user);

        await RunInParallelAsync(
            client, user, source, destination, start: 1000m, AmountsTheAccountCovers,
            TransactionType.TransferOut, SecurityEvents.MoneyTransferredInternally,
            amount => MintAsync(client, user, "/api/transfers/internal/authorizations",
                new InternalTransferAuthorizationRequest
                {
                    FromAccountId = source,
                    ToAccountId = destination,
                    Amount = amount,
                    Pin = Pin,
                }),
            (amount, authorizationId, key) => SendAsync(client, user, HttpMethod.Post, "/api/transfers/internal",
                new InternalTransferRequest
                {
                    FromAccountId = source,
                    ToAccountId = destination,
                    Amount = amount,
                    Description = "busy account proof",
                }, authorizationId, key));
    }

    private const int Parallelism = 24;

    /// <summary>An amount of its own for each parallel request, 516 together: an account that holds 1000 covers them all.</summary>
    private static readonly decimal[] AmountsTheAccountCovers =
        Enumerable.Range(0, Parallelism).Select(i => 10m + i).ToArray();

    /// <summary>
    /// An amount of its own for each parallel request, 2223 together, which an account that holds
    /// 1100 does not cover: three of 600 and more, of which the account covers one, and twenty-one
    /// small ones, 420 together, which it covers beside that one and which therefore keep racing
    /// for the row until they are made or have spent their attempts.
    /// </summary>
    private static readonly decimal[] AmountsTheAccountDoesNotCover =
        Enumerable.Range(0, Parallelism).Select(i => i < 21 ? 10m + i : 579m + i).ToArray();

    /// <summary>One parallel request's answer, with what it asked for.</summary>
    private sealed record Answer(
        decimal Amount, Guid AuthorizationId, Guid Key, HttpResponseMessage Response, string Body)
    {
        public HttpStatusCode Status => Response.StatusCode;

        public string? ErrorCode =>
            Response.IsSuccessStatusCode
                ? null
                : JsonSerializer.Deserialize<JsonElement>(Body).TryGetProperty("errorCode", out var code) ? code.GetString() : null;
    }

    /// <summary>
    /// Funds <paramref name="debited"/> with <paramref name="start"/>, sends one request for each
    /// of the <paramref name="amounts"/> at once, writes what was measured and asserts the outcome.
    /// Each request has an amount of its own, so that the balance and the ledger say WHICH writes
    /// were made, not only how many. A <paramref name="start"/> below what the amounts sum to also
    /// lets a request be refused for funds.
    /// </summary>
    private async Task RunInParallelAsync(
        HttpClient client, TestUser user, Guid debited, Guid? credited, decimal start, decimal[] amounts,
        TransactionType ledgerType, string moneyEvent,
        Func<decimal, Task<Guid>> mintAsync,
        Func<decimal, Guid, Guid, Task<HttpResponseMessage>> sendAsync)
    {
        amounts.Should().HaveCount(Parallelism).And.OnlyHaveUniqueItems();
        var coversThemAll = start >= amounts.Sum();
        (await SendAsync(client, user, HttpMethod.Post, "/api/transactions/deposit",
            new DepositRequest { AccountId = debited, Amount = start, Description = "funding" },
            idempotencyKey: Guid.NewGuid())).EnsureSuccessStatusCode();

        // One authorisation per request, minted before the race: an authorisation is spent once.
        var authorizations = new Guid[Parallelism];
        for (var i = 0; i < Parallelism; i++)
        {
            authorizations[i] = await mintAsync(amounts[i]);
        }

        var clock = Stopwatch.StartNew();
        var answers = await Task.WhenAll(Enumerable.Range(0, Parallelism).Select(async i =>
        {
            var key = Guid.NewGuid();
            var response = await sendAsync(amounts[i], authorizations[i], key);
            return new Answer(amounts[i], authorizations[i], key, response, await response.Content.ReadAsStringAsync());
        }));
        clock.Stop();

        var made = answers.Where(a => a.Status == HttpStatusCode.Created).ToArray();
        var busy = answers.Where(a => a.Status == HttpStatusCode.ServiceUnavailable).ToArray();
        var refusedForFunds = answers
            .Where(a => a.Status == HttpStatusCode.UnprocessableEntity && a.ErrorCode == ErrorCodes.InsufficientFunds)
            .ToArray();
        var gaveUp = Host.CapturedLog.Count(l => l.Contains("lost the account to another write", StringComparison.Ordinal));

        using var services = Host.Services.CreateScope();
        var db = services.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var chain = services.ServiceProvider.GetRequiredService<IAuditChain>();
        var balance = (await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == debited)).Balance;
        var creditedBalance = credited is { } other
            ? (await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == other)).Balance
            : (decimal?)null;
        var ledger = await db.Transactions.AsNoTracking()
            .Where(t => t.AccountId == debited && t.Type == ledgerType)
            .Select(t => t.Amount)
            .ToListAsync();
        var lowest = await db.Transactions.AsNoTracking()
            .Where(t => t.AccountId == debited)
            .MinAsync(t => t.BalanceAfter);
        var auditRows = await db.AuditEvents.AsNoTracking()
            .CountAsync(e => e.Event == moneyEvent && e.ActorUserId == user.UserId);
        var spent = await db.StepUpAuthorizations.AsNoTracking()
            .Where(a => a.UserId == user.UserId)
            .ToDictionaryAsync(a => a.Id, a => a.Status);
        var records = (await db.IdempotencyRecords.AsNoTracking()
                .Where(r => r.UserId == user.UserId)
                .Select(r => new { r.Key, r.Status })
                .ToListAsync())
            .ToLookup(r => r.Key, r => r.Status);
        var verification = await chain.VerifyAsync(db);

        _output.WriteLine(
            $"{Parallelism} parallel requests at {ConcurrencyRetry.MaxAttempts} attempts in {clock.ElapsedMilliseconds} ms: "
            + $"{made.Length} made (201), {busy.Length} busy (503), {refusedForFunds.Length} refused for funds (422); every answer: "
            + string.Join(", ", answers.GroupBy(a => (int)a.Status).OrderBy(g => g.Key)
                .Select(g => $"{g.Count()} x {g.Key}{(g.Key is 201 or 503 ? string.Empty : " " + string.Join("/", g.Select(a => a.ErrorCode).Distinct()))}"))
            + $"; gave up in the log: {gaveUp}; balance {start} -> {balance}, the 201s sum to {made.Sum(a => a.Amount)}, "
            + $"lowest balance in the ledger {lowest}; credited account: {creditedBalance?.ToString() ?? "none"}; "
            + $"ledger rows {ledger.Count}, audit rows {auditRows}; "
            + $"authorisations of the 201s: {Tally(made.Select(a => spent[a.AuthorizationId].ToString()))}; "
            + $"of the 503s: {Tally(busy.Select(a => spent[a.AuthorizationId].ToString()))}; "
            + $"of the 422s: {Tally(refusedForFunds.Select(a => spent[a.AuthorizationId].ToString()))}; "
            + $"records under the keys answered 201: {Tally(made.Select(a => string.Join("+", records[a.Key])))}; "
            + $"under the keys answered 503: {Tally(busy.Select(a => string.Join("+", records[a.Key])))}; "
            + $"chain intact: {verification.IsIntact}");

        using var scope = new AssertionScope();
        answers.Where(a => a.Status == HttpStatusCode.InternalServerError).Should().BeEmpty(
            "a write that cannot be made now is a write to send again, not a fault of the API; got: "
            + string.Join(" | ", answers.Where(a => a.Status == HttpStatusCode.InternalServerError)
                .Select(a => a.Body[..Math.Min(150, a.Body.Length)])));
        (made.Length + busy.Length + refusedForFunds.Length).Should().Be(
            Parallelism, "each write is made, or its account is busy, or the account does not cover it, and nothing else");
        if (coversThemAll)
        {
            refusedForFunds.Should().BeEmpty("the account covers every amount, so none is refused for funds");
        }

        busy.Should().NotBeEmpty("the proof is void unless at least one write spent its attempts and was answered busy");

        foreach (var answer in busy)
        {
            ShouldBeTheBusy503(answer.Response, answer.Body, saysNothingWasApplied: true);
            spent[answer.AuthorizationId].Should().Be(
                StepUpAuthorizationStatus.Pending, "a write answered 503 spends no authorisation");
            records[answer.Key].Should().BeEmpty("the claim of a write answered 503 is released, so its key holds no record");
        }

        foreach (var answer in refusedForFunds)
        {
            spent[answer.AuthorizationId].Should().Be(
                StepUpAuthorizationStatus.Pending, "a write refused for funds spends no authorisation");
            records[answer.Key].Should().BeEmpty("the claim of a write refused before its commit is released");
        }

        foreach (var answer in made)
        {
            spent[answer.AuthorizationId].Should().Be(StepUpAuthorizationStatus.Consumed);
            records[answer.Key].Should().Equal([IdempotencyStatus.Completed], "a key answered 201 holds exactly one record");
        }

        gaveUp.Should().Be(busy.Length, "each busy answer is one write that spent its attempts");
        balance.Should().Be(start - made.Sum(a => a.Amount), "the balance holds every write answered 201 and no other");
        balance.Should().BeGreaterThanOrEqualTo(0m, "the writes made never take more than the account held");
        lowest.Should().BeGreaterThanOrEqualTo(0m, "and no write leaves the account below zero on its way");
        if (creditedBalance is { } received)
        {
            received.Should().Be(made.Sum(a => a.Amount), "the destination holds what the source gave");
        }

        ledger.Should().BeEquivalentTo(
            made.Select(a => a.Amount), "each write answered 201 is in the ledger once, and no other write is");
        auditRows.Should().Be(made.Length, "the audit trail records each write made once, and no attempt that lost");
        verification.IsIntact.Should().BeTrue(because: verification.Reason);

        foreach (var answer in answers)
        {
            answer.Response.Dispose();
        }

        static string Tally(IEnumerable<string> values) =>
            string.Join(", ", values.GroupBy(v => v.Length == 0 ? "none" : v).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => $"{g.Count()} x {g.Key}")) is { Length: > 0 } tally ? tally : "no such answer";
    }

    /// <summary>The write, its accounts in the order they are observed (the debited one first), and the one it loses.</summary>
    private sealed record Scene(
        TestUser Actor, string MoneyEvent, Guid[] Accounts, Guid Lost, bool CountsTowardTheDay,
        Func<Task<Guid>> MintAsync, Func<Guid, Guid, Task<HttpResponseMessage>> SendAsync);

    private static async Task<Scene> SetUpAsync(HttpClient client, Write write, string prefix)
    {
        var actor = await RegisterWithPinAsync(client, prefix);
        var source = actor.PrimaryAccountId;
        await FundAsync(client, actor, source);

        switch (write)
        {
            case Write.Withdrawal:
                return new Scene(
                    actor, SecurityEvents.MoneyWithdrawn, [source], source, CountsTowardTheDay: false,
                    () => MintWithdrawalAsync(client, actor, source),
                    (authorizationId, key) => WithdrawAsync(client, actor, source, authorizationId, key));

            case Write.TransferLosingItsSource or Write.TransferLosingThePayeeAccount:
                var payee = await RegisterAsync(client, prefix + "p");
                return new Scene(
                    actor, SecurityEvents.MoneyTransferred, [source, payee.PrimaryAccountId],
                    write == Write.TransferLosingItsSource ? source : payee.PrimaryAccountId, CountsTowardTheDay: true,
                    () => MintTransferAsync(client, actor, source, payee.AzureTag),
                    (authorizationId, key) => TransferAsync(client, actor, source, payee.AzureTag, authorizationId, key));

            default:
                var destination = await CreateSpareAsync(client, actor);
                return new Scene(
                    actor, SecurityEvents.MoneyTransferredInternally, [source, destination],
                    write == Write.InternalTransferLosingItsSource ? source : destination, CountsTowardTheDay: false,
                    () => MintInternalAsync(client, actor, source, destination),
                    (authorizationId, key) => InternalTransferAsync(client, actor, source, destination, authorizationId, key));
        }
    }

    /// <summary>A 500 that names no wait and says nothing of <c>applied</c>.</summary>
    private static void ShouldBeAFaultThatInvitesNoRetry(HttpResponseMessage response, Observed seen)
    {
        seen.Status.Should().Be(
            HttpStatusCode.InternalServerError,
            $"a key that another request holds is not to be sent again as if the account were busy; the body was: {seen.Body}");
        response.Headers.RetryAfter.Should().BeNull("nothing invites the caller to send it again");
        var problem = JsonSerializer.Deserialize<JsonElement>(seen.Body);
        problem.TryGetProperty("retryAfterSeconds", out _).Should().BeFalse();
        problem.TryGetProperty("applied", out _).Should().BeFalse("a request without its claim knows nothing about the key");
    }

    /// <summary>What the user's external transfers of today sum to, as the day's ceiling counts them.</summary>
    private async Task<decimal> UsedTodayAsync(TestUser user)
    {
        using var scope = Host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IDailyOutflowLimit>().UsedAsync(user.UserId);
    }

    /// <summary>
    /// The ledger row an external transfer out of <paramref name="accountId"/> leaves, the row the
    /// day's ceiling counts, saved from a scope of its own. No balance moves: the proof is about
    /// what the day's sum does to the request that reads it.
    /// </summary>
    private async Task AddAnExternalTransferOutAsync(Guid accountId, decimal amount, CancellationToken cancellationToken)
    {
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var account = await db.Accounts.SingleAsync(a => a.Id == accountId, cancellationToken);
        db.Transactions.Add(new Transaction
        {
            Id = Guid.CreateVersion7(),
            TransactionNumber = IdGenerator.GenerateTransactionNumber(),
            AccountId = accountId,
            Account = account,
            Type = TransactionType.TransferOut,
            Amount = amount,
            BalanceBefore = amount,
            BalanceAfter = 0m,
            RecipientAzureTag = "another_payee",
            Status = TransactionStatus.Completed,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Every idempotency record under the key: its status and the claim that holds it.</summary>
    private async Task<List<(IdempotencyStatus Status, Guid ClaimId)>> ReadClaimsAsync(Guid key)
    {
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return (await db.IdempotencyRecords.AsNoTracking()
            .Where(r => r.Key == key)
            .Select(r => new { r.Status, r.ClaimId })
            .ToListAsync())
            .ConvertAll(r => (r.Status, r.ClaimId));
    }

    private void WriteTheLog()
    {
        foreach (var line in Host.CapturedLog)
        {
            _output.WriteLine("  log " + line[..Math.Min(300, line.Length)]);
        }
    }
}
