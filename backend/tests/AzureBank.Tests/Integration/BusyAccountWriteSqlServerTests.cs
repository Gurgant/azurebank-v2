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
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The account writes that hold no idempotency key, past their attempts, on SQL Server: a closure,
/// a change of the primary account and a rename that lose the race for an account's row on every
/// attempt they are given.
/// </summary>
/// <remarks>
/// <para>
/// With no claim to read, what shows that nothing of the request was written is the request's
/// commit mark alone. So the first proofs here are about the mark: each save of a closure and of a
/// change of the primary account lands at a commit that sets it. Those two answer the busy 503,
/// which says nothing of <c>applied</c>, leave everything as it was, and are made when the same
/// request is sent again.
/// </para>
/// <para>
/// A rename's save commits by itself, outside any transaction, and sets no mark. So a rename is
/// not answered busy: its last conflict propagates, a 500, and the last two proofs hold both.
/// </para>
/// <para>
/// Each proof writes what it measured to the test output before it asserts, and asserts that its
/// fixtures fired.
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
    }

    [SqlServerFact]
    public async Task TheSaveOfARename_CommitsByItself_AndSetsNoMark()
    {
        var probe = new CommitMarkProbeInterceptor();
        var client = CreateSqlClient(host => host.AddInterceptor(probe));
        var user = await RegisterAsync(client, "rm");

        probe.Arm();
        using var response = await RenameAsync(client, user, user.PrimaryAccountId);
        probe.Disarm();

        WriteTheMark("rename", response, probe);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the proof is void unless the rename was made");
        probe.Commits.Should().BeEmpty("the rename's one statement rides no transaction, so no commit passes the gate");
        probe.SavesOutsideATransaction.Should().Equal(
            [false], "the save landed with the mark unset: for a rename the mark does not show that nothing was written");
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
        before.ChainIntact.Should().BeTrue();

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
    }

    // The rename.

    /// <summary>
    /// A rename that loses every attempt is not answered busy, since no mark covers its save
    /// (<see cref="TheSaveOfARename_CommitsByItself_AndSetsNoMark"/>): the conflict propagates.
    /// </summary>
    [SqlServerFact]
    public async Task ARenameThatLosesEveryAttempt_IsNotAnsweredBusy()
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

        using var again = await RenameAsync(client, user, user.PrimaryAccountId);
        var againBody = await again.Content.ReadAsStringAsync();
        var renamed = await ReadAsync(user, null, user.PrimaryAccountId);

        _output.WriteLine(
            $"rename: fired {race.Fired} of {ConcurrencyRetry.MaxAttempts} attempts; {Describe(response, body)}; "
            + $"before: {before}; after: {after}; the same PATCH again: {(int)again.StatusCode}; then: {renamed}");
        WriteTheLog();

        using var scope = new AssertionScope();
        race.Fired.Should().Be(ConcurrencyRetry.MaxAttempts, "the proof is void unless the rename lost every attempt it is given");
        response.StatusCode.Should().Be(
            HttpStatusCode.InternalServerError, $"no mark shows that a rename wrote nothing; the body was: {body}");
        response.Headers.RetryAfter.Should().BeNull("nothing invites the caller to send it again");
        JsonSerializer.Deserialize<JsonElement>(body).TryGetProperty("retryAfterSeconds", out _).Should().BeFalse();
        after.Should().Be(before, "the name is as it was");

        again.StatusCode.Should().Be(HttpStatusCode.OK, $"a rename sent again is made; the body was: {againBody}");
        renamed.Accounts[0]!.Name.Should().Be(NewName);
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
    /// carry the mark, and no save wrote rows outside a transaction, where no gate is passed.
    /// </summary>
    private static void ShouldHaveLandedOnlyAtMarkedCommits(
        HttpResponseMessage response, CommitMarkProbeInterceptor probe, int commits)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the proof is void unless the write was made");
        probe.Commits.Should().Equal(Enumerable.Repeat(true, commits), "each commit of the write sets the mark before it starts");
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
