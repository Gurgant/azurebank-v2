using System.Net;
using System.Text.Json;
using AzureBank.Api.Services;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.Enums;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Events;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The account writes that hold no idempotency key, past their attempts, on SQL Server: a closure,
/// a change of the primary account and a rename that lose the race for an account's row on every
/// attempt they are given.
/// </summary>
/// <remarks>
/// <para>
/// With no claim to read, what shows that nothing of the write was committed is the request's
/// commit mark alone. So three proofs are about the mark: each save of a closure, of a change of
/// the primary account and of a rename lands at a commit that has set it.
/// </para>
/// <para>
/// Seven are about a closure past its attempts. One answers the busy 503, which says nothing of
/// <c>applied</c>, leaves everything as it was, and closes the account when the same request is
/// sent again. Five change, as the last attempt loses, something the closure looks at, and are
/// answered that refusal and not the busy 503: the account closes (404), its row is deleted
/// (404), it is credited (422), it becomes the primary one (422), the authorisation is spent by
/// another request (401). One lets a commit start and fail first, and is answered 500.
/// </para>
/// <para>
/// Four are about a change of the primary account past its attempts: one is busy and made when
/// sent again, losing the old primary's row or the new one's; one has a target that closes (404);
/// one a target whose row is deleted (404); one lets a commit start and fail first (500).
/// </para>
/// <para>
/// Six are about a rename. One is past its attempts: it answers the busy 503, leaves the name as
/// it was, and is made when the same request is sent again. One loses every attempt but its last
/// and is made there, at one commit. One has an account that closes as its last attempt loses
/// (404); one an account whose row is deleted then (404); one lets a commit start and fail first
/// (500). One has a commit that fails with no other write about: it is made on the run that
/// follows, and spends no attempt.
/// </para>
/// <para>
/// Not held here: a closure whose authorisation expires as its last attempt loses, and a change
/// of the primary account whose old primary closes then.
/// </para>
/// <para>
/// Each proof writes what it measured to the test output before it asserts, asserts that its
/// fixtures fired, and asserts that the audit chain verifies after its request.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class BusyAccountWriteSqlServerTests(ITestOutputHelper output) : ClosedAccountSqlServerProofs(output)
{
    private const string NewName = "Renamed";

    private readonly ITestOutputHelper _output = output;

    // The commit mark.

    [SqlServerFact]
    public async Task EverySaveOfAClosure_LandsAtACommitThatSetsTheMark()
    {
        var probe = new CommitMarkProbeInterceptor();
        var client = CreateSqlClient(host => host.AddInterceptor(probe));
        var user = await RegisterWithPinAsync(client, "km");
        var spare = await CreateSpareAsync(client, user);
        var authorizationId = await MintDeletionAsync(client, user, spare);

        probe.Arm();
        using var response = await DeleteAsync(client, user, spare, authorizationId);
        probe.Disarm();

        WriteTheMark("closure", response, probe);
        ShouldHaveLandedOnlyAtMarkedCommits(response, probe, commits: 1);
        ChainShouldBeIntact(await ReadAsync(user, authorizationId, spare));
    }

    [SqlServerFact]
    public async Task EverySaveOfAChangeOfThePrimaryAccount_LandsAtACommitThatSetsTheMark()
    {
        var probe = new CommitMarkProbeInterceptor();
        var client = CreateSqlClient(host => host.AddInterceptor(probe));
        var user = await RegisterAsync(client, "pm");
        var second = await CreateSpareAsync(client, user);

        // Two saves in one transaction: the old primary's, then the new one's.
        probe.Arm();
        using var response = await SetPrimaryAsync(client, user, second);
        probe.Disarm();

        WriteTheMark("change of primary", response, probe);
        ShouldHaveLandedOnlyAtMarkedCommits(response, probe, commits: 1);
        ChainShouldBeIntact(await ReadAsync(user, null, user.PrimaryAccountId, second));
    }

    [SqlServerFact]
    public async Task EverySaveOfARename_LandsAtACommitThatSetsTheMark()
    {
        var probe = new CommitMarkProbeInterceptor();
        var client = CreateSqlClient(host => host.AddInterceptor(probe));
        var user = await RegisterAsync(client, "rm");

        probe.Arm();
        using var response = await RenameAsync(client, user, user.PrimaryAccountId);
        probe.Disarm();
        var renamed = await ReadAsync(user, null, user.PrimaryAccountId);

        WriteTheMark("rename", response, probe);
        ShouldHaveLandedOnlyAtMarkedCommits(response, probe, commits: 1);
        renamed.Accounts[0]!.Name.Should().Be(NewName);
        ChainShouldBeIntact(renamed);
    }

    // The closure.

    [SqlServerFact]
    public async Task AClosureThatLosesEveryAttempt_AnswersTheBusy503_ChangesNothing_AndClosesWhenSentAgain()
    {
        var race = new LostRowVersionRaceInterceptor(SqlServerFactAttribute.ConnectionString!);
        var client = CreateSqlClient(host =>
        {
            host.AddInterceptor(race);
            host.CaptureLog();
        });
        var user = await RegisterWithPinAsync(client, "kb");
        var spare = await CreateSpareAsync(client, user);
        var authorizationId = await MintDeletionAsync(client, user, spare);
        var before = await ReadAsync(user, authorizationId, spare);

        race.Arm(spare);
        using var response = await DeleteAsync(client, user, spare, authorizationId);
        race.Disarm();
        var body = await response.Content.ReadAsStringAsync();
        var after = await ReadAsync(user, authorizationId, spare);

        // The 503 says to send the request again: the same DELETE with the same authorisation.
        using var again = await DeleteAsync(client, user, spare, authorizationId);
        var againBody = await again.Content.ReadAsStringAsync();
        var closed = await ReadAsync(user, authorizationId, spare);

        _output.WriteLine(
            $"closure: fired {race.Fired} of {ConcurrencyRetry.MaxAttempts} attempts; {Describe(response, body)}; "
            + $"before: {before}; after: {after}; the same DELETE again: {(int)again.StatusCode}, body: {againBody}; then: {closed}");
        WriteTheLog();

        using var scope = new AssertionScope();
        race.Fired.Should().Be(ConcurrencyRetry.MaxAttempts, "the proof is void unless the closure lost every attempt it is given");
        ShouldBeTheBusy503(response, body, saysNothingWasApplied: false);
        after.Should().Be(before, "a closure answered 503 closed nothing, wrote no audit row and spent nothing");
        before.Authorization.Should().Be(StepUpAuthorizationStatus.Pending);
        ChainShouldBeIntact(before, after, closed);

        again.StatusCode.Should().Be(HttpStatusCode.OK, $"the authorisation is still good for the same closure; the body was: {againBody}");
        closed.Should().Be(before with
        {
            Accounts = [before.Accounts[0]! with { IsDeleted = true }],
            AccountDeletedRows = 1,
            Authorization = StepUpAuthorizationStatus.Consumed,
        }, "the closure is made once, with its one audit row, and spends its authorisation once");
    }

    [SqlServerFact]
    public async Task AClosureWhoseAccountClosesAsItLosesItsLastAttempt_AnswersNotFound_NotBusy()
    {
        var race = new LostRowVersionRaceInterceptor(SqlServerFactAttribute.ConnectionString!);
        var client = CreateSqlClient(host => host.AddInterceptor(race));
        var user = await RegisterWithPinAsync(client, "kc");
        var spare = await CreateSpareAsync(client, user);
        var authorizationId = await MintDeletionAsync(client, user, spare);
        var before = await ReadAsync(user, authorizationId, spare);

        race.Arm(spare, closingOn: ConcurrencyRetry.MaxAttempts);
        using var response = await DeleteAsync(client, user, spare, authorizationId);
        race.Disarm();
        var body = await response.Content.ReadAsStringAsync();
        var after = await ReadAsync(user, authorizationId, spare);

        _output.WriteLine(
            $"closure: fired {race.Fired} of {ConcurrencyRetry.MaxAttempts} attempts, the last a closure: {race.Closed}; "
            + $"{Describe(response, body)}; before: {before}; after: {after}");

        using var scope = new AssertionScope();
        race.Fired.Should().Be(ConcurrencyRetry.MaxAttempts, "the proof is void unless the closure lost every attempt it is given");
        race.Closed.Should().BeTrue("and unless the write that won the last one closed the account");
        ShouldBeRefused(response, body, HttpStatusCode.NotFound, ErrorCodes.AccountNotFound);
        after.Should().Be(
            before with { Accounts = [before.Accounts[0]! with { IsDeleted = true }] },
            "the request wrote no audit row and spent nothing: the account is closed by the other write alone");
        ChainShouldBeIntact(after);
    }

    /// <summary>
    /// The account's row is deleted, not closed, as the closure loses its last attempt: the answer
    /// is the 404 of an account that is gone, and the request wrote nothing and spent nothing.
    /// </summary>
    [SqlServerFact]
    public async Task AClosureWhoseRowIsDeletedAsItLosesItsLastAttempt_AnswersNotFound_NotBusy()
    {
        var race = new LostRowVersionRaceInterceptor(SqlServerFactAttribute.ConnectionString!);
        var client = CreateSqlClient(host => host.AddInterceptor(race));
        var user = await RegisterWithPinAsync(client, "kr");
        var spare = await CreateSpareAsync(client, user);
        var authorizationId = await MintDeletionAsync(client, user, spare);
        var before = await ReadAsync(user, authorizationId, spare);

        race.Arm(spare, removingOn: ConcurrencyRetry.MaxAttempts);
        using var response = await DeleteAsync(client, user, spare, authorizationId);
        race.Disarm();
        var body = await response.Content.ReadAsStringAsync();
        var after = await ReadAsync(user, authorizationId, spare);

        _output.WriteLine(
            $"closure: fired {race.Fired} of {ConcurrencyRetry.MaxAttempts} attempts, the last a delete of the row: {race.Removed}; "
            + $"{Describe(response, body)}; before: {before}; after: {after}");

        using var scope = new AssertionScope();
        race.Fired.Should().Be(ConcurrencyRetry.MaxAttempts, "the proof is void unless the closure lost every attempt it is given");
        race.Removed.Should().BeTrue("and unless the write that won the last one deleted the account's row");
        before.Accounts.Should().Equal([new AccountState("Spare", false, false, 0m)], "and unless the row was there, open, before");
        before.Authorization.Should().Be(StepUpAuthorizationStatus.Pending);
        ShouldBeRefused(response, body, HttpStatusCode.NotFound, ErrorCodes.AccountNotFound);
        after.Should().Be(
            before with { Accounts = [null] },
            "the row is gone, and a closure that closed nothing writes no audit row and spends nothing");
        ChainShouldBeIntact(after);
    }

    [SqlServerFact]
    public async Task AClosureWhoseAccountIsCreditedAsItLosesItsLastAttempt_AnswersNonZeroBalance_NotBusy()
    {
        var race = new LostRowVersionRaceInterceptor(SqlServerFactAttribute.ConnectionString!);
        var client = CreateSqlClient(host => host.AddInterceptor(race));
        var user = await RegisterWithPinAsync(client, "kd");
        var spare = await CreateSpareAsync(client, user);
        var authorizationId = await MintDeletionAsync(client, user, spare);
        var before = await ReadAsync(user, authorizationId, spare);

        race.Arm(spare, creditingOn: ConcurrencyRetry.MaxAttempts);
        using var response = await DeleteAsync(client, user, spare, authorizationId);
        race.Disarm();
        var body = await response.Content.ReadAsStringAsync();
        var after = await ReadAsync(user, authorizationId, spare);

        _output.WriteLine(
            $"closure: fired {race.Fired} of {ConcurrencyRetry.MaxAttempts} attempts, the last a credit: {race.Credited}; "
            + $"{Describe(response, body)}; before: {before}; after: {after}");

        using var scope = new AssertionScope();
        race.Fired.Should().Be(ConcurrencyRetry.MaxAttempts, "the proof is void unless the closure lost every attempt it is given");
        race.Credited.Should().BeTrue("and unless the write that won the last one credited the account");
        ShouldBeRefused(response, body, HttpStatusCode.UnprocessableEntity, ErrorCodes.NonZeroBalance);
        after.Should().Be(
            before with { Accounts = [before.Accounts[0]! with { Balance = LostRowVersionRaceInterceptor.Credit }] },
            "a funded account is not closed, and a closure the guards refuse spends nothing");
        ChainShouldBeIntact(after);
    }

    /// <summary>
    /// The account becomes the user's primary one as the closure loses its last attempt: another
    /// connection clears the old primary and sets this one in one transaction, the order the
    /// unique index on the primary allows. The answer is the primary guard's 422.
    /// </summary>
    [SqlServerFact]
    public async Task AClosureWhoseAccountBecomesThePrimaryOneAsItLosesItsLastAttempt_AnswersThePrimaryGuard_NotBusy()
    {
        var race = new LostRowVersionRaceInterceptor(SqlServerFactAttribute.ConnectionString!);
        var client = CreateSqlClient(host => host.AddInterceptor(race));
        var user = await RegisterWithPinAsync(client, "kp");
        var spare = await CreateSpareAsync(client, user);
        var authorizationId = await MintDeletionAsync(client, user, spare);
        var before = await ReadAsync(user, authorizationId, spare, user.PrimaryAccountId);
        var rows = 0;

        race.Arm(
            spare, meanwhileOn: ConcurrencyRetry.MaxAttempts,
            meanwhile: async ct => rows = await OutOfBandClosureInterceptor.CloseAsync(
                SqlServerFactAttribute.ConnectionString!, user.PrimaryAccountId, newPrimaryId: spare,
                keepOpen: true, cancellationToken: ct));
        using var response = await DeleteAsync(client, user, spare, authorizationId);
        race.Disarm();
        var body = await response.Content.ReadAsStringAsync();
        var after = await ReadAsync(user, authorizationId, spare, user.PrimaryAccountId);

        _output.WriteLine(
            $"closure: fired {race.Fired} of {ConcurrencyRetry.MaxAttempts} attempts; on the last, the primary was moved "
            + $"to the account from another connection: {rows} row(s); {Describe(response, body)}; "
            + $"before: {before}; after: {after}");

        using var scope = new AssertionScope();
        race.Fired.Should().Be(ConcurrencyRetry.MaxAttempts, "the proof is void unless the closure lost every attempt it is given");
        race.MeanwhileRan.Should().BeTrue("and unless the primary was moved as the last attempt lost");
        rows.Should().Be(2, "one row for the old primary cleared and one for the account made primary");
        before.Accounts.Select(a => a?.IsPrimary).Should().Equal(
            [false, true], "and unless the account was not the primary one before");
        before.Authorization.Should().Be(StepUpAuthorizationStatus.Pending);
        ShouldBeRefused(response, body, HttpStatusCode.UnprocessableEntity, ErrorCodes.PrimaryAccountDelete);
        after.Should().Be(
            before with
            {
                Accounts = [before.Accounts[0]! with { IsPrimary = true }, before.Accounts[1]! with { IsPrimary = false }],
            },
            "the primary account is not closed, and a closure the guards refuse writes no audit row and spends nothing");
        ChainShouldBeIntact(after);
    }

    /// <summary>
    /// Another request spends the closure's authorisation as the closure loses its last attempt:
    /// the answer is the 401 a spent authorisation gets, not the busy 503.
    /// </summary>
    [SqlServerFact]
    public async Task AClosureWhoseAuthorisationIsSpentAsItLosesItsLastAttempt_AnswersThe401_NotBusy()
    {
        var race = new LostRowVersionRaceInterceptor(SqlServerFactAttribute.ConnectionString!);
        var client = CreateSqlClient(host => host.AddInterceptor(race));
        var user = await RegisterWithPinAsync(client, "ks");
        var spare = await CreateSpareAsync(client, user);
        var authorizationId = await MintDeletionAsync(client, user, spare);
        var before = await ReadAsync(user, authorizationId, spare);
        var rows = 0;

        race.Arm(
            spare, meanwhileOn: ConcurrencyRetry.MaxAttempts,
            meanwhile: async ct => rows = await SpendOrExpireAsync(authorizationId, expires: false, ct));
        using var response = await DeleteAsync(client, user, spare, authorizationId);
        race.Disarm();
        var body = await response.Content.ReadAsStringAsync();
        var after = await ReadAsync(user, authorizationId, spare);

        _output.WriteLine(
            $"closure: fired {race.Fired} of {ConcurrencyRetry.MaxAttempts} attempts; on the last, the authorisation was "
            + $"spent by another request: {rows} row(s); {Describe(response, body)}; before: {before}; after: {after}");

        using var scope = new AssertionScope();
        race.Fired.Should().Be(ConcurrencyRetry.MaxAttempts, "the proof is void unless the closure lost every attempt it is given");
        rows.Should().Be(1, "and unless the authorisation was spent as the last attempt lost");
        ShouldBeRefused(response, body, HttpStatusCode.Unauthorized, ErrorCodes.AuthorizationInvalid);
        after.Should().Be(
            before with { Authorization = StepUpAuthorizationStatus.Consumed },
            "the account stays open and the request wrote no audit row: the authorisation is spent by the other request alone");
        ChainShouldBeIntact(after);
    }

    /// <summary>
    /// A closure whose commit was let start and failed, and which then loses its account on every
    /// attempt left, is not answered busy: a commit that started may have landed, whatever the
    /// store shows afterwards, and with no claim to read the mark is all the request has.
    /// </summary>
    [SqlServerFact]
    public async Task AClosureThatLetACommitStart_AndThenLosesEveryAttempt_IsNotAnsweredBusy()
    {
        var race = new LostRowVersionRaceInterceptor(SqlServerFactAttribute.ConnectionString!);
        var client = CreateSqlClient(host =>
        {
            host.EnableSqlRetryOnFailure();
            host.AddInterceptor(race);
        });
        var user = await RegisterWithPinAsync(client, "kf");
        var spare = await CreateSpareAsync(client, user);
        var authorizationId = await MintDeletionAsync(client, user, spare);
        var before = await ReadAsync(user, authorizationId, spare);

        // The first attempt's commit passes the gate and fails with a fault EF retries; from then
        // on every save of the closure loses the account.
        var fault = new FailedCommitInterceptor(() => ArmAsync(race, spare), "UPDATE [Accounts]");
        AddInterceptor(fault);
        using var response = await DeleteAsync(client, user, spare, authorizationId);
        race.Disarm();
        var body = await response.Content.ReadAsStringAsync();
        var after = await ReadAsync(user, authorizationId, spare);

        _output.WriteLine(
            $"closure: a commit let start and failed: {fault.Fired}; then fired {race.Fired} of "
            + $"{ConcurrencyRetry.MaxAttempts} attempts; {Describe(response, body)}; before: {before}; after: {after}");

        using var scope = new AssertionScope();
        ShouldNotBeBusyAfterACommitStarted(fault, race, response, body);
        after.Should().Be(before, "the commit that failed landed nothing, and no later attempt wrote");
        ChainShouldBeIntact(after);
    }

    // The change of the primary account.

    /// <summary>The same for a change of the primary account: the mark refuses the busy answer.</summary>
    [SqlServerFact]
    public async Task AChangeOfPrimaryThatLetACommitStart_AndThenLosesEveryAttempt_IsNotAnsweredBusy()
    {
        var race = new LostRowVersionRaceInterceptor(SqlServerFactAttribute.ConnectionString!);
        var client = CreateSqlClient(host =>
        {
            host.EnableSqlRetryOnFailure();
            host.AddInterceptor(race);
        });
        var user = await RegisterAsync(client, "pf");
        var second = await CreateSpareAsync(client, user);
        var before = await ReadAsync(user, null, user.PrimaryAccountId, second);

        var fault = new FailedCommitInterceptor(() => ArmAsync(race, second), "UPDATE [Accounts]");
        AddInterceptor(fault);
        using var response = await SetPrimaryAsync(client, user, second);
        race.Disarm();
        var body = await response.Content.ReadAsStringAsync();
        var after = await ReadAsync(user, null, user.PrimaryAccountId, second);

        _output.WriteLine(
            $"change of primary: a commit let start and failed: {fault.Fired}; then fired {race.Fired} of "
            + $"{ConcurrencyRetry.MaxAttempts} attempts; {Describe(response, body)}; before: {before}; after: {after}");

        using var scope = new AssertionScope();
        ShouldNotBeBusyAfterACommitStarted(fault, race, response, body);
        after.Should().Be(before, "the commit that failed landed nothing, and no later attempt wrote");
        ChainShouldBeIntact(after);
    }

    [SqlServerTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AChangeOfPrimaryThatLosesEveryAttempt_AnswersTheBusy503_ChangesNothing_AndIsMadeWhenSentAgain(
        bool losingTheNewPrimary)
    {
        var race = new LostRowVersionRaceInterceptor(SqlServerFactAttribute.ConnectionString!);
        var client = CreateSqlClient(host =>
        {
            host.AddInterceptor(race);
            host.CaptureLog();
        });
        var user = await RegisterAsync(client, "pb");
        var second = await CreateSpareAsync(client, user);
        var before = await ReadAsync(user, null, user.PrimaryAccountId, second);

        race.Arm(losingTheNewPrimary ? second : user.PrimaryAccountId);
        using var response = await SetPrimaryAsync(client, user, second);
        race.Disarm();
        var body = await response.Content.ReadAsStringAsync();
        var after = await ReadAsync(user, null, user.PrimaryAccountId, second);

        using var again = await SetPrimaryAsync(client, user, second);
        var againBody = await again.Content.ReadAsStringAsync();
        var swapped = await ReadAsync(user, null, user.PrimaryAccountId, second);

        _output.WriteLine(
            $"change of primary, losing {(losingTheNewPrimary ? "the new primary" : "the old primary")}: "
            + $"fired {race.Fired} of {ConcurrencyRetry.MaxAttempts} attempts; {Describe(response, body)}; "
            + $"before: {before}; after: {after}; the same PATCH again: {(int)again.StatusCode}, body: {againBody}; then: {swapped}");
        WriteTheLog();

        using var scope = new AssertionScope();
        race.Fired.Should().Be(ConcurrencyRetry.MaxAttempts, "the proof is void unless the change lost every attempt it is given");
        ShouldBeTheBusy503(response, body, saysNothingWasApplied: false);
        after.Should().Be(before, "a change answered 503 left the primary where it was");
        before.Accounts.Select(a => a!.IsPrimary).Should().Equal(true, false);

        again.StatusCode.Should().Be(HttpStatusCode.OK, $"the same request is made when it is sent again; the body was: {againBody}");
        swapped.Accounts.Select(a => a!.IsPrimary).Should().Equal(false, true);
        ChainShouldBeIntact(after, swapped);
    }

    [SqlServerFact]
    public async Task AChangeOfPrimaryWhoseTargetClosesAsItLosesItsLastAttempt_AnswersNotFound_NotBusy()
    {
        var race = new LostRowVersionRaceInterceptor(SqlServerFactAttribute.ConnectionString!);
        var client = CreateSqlClient(host => host.AddInterceptor(race));
        var user = await RegisterAsync(client, "pc");
        var second = await CreateSpareAsync(client, user);
        var before = await ReadAsync(user, null, user.PrimaryAccountId, second);

        race.Arm(second, closingOn: ConcurrencyRetry.MaxAttempts);
        using var response = await SetPrimaryAsync(client, user, second);
        race.Disarm();
        var body = await response.Content.ReadAsStringAsync();
        var after = await ReadAsync(user, null, user.PrimaryAccountId, second);

        _output.WriteLine(
            $"change of primary: fired {race.Fired} of {ConcurrencyRetry.MaxAttempts} attempts, the last a closure: {race.Closed}; "
            + $"{Describe(response, body)}; before: {before}; after: {after}");

        using var scope = new AssertionScope();
        race.Fired.Should().Be(ConcurrencyRetry.MaxAttempts, "the proof is void unless the change lost every attempt it is given");
        race.Closed.Should().BeTrue("and unless the write that won the last one closed the target");
        ShouldBeRefused(response, body, HttpStatusCode.NotFound, ErrorCodes.AccountNotFound);
        after.Should().Be(
            before with { Accounts = [before.Accounts[0], before.Accounts[1]! with { IsDeleted = true }] },
            "the primary stays where it was");
        ChainShouldBeIntact(after);
    }

    /// <summary>
    /// The target's row is deleted, not closed, as the change loses its last attempt: the answer
    /// is the 404 of an account that is gone, and the primary stays where it was.
    /// </summary>
    [SqlServerFact]
    public async Task AChangeOfPrimaryWhoseTargetRowIsDeletedAsItLosesItsLastAttempt_AnswersNotFound_NotBusy()
    {
        var race = new LostRowVersionRaceInterceptor(SqlServerFactAttribute.ConnectionString!);
        var client = CreateSqlClient(host => host.AddInterceptor(race));
        var user = await RegisterAsync(client, "pr");
        var second = await CreateSpareAsync(client, user);
        var before = await ReadAsync(user, null, user.PrimaryAccountId, second);

        race.Arm(second, removingOn: ConcurrencyRetry.MaxAttempts);
        using var response = await SetPrimaryAsync(client, user, second);
        race.Disarm();
        var body = await response.Content.ReadAsStringAsync();
        var after = await ReadAsync(user, null, user.PrimaryAccountId, second);

        _output.WriteLine(
            $"change of primary: fired {race.Fired} of {ConcurrencyRetry.MaxAttempts} attempts, the last a delete of the "
            + $"target's row: {race.Removed}; {Describe(response, body)}; before: {before}; after: {after}");

        using var scope = new AssertionScope();
        race.Fired.Should().Be(ConcurrencyRetry.MaxAttempts, "the proof is void unless the change lost every attempt it is given");
        race.Removed.Should().BeTrue("and unless the write that won the last one deleted the target's row");
        before.Accounts.Select(a => a?.IsPrimary).Should().Equal(
            [true, false], "and unless the target was there before, and not primary");
        ShouldBeRefused(response, body, HttpStatusCode.NotFound, ErrorCodes.AccountNotFound);
        after.Should().Be(
            before with { Accounts = [before.Accounts[0], null] },
            "the primary stays where it was, and the target's row is gone");
        ChainShouldBeIntact(after);
    }

    // The rename.

    [SqlServerFact]
    public async Task ARenameThatLosesEveryAttempt_AnswersTheBusy503_ChangesNothing_AndIsMadeWhenSentAgain()
    {
        var race = new LostRowVersionRaceInterceptor(SqlServerFactAttribute.ConnectionString!);
        var client = CreateSqlClient(host =>
        {
            host.AddInterceptor(race);
            host.CaptureLog();
        });
        var user = await RegisterAsync(client, "rb");
        var before = await ReadAsync(user, null, user.PrimaryAccountId);

        race.Arm(user.PrimaryAccountId);
        using var response = await RenameAsync(client, user, user.PrimaryAccountId);
        race.Disarm();
        var body = await response.Content.ReadAsStringAsync();
        var after = await ReadAsync(user, null, user.PrimaryAccountId);

        // The 503 says to send the request again: the same PATCH with the same name.
        using var again = await RenameAsync(client, user, user.PrimaryAccountId);
        var againBody = await again.Content.ReadAsStringAsync();
        var renamed = await ReadAsync(user, null, user.PrimaryAccountId);

        _output.WriteLine(
            $"rename: fired {race.Fired} of {ConcurrencyRetry.MaxAttempts} attempts; {Describe(response, body)}; "
            + $"before: {before}; after: {after}; the same PATCH again: {(int)again.StatusCode}, body: {againBody}; then: {renamed}");
        WriteTheLog();

        using var scope = new AssertionScope();
        race.Fired.Should().Be(ConcurrencyRetry.MaxAttempts, "the proof is void unless the rename lost every attempt it is given");
        ShouldBeTheBusy503(response, body, saysNothingWasApplied: false);
        after.Should().Be(before, "a rename answered 503 left the name as it was");
        before.Accounts[0]!.Name.Should().NotBe(NewName, "the proof is void unless the name asked for is a new one");

        again.StatusCode.Should().Be(HttpStatusCode.OK, $"the same request is made when it is sent again; the body was: {againBody}");
        renamed.Should().Be(
            before with { Accounts = [before.Accounts[0]! with { Name = NewName }] }, "the rename changes the name and nothing else");
        ChainShouldBeIntact(after, renamed);
    }

    /// <summary>
    /// A rename that loses every attempt but its last is made on that one: one commit lands, with
    /// the mark set, and no save writes outside it.
    /// </summary>
    [SqlServerFact]
    public async Task ARenameThatGetsThroughOnItsLastAttempt_IsMadeOnce()
    {
        var race = new LostRowVersionRaceInterceptor(SqlServerFactAttribute.ConnectionString!);
        var probe = new CommitMarkProbeInterceptor();
        var client = CreateSqlClient(host =>
        {
            host.AddInterceptor(race);
            host.AddInterceptor(probe);
        });
        var user = await RegisterAsync(client, "ro");
        var before = await ReadAsync(user, null, user.PrimaryAccountId);

        race.Arm(user.PrimaryAccountId, saves: ConcurrencyRetry.MaxAttempts - 1);
        probe.Arm();
        using var response = await RenameAsync(client, user, user.PrimaryAccountId);
        probe.Disarm();
        race.Disarm();
        var body = await response.Content.ReadAsStringAsync();
        var after = await ReadAsync(user, null, user.PrimaryAccountId);

        _output.WriteLine(
            $"rename: fired {race.Fired} of {ConcurrencyRetry.MaxAttempts} attempts; {Describe(response, body)}; "
            + $"before: {before}; after: {after}");
        WriteTheMark("rename", response, probe);

        using var scope = new AssertionScope();
        race.Fired.Should().Be(
            ConcurrencyRetry.MaxAttempts - 1, "the proof is void unless the rename lost every attempt but its last");
        ShouldHaveLandedOnlyAtMarkedCommits(response, probe, commits: 1);
        after.Should().Be(
            before with { Accounts = [before.Accounts[0]! with { Name = NewName }] }, "the rename changes the name and nothing else");
        ChainShouldBeIntact(after);
    }

    [SqlServerFact]
    public async Task ARenameWhoseAccountClosesAsItLosesItsLastAttempt_AnswersNotFound_NotBusy()
    {
        var race = new LostRowVersionRaceInterceptor(SqlServerFactAttribute.ConnectionString!);
        var client = CreateSqlClient(host => host.AddInterceptor(race));
        var user = await RegisterAsync(client, "rc");
        var spare = await CreateSpareAsync(client, user);
        var before = await ReadAsync(user, null, spare);

        race.Arm(spare, closingOn: ConcurrencyRetry.MaxAttempts);
        using var response = await RenameAsync(client, user, spare);
        race.Disarm();
        var body = await response.Content.ReadAsStringAsync();
        var after = await ReadAsync(user, null, spare);

        _output.WriteLine(
            $"rename: fired {race.Fired} of {ConcurrencyRetry.MaxAttempts} attempts, the last a closure: {race.Closed}; "
            + $"{Describe(response, body)}; before: {before}; after: {after}");

        using var scope = new AssertionScope();
        race.Fired.Should().Be(ConcurrencyRetry.MaxAttempts, "the proof is void unless the rename lost every attempt it is given");
        race.Closed.Should().BeTrue("and unless the write that won the last one closed the account");
        ShouldBeRefused(response, body, HttpStatusCode.NotFound, ErrorCodes.AccountNotFound);
        after.Should().Be(
            before with { Accounts = [before.Accounts[0]! with { IsDeleted = true }] },
            "the account is closed by the other write alone, under the name it had");
        ChainShouldBeIntact(after);
    }

    /// <summary>
    /// The account's row is deleted, not closed, as the rename loses its last attempt: the answer
    /// is the 404 of an account that is gone.
    /// </summary>
    [SqlServerFact]
    public async Task ARenameWhoseRowIsDeletedAsItLosesItsLastAttempt_AnswersNotFound_NotBusy()
    {
        var race = new LostRowVersionRaceInterceptor(SqlServerFactAttribute.ConnectionString!);
        var client = CreateSqlClient(host => host.AddInterceptor(race));
        var user = await RegisterAsync(client, "rr");
        var spare = await CreateSpareAsync(client, user);
        var before = await ReadAsync(user, null, spare);

        race.Arm(spare, removingOn: ConcurrencyRetry.MaxAttempts);
        using var response = await RenameAsync(client, user, spare);
        race.Disarm();
        var body = await response.Content.ReadAsStringAsync();
        var after = await ReadAsync(user, null, spare);

        _output.WriteLine(
            $"rename: fired {race.Fired} of {ConcurrencyRetry.MaxAttempts} attempts, the last a delete of the row: {race.Removed}; "
            + $"{Describe(response, body)}; before: {before}; after: {after}");

        using var scope = new AssertionScope();
        race.Fired.Should().Be(ConcurrencyRetry.MaxAttempts, "the proof is void unless the rename lost every attempt it is given");
        race.Removed.Should().BeTrue("and unless the write that won the last one deleted the account's row");
        before.Accounts.Should().Equal([new AccountState("Spare", false, false, 0m)], "and unless the row was there, open, before");
        ShouldBeRefused(response, body, HttpStatusCode.NotFound, ErrorCodes.AccountNotFound);
        after.Should().Be(before with { Accounts = [null] }, "the row is gone, and a rename of nothing writes nothing");
        ChainShouldBeIntact(after);
    }

    /// <summary>
    /// The same for a rename: a commit that was let start and failed keeps the request from the
    /// busy answer, whatever the store shows afterwards.
    /// </summary>
    [SqlServerFact]
    public async Task ARenameThatLetACommitStart_AndThenLosesEveryAttempt_IsNotAnsweredBusy()
    {
        var race = new LostRowVersionRaceInterceptor(SqlServerFactAttribute.ConnectionString!);
        var client = CreateSqlClient(host =>
        {
            host.EnableSqlRetryOnFailure();
            host.AddInterceptor(race);
        });
        var user = await RegisterAsync(client, "rf");
        var before = await ReadAsync(user, null, user.PrimaryAccountId);

        var fault = new FailedCommitInterceptor(() => ArmAsync(race, user.PrimaryAccountId), "UPDATE [Accounts]");
        AddInterceptor(fault);
        using var response = await RenameAsync(client, user, user.PrimaryAccountId);
        race.Disarm();
        var body = await response.Content.ReadAsStringAsync();
        var after = await ReadAsync(user, null, user.PrimaryAccountId);

        _output.WriteLine(
            $"rename: a commit let start and failed: {fault.Fired}; then fired {race.Fired} of "
            + $"{ConcurrencyRetry.MaxAttempts} attempts; {Describe(response, body)}; before: {before}; after: {after}");

        using var scope = new AssertionScope();
        ShouldNotBeBusyAfterACommitStarted(fault, race, response, body);
        after.Should().Be(before, "the commit that failed landed nothing, and no later attempt wrote");
        ChainShouldBeIntact(after);
    }

    /// <summary>
    /// A commit of a rename that fails and lands nothing, under the retrying strategy, with no
    /// other write on the account: the run that follows starts from the store, so its save carries
    /// the RowVersion the row has and is made. Left with what the failed run tracked, that save
    /// would lose to nobody and spend one of the attempts.
    /// </summary>
    [SqlServerFact]
    public async Task ARenameWhoseCommitFailed_IsMadeOnTheRunThatFollows_AndSpendsNoAttempt()
    {
        const string update = "UPDATE [Accounts]";
        var commands = new CommandRecordingInterceptor();
        var client = CreateSqlClient(host =>
        {
            host.EnableSqlRetryOnFailure();
            host.AddInterceptor(commands);
            host.CaptureLog(LogEventLevel.Information);
        });
        var user = await RegisterAsync(client, "rs");
        var before = await ReadAsync(user, null, user.PrimaryAccountId);

        var fault = new FailedCommitInterceptor(() => Task.CompletedTask, update);
        AddInterceptor(fault);
        commands.Start();
        using var response = await RenameAsync(client, user, user.PrimaryAccountId);
        commands.Stop();
        var body = await response.Content.ReadAsStringAsync();
        var after = await ReadAsync(user, null, user.PrimaryAccountId);
        var updates = commands.Writes.Count(text => text.Contains(update, StringComparison.Ordinal));
        var conflicts = Host.CapturedLog.Count(
            line => line.Contains("Concurrency conflict renaming", StringComparison.Ordinal));

        _output.WriteLine(
            $"rename: a commit let start and failed: {fault.Fired}; {Describe(response, body)}; "
            + $"{update} commands sent: {updates}; conflicts logged: {conflicts}; before: {before}; after: {after}");

        using var scope = new AssertionScope();
        fault.Fired.Should().BeTrue("the proof is void unless a commit of the rename failed");
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"the run that follows makes the rename; the body was: {body}");
        updates.Should().Be(2, "one save whose commit failed, and one that is made");
        conflicts.Should().Be(0, "no other write touched the account, so no attempt has a conflict to lose");
        after.Should().Be(
            before with { Accounts = [before.Accounts[0]! with { Name = NewName }] }, "the rename changes the name and nothing else");
        ChainShouldBeIntact(after);
    }

    // What the proofs share.

    /// <summary>One account as the database holds it, closed or not.</summary>
    private sealed record AccountState(string Name, bool IsDeleted, bool IsPrimary, decimal Balance);

    /// <summary>
    /// What the database holds around one request, as a fresh scope reads it: the accounts in the
    /// order asked for (null for a row that is gone), the actor's closure audit rows, the
    /// authorisation where the request presents one, and the audit chain.
    /// </summary>
    private sealed record Rows(
        IReadOnlyList<AccountState?> Accounts, int AccountDeletedRows, StepUpAuthorizationStatus? Authorization,
        bool ChainIntact)
    {
        public bool Equals(Rows? other) =>
            other is not null
            && Accounts.SequenceEqual(other.Accounts)
            && AccountDeletedRows == other.AccountDeletedRows
            && Authorization == other.Authorization
            && ChainIntact == other.ChainIntact;

        public override int GetHashCode() => HashCode.Combine(Accounts.Count, AccountDeletedRows, Authorization, ChainIntact);

        public override string ToString() =>
            "[" + string.Join(" ", Accounts.Select(a => a is null
                ? "[gone]"
                : $"[\"{a.Name}\", IsDeleted: {a.IsDeleted}, primary: {a.IsPrimary}, balance: {a.Balance}]"))
            + $", AccountDeleted audit rows: {AccountDeletedRows}, authorisation: {Authorization?.ToString() ?? "none"}, "
            + $"chain intact: {ChainIntact}]";
    }

    private async Task<Rows> ReadAsync(TestUser user, Guid? authorizationId, params Guid[] accountIds)
    {
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var chain = scope.ServiceProvider.GetRequiredService<IAuditChain>();

        var accounts = new List<AccountState?>();
        foreach (var accountId in accountIds)
        {
            var account = await db.Accounts.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId);
            accounts.Add(account is null
                ? null
                : new AccountState(account.Name, account.IsDeleted, account.IsPrimary, account.Balance));
        }

        var deletedRows = await db.AuditEvents.AsNoTracking()
            .CountAsync(e => e.ActorUserId == user.UserId && e.Event == SecurityEvents.AccountDeleted);
        var authorization = authorizationId is { } id
            ? (await db.StepUpAuthorizations.AsNoTracking().SingleAsync(a => a.Id == id)).Status
            : (StepUpAuthorizationStatus?)null;
        var verification = await chain.VerifyAsync(db);

        return new Rows(accounts, deletedRows, authorization, verification.IsIntact);
    }

    /// <summary>
    /// The audit chain verifies at each of the reads. Two reads that are equal say only that the
    /// chain did not change between them, so each proof asks this of what it read after its request.
    /// </summary>
    private static void ChainShouldBeIntact(params Rows[] reads)
    {
        foreach (var read in reads)
        {
            read.ChainIntact.Should().BeTrue($"the audit chain verifies after the request; the read was: {read}");
        }
    }

    private static string Describe(HttpResponseMessage response, string body) =>
        $"{(int)response.StatusCode}, Retry-After {response.Headers.RetryAfter?.Delta?.ToString() ?? "none"}, body: {body}";

    private static void ShouldBeRefused(HttpResponseMessage response, string body, HttpStatusCode status, string errorCode)
    {
        response.StatusCode.Should().Be(status, $"the body was: {body}");
        JsonSerializer.Deserialize<JsonElement>(body).GetProperty("errorCode").GetString().Should().Be(errorCode);
        response.Headers.RetryAfter.Should().BeNull("nothing invites the caller to send it again");
    }

    private static Task ArmAsync(LostRowVersionRaceInterceptor race, Guid accountId)
    {
        race.Arm(accountId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The commit failed after the gate let it start, the write then lost every attempt it had
    /// left, and the answer is the 500 of a conflict left to propagate, with no wait named.
    /// </summary>
    private static void ShouldNotBeBusyAfterACommitStarted(
        FailedCommitInterceptor fault, LostRowVersionRaceInterceptor race, HttpResponseMessage response, string body)
    {
        fault.Fired.Should().BeTrue("the proof is void unless a commit the gate let start then failed");
        race.Fired.Should().Be(
            ConcurrencyRetry.MaxAttempts, "and unless the write then lost its account on the run that followed and on every later attempt");
        response.StatusCode.Should().Be(
            HttpStatusCode.InternalServerError, $"a request that let a commit start cannot say it wrote nothing; the body was: {body}");
        response.Headers.RetryAfter.Should().BeNull("nothing invites the caller to send it again");
        JsonSerializer.Deserialize<JsonElement>(body).TryGetProperty("retryAfterSeconds", out _).Should().BeFalse();
    }

    private void WriteTheMark(string write, HttpResponseMessage response, CommitMarkProbeInterceptor probe) =>
        _output.WriteLine(
            $"{write}: {(int)response.StatusCode}; commits that landed, mark set: [{string.Join(", ", probe.Commits)}]; "
            + $"saves that wrote rows outside a transaction, mark set: [{string.Join(", ", probe.SavesOutsideATransaction)}]");

    /// <summary>
    /// Every row the request wrote landed at a commit the gate marked: the commits counted all
    /// carry the mark when they have landed, which is when the probe reads it, and no save wrote
    /// rows outside a transaction, where no gate is passed. That the mark is set before a commit
    /// starts is held elsewhere:
    /// <see cref="AClosureThatLetACommitStart_AndThenLosesEveryAttempt_IsNotAnsweredBusy"/>,
    /// <see cref="AChangeOfPrimaryThatLetACommitStart_AndThenLosesEveryAttempt_IsNotAnsweredBusy"/>
    /// and <see cref="ARenameThatLetACommitStart_AndThenLosesEveryAttempt_IsNotAnsweredBusy"/>
    /// answer 500 after a commit that failed.
    /// </summary>
    private static void ShouldHaveLandedOnlyAtMarkedCommits(
        HttpResponseMessage response, CommitMarkProbeInterceptor probe, int commits)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the proof is void unless the write was made");
        probe.Commits.Should().Equal(
            Enumerable.Repeat(true, commits),
            "each commit of the write has set the mark by the time it lands; that the mark is set before the commit "
            + "starts is held by the three proofs of a commit let start that then failed");
        probe.SavesOutsideATransaction.Should().BeEmpty("a save outside a transaction commits by itself and sets no mark");
    }

    private static Task<Guid> MintDeletionAsync(HttpClient client, TestUser user, Guid accountId) =>
        MintAsync(client, user, $"/api/accounts/{accountId}/deletion-authorizations",
            new AccountDeletionAuthorizationRequest { Pin = Pin });

    private static Task<HttpResponseMessage> DeleteAsync(HttpClient client, TestUser user, Guid accountId, Guid authorizationId) =>
        SendAsync<object?>(client, user, HttpMethod.Delete, $"/api/accounts/{accountId}", null, authorizationId);

    private static Task<HttpResponseMessage> SetPrimaryAsync(HttpClient client, TestUser user, Guid accountId) =>
        SendAsync<object?>(client, user, HttpMethod.Patch, $"/api/accounts/{accountId}/set-primary", null);

    private static Task<HttpResponseMessage> RenameAsync(HttpClient client, TestUser user, Guid accountId) =>
        SendAsync(client, user, HttpMethod.Patch, $"/api/accounts/{accountId}", new UpdateAccountRequest { Name = NewName });

    private void WriteTheLog()
    {
        foreach (var line in Host.CapturedLog)
        {
            _output.WriteLine("  log " + line[..Math.Min(300, line.Length)]);
        }
    }
}
