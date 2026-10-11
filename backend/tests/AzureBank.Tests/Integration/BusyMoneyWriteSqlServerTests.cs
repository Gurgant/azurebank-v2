using System.Diagnostics;
using System.Net;
using System.Text.Json;
using AzureBank.Api.Services;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
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
/// A withdrawal, an external transfer and an internal transfer that lose the race for an account's
/// row on every attempt they are given, on SQL Server: each answers the busy 503 a deposit
/// answers, by the deposit's rule, writes nothing, leaves its authorisation unspent and its key
/// free, and is made when the same request is sent again.
/// </summary>
/// <remarks>
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
    /// answers that the write was applied. It is not a busy account, and the money moved once.
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
    /// More parallel withdrawals from one account than the retry carries, a key, an amount and an
    /// authorisation of its own each: each is answered 201 and made once, or answered the busy 503
    /// and not made, and at least one is answered busy. None is a 500.
    /// </summary>
    [SqlServerFact]
    public async Task MoreParallelWithdrawalsThanTheRetryCarries_AreMadeOnce_OrAnsweredTheBusy503()
    {
        var client = CreateSqlClient(host => host.CaptureLog());
        var user = await RegisterWithPinAsync(client, "bw");
        var account = user.PrimaryAccountId;

        await RunInParallelAsync(
            client, user, account, credited: null, TransactionType.Withdrawal, SecurityEvents.MoneyWithdrawn,
            amount => MintWithdrawalAsync(client, user, account, amount),
            (amount, authorizationId, key) => WithdrawAsync(client, user, account, authorizationId, key, amount));
    }

    /// <summary>
    /// The same for internal transfers out of one account into another of the same user. Each
    /// transfer writes both rows, so the destination's balance holds the same transfers.
    /// </summary>
    [SqlServerFact]
    public async Task MoreParallelInternalTransfersThanTheRetryCarries_AreMadeOnce_OrAnsweredTheBusy503()
    {
        var client = CreateSqlClient(host => host.CaptureLog());
        var user = await RegisterWithPinAsync(client, "bi");
        var source = user.PrimaryAccountId;
        var destination = await CreateSpareAsync(client, user);

        await RunInParallelAsync(
            client, user, source, destination, TransactionType.TransferOut, SecurityEvents.MoneyTransferredInternally,
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
    /// Funds <paramref name="debited"/> for every request, sends <see cref="Parallelism"/> of them
    /// at once with an amount of its own each, writes what was measured and asserts the outcome.
    /// </summary>
    private async Task RunInParallelAsync(
        HttpClient client, TestUser user, Guid debited, Guid? credited, TransactionType ledgerType, string moneyEvent,
        Func<decimal, Task<Guid>> mintAsync,
        Func<decimal, Guid, Guid, Task<HttpResponseMessage>> sendAsync)
    {
        // An amount of its own for each request, so that the balance and the ledger say WHICH
        // writes were made, not only how many. The account holds enough for all of them.
        var amounts = Enumerable.Range(0, Parallelism).Select(i => 10m + i).ToArray();
        const decimal start = 1000m;
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
            + string.Join(", ", answers.GroupBy(a => (int)a.Status).OrderBy(g => g.Key)
                .Select(g => $"{g.Count()} x {g.Key}{(g.Key is 201 or 503 ? string.Empty : " " + string.Join("/", g.Select(a => a.ErrorCode).Distinct()))}"))
            + $"; gave up in the log: {gaveUp}; balance {start} -> {balance}, the 201s sum to {made.Sum(a => a.Amount)}, "
            + $"lowest balance in the ledger {lowest}; credited account: {creditedBalance?.ToString() ?? "none"}; "
            + $"ledger rows {ledger.Count}, audit rows {auditRows}; "
            + $"authorisations of the 201s: {Tally(made.Select(a => spent[a.AuthorizationId].ToString()))}; "
            + $"of the 503s: {Tally(busy.Select(a => spent[a.AuthorizationId].ToString()))}; "
            + $"records under the keys answered 201: {Tally(made.Select(a => string.Join("+", records[a.Key])))}; "
            + $"under the keys answered 503: {Tally(busy.Select(a => string.Join("+", records[a.Key])))}; "
            + $"chain intact: {verification.IsIntact}");

        using var scope = new AssertionScope();
        answers.Where(a => a.Status == HttpStatusCode.InternalServerError).Should().BeEmpty(
            "a write that cannot be made now is a write to send again, not a fault of the API; got: "
            + string.Join(" | ", answers.Where(a => a.Status == HttpStatusCode.InternalServerError)
                .Select(a => a.Body[..Math.Min(150, a.Body.Length)])));
        answers.Should().OnlyContain(
            a => a.Status == HttpStatusCode.Created
                || a.Status == HttpStatusCode.ServiceUnavailable
                || (a.Status == HttpStatusCode.UnprocessableEntity && a.ErrorCode == ErrorCodes.InsufficientFunds),
            "each write is made, or its account is busy, or the account no longer covers it");
        busy.Should().NotBeEmpty("the proof is void unless at least one write spent its attempts and was answered busy");

        foreach (var answer in busy)
        {
            ShouldBeTheBusy503(answer.Response, answer.Body, saysNothingWasApplied: true);
            spent[answer.AuthorizationId].Should().Be(
                StepUpAuthorizationStatus.Pending, "a write answered 503 spends no authorisation");
            records[answer.Key].Should().BeEmpty("the claim of a write answered 503 is released, so its key holds no record");
        }

        foreach (var answer in made)
        {
            spent[answer.AuthorizationId].Should().Be(StepUpAuthorizationStatus.Consumed);
            records[answer.Key].Should().Equal([IdempotencyStatus.Completed], "a key answered 201 holds exactly one record");
        }

        gaveUp.Should().Be(busy.Length, "each busy answer is one write that spent its attempts");
        balance.Should().Be(start - made.Sum(a => a.Amount), "the balance holds every write answered 201 and no other");
        lowest.Should().BeGreaterThanOrEqualTo(0m, "no write leaves the account below zero");
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
