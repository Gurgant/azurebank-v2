extern alias seeder;

using System.Net;
using System.Text.RegularExpressions;
using AzureBank.AuditVerifier.Commands;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit.Abstractions;
using PoolRunSummary = seeder::AzureBank.Seeder.Pool.PoolRunSummary;
using RecyclerControl = seeder::AzureBank.Seeder.Pool.RecyclerControl;

namespace AzureBank.Tests.Integration;

/// <summary>
/// What <c>recycle</c> does to the pool: it tops it up, deletes the copies whose time is over, and
/// says in one line and one exit code what it found.
/// </summary>
/// <remarks>
/// <para>
/// THE DELETE IS THE RISK. A copy is three users whose ledger rows point at each other, on a schema
/// that was built so that ledger rows cannot be deleted: foreign keys that restrict, a soft-delete
/// filter that hides closed accounts, two tables no foreign key covers. A delete that is wrong
/// fails, or leaves rows behind, only on a copy a visitor actually used. So the copy these tests
/// delete has been used through the API first: transfers, a closed account, a changed PIN.
/// </para>
/// <para>
/// EVERY "NOTHING HAPPENED" HAS A TWIN. A recycler that does nothing skips a copy with a live grant,
/// skips a copy a visitor just claimed, and leaves an ordinary user alone. Each such test is
/// paired with the run in which the same copy IS deleted, so the silence means something.
/// </para>
/// <para>
/// Time is moved by back-dating rows (a claim, a seed instant, a grant's expiry), not by a fake
/// clock: the recycler runs from the Seeder's own container, as the job does. The exception is
/// the two tests about WHEN a run reads the time. Time has to pass in the middle of those runs,
/// so that container is given a clock the test moves, and nothing else of it is changed.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class DemoPoolRecycleSqlServerTests(ITestOutputHelper output)
{
    private const int ConstraintViolated = 547;

    /// <summary>A day and an hour ago: past the 24 hours a copy works for, and the 5 minutes after.</summary>
    private static DateTime ADayAndAnHourAgo => DateTime.UtcNow.AddHours(-25);

    private static async Task<long> AuditRowsAsync(DemoPoolDatabase database)
    {
        await using var db = database.NewContext();
        return await db.AuditEvents.LongCountAsync();
    }

    private static void StillWhole(BuiltCopy? copy, string because)
    {
        copy.Should().NotBeNull(because);
        copy!.Users.Should().HaveCount(3, because);
        copy.Row.DeletedAt.Should().BeNull(because);
    }

    private static void ATombstone(BuiltCopy? copy, string because)
    {
        copy.Should().NotBeNull("a claimed copy that was deleted leaves its pool row as the record of it");
        copy!.Users.Should().BeEmpty(because);
        copy.Row.DeletedAt.Should().NotBeNull(because);
    }

    // "DELETE FROM [Accounts]", or the form a set-based delete is sent in: "DELETE FROM [a] FROM [Accounts] AS [a]".
    private static readonly Regex DeleteStatement = new(
        @"\bDELETE FROM \[(?<name>\w+)\](?:\s+FROM \[(?<table>\w+)\] AS \[\k<name>\])?", RegexOptions.Compiled);

    /// <summary>The table each DELETE statement of a run removes rows from: one entry per statement.</summary>
    private static List<string> TablesDeletedFrom(CommandTimeoutRecordingInterceptor sent) =>
    [
        .. sent.Commands
            .SelectMany(command => DeleteStatement.Matches(command.Text))
            .Select(statement => statement.Groups["table"].Success ? statement.Groups["table"].Value : statement.Groups["name"].Value),
    ];

    // ── A copy a visitor used ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What a thorough visitor leaves in a copy: an external and an internal transfer, a closed
    /// account that had movements, consumed authorisations and one never used, idempotency records,
    /// a changed PIN and its notice, a grant.
    /// </summary>
    private static async Task UseAsAVisitorAsync(DemoPoolDatabase database, BuiltCopy copy)
    {
        using var client = database.Api().CreateClient();
        var visitor = await DemoVisitor.SignInAsync(client, copy.Owner.Email!, DemoPoolDatabase.VisitorPassword);
        var accounts = await visitor.AccountsAsync();
        var savings = accounts.Single(a => a.IsPrimary);
        var checking = accounts.Single(a => !a.IsPrimary);

        var toJane = await DemoVisitor.AuthorizationOfAsync(visitor.MintTransferAsync(savings.Id, copy.Jane.AzureTag, 40m));
        using (var sent = await visitor.TransferAsync(savings.Id, copy.Jane.AzureTag, 40m, toJane))
        {
            sent.StatusCode.Should().Be(HttpStatusCode.Created, "ARRANGE: an external transfer to the copy's own contact");
        }

        var everything = await DemoVisitor.AuthorizationOfAsync(visitor.MintInternalTransferAsync(checking.Id, savings.Id, checking.Balance));
        using (var moved = await visitor.InternalTransferAsync(checking.Id, savings.Id, checking.Balance, everything))
        {
            moved.StatusCode.Should().Be(HttpStatusCode.Created, "ARRANGE: an internal transfer that empties the checking account");
        }

        var closure = await DemoVisitor.AuthorizationOfAsync(visitor.MintClosureAsync(checking.Id));
        using (var closed = await visitor.CloseAccountAsync(checking.Id, closure))
        {
            closed.StatusCode.Should().Be(HttpStatusCode.OK, "ARRANGE: the checking account, which had movements, is closed");
        }

        // An authorisation that is minted and never spent: a Pending row the delete must take too.
        await DemoVisitor.AuthorizationOfAsync(visitor.MintTransferAsync(savings.Id, copy.Mike.AzureTag, 15m));

        using var changed = await visitor.ChangePinAsync("123456", "654321");
        changed.StatusCode.Should().Be(HttpStatusCode.OK, "ARRANGE: the PIN is changed");
    }

    [SqlServerFact]
    public async Task ACopyAVisitorUsed_IsDeletedWhole_ItsAuditRowsStay_AndTheChainStillVerifies()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copies = await database.BuildCopiesAsync(3);
        var copy = copies[0];
        var clientKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        await database.GiveOwnerAPasswordAsync(copy);
        await database.MarkClaimedAsync(copy.Id, DateTime.UtcNow, clientKey);
        await UseAsAVisitorAsync(database, copy);

        // What the visit left, so "0 rows" below is said of tables that held some.
        var left = await database.RowsOfAsync(copy.UserIds);
        output.WriteLine("before: " + string.Join(", ", left.Select(t => $"{t.Key}={t.Value}")));
        left["Transactions"].Should().Be(30, "ARRANGE: 26 seeded rows and two transfers of two rows each");
        left["Accounts"].Should().Be(4, "ARRANGE: the closed account is still a row");
        left["StepUpAuthorizations"].Should().Be(4, "ARRANGE: three consumed and one pending");
        left["IdempotencyRecords"].Should().Be(2, "ARRANGE: one per transfer");
        left["RefreshTokens"].Should().BeGreaterThan(0, "ARRANGE: the visitor signed in");
        left["SubscriberNotices"].Should().BeGreaterThan(0, "ARRANGE: a changed PIN owes a notice");

        var auditBefore = await AuditRowsAsync(database);
        auditBefore.Should().BeGreaterThan(0, "ARRANGE: the visit wrote audit rows");
        await using (var db = database.NewContext())
        {
            (await db.AuditEvents.Select(e => e.ActorUserId).Distinct().ToListAsync())
                .Should().Equal(new Guid?[] { copy.Owner.Id }, "ARRANGE: every audit row is the demo user's");
            (await db.Accounts.IgnoreQueryFilters().CountAsync(a => a.UserId == copy.Owner.Id && a.IsDeleted))
                .Should().Be(1, "ARRANGE: one of the owner's accounts is closed");
        }

        // A day passes: the copy's time is over and the session it opened has ended.
        await database.BackdateClaimAsync(copy.Id, ADayAndAnHourAgo);
        await database.ExpireGrantsAsync(copy.UserIds);

        var sent = new CommandTimeoutRecordingInterceptor();
        var summary = await database.RecycleAsync(interceptors: sent);
        output.WriteLine(summary.ToLine());

        summary.Failures.Should().BeEmpty();
        (summary.DeletedExpired, summary.DeleteFailed, summary.Tombstones, summary.ExitCode).Should().Be((1, 0, 1, 0));

        // A delete of a used copy is the one statement here that can be slow, and a timeout is not
        // retried: the recycler gives its statements 120 seconds, not the 30 every other one has.
        var deletes = sent.Commands.Where(c => c.Text.Contains("DELETE", StringComparison.Ordinal)).ToList();
        deletes.Should().OnlyContain(c => c.TimeoutSeconds == 120);

        // SET-BASED: one DELETE per table, whatever the copy holds. Thirty ledger rows, four
        // accounts and three users leave in three statements; the two sweeps come after. A delete
        // that loaded the rows and removed them one by one would send a statement for each, and
        // the copy of a visitor who went on writing holds a few hundred.
        var deletedFrom = TablesDeletedFrom(sent);
        output.WriteLine("deletes: " + string.Join(", ", deletedFrom));
        deletedFrom.Should().BeEquivalentTo(
            new[] { "StepUpAuthorizations", "IdempotencyRecords", "Transactions", "Accounts", "AspNetUsers", "IdempotencyRecords", "RefreshTokens" },
            "the run sends seven DELETE statements: one for each table the recycler empties of a copy, and the two sweeps");

        // Nothing of the three users is left, in any table that held their rows.
        (await database.RowsOfAsync(copy.UserIds)).Should().OnlyContain(table => table.Value == 0);

        // The audit rows are all still there, their actor now names no user, and the pool row is
        // what says that actor was a demo copy.
        (await AuditRowsAsync(database)).Should().Be(auditBefore, "recycle removes no audit row and writes none");
        var tombstone = await database.CopyAsync(copy.Id);
        ATombstone(tombstone, "the copy's users are gone and its row stays as the record");
        tombstone!.Row.OwnerUserId.Should().Be(copy.Owner.Id, "it matches the actor of the audit rows that stayed");
        tombstone.Row.ClientKey.Should().BeNull("the hash of the client's address is removed with the copy");
        tombstone.Row.ClaimedAt.Should().NotBeNull();
        tombstone.Row.DeletedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));

        using var scope = database.Api().Services.CreateScope();
        var apiDb = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        (await apiDb.AuditEvents.Select(e => e.ActorUserId).Distinct().ToListAsync()).Should().Equal(new Guid?[] { copy.Owner.Id });
        (await apiDb.Users.AnyAsync(u => u.Id == copy.Owner.Id)).Should().BeFalse("the audit rows' actor resolves to no user");

        var verification = await scope.ServiceProvider.GetRequiredService<IAuditChain>().VerifyAsync(apiDb);
        verification.IsIntact.Should().BeTrue(because: verification.Reason);
        verification.Verified.Should().Be(auditBefore, "every row was read: an empty read also reports intact");

        // The two other copies were nobody's business.
        StillWhole(await database.CopyAsync(copies[1].Id), "a free copy is not touched");
        StillWhole(await database.CopyAsync(copies[2].Id), "a free copy is not touched");
    }

    /// <summary>
    /// What a copy's delete costs the evidence verb. The pack is assembled from the ledger row a
    /// number names, so a deleted copy's transfer has none, and the verb answers for it as for a
    /// number nobody issued; the audit row that names the transfer, and the authorisation that paid
    /// for it, stays as it was. TWIN: the same number, asked while the copy exists, is assembled.
    /// </summary>
    [SqlServerFact]
    public async Task ADeletedCopysTransfer_HasNoEvidencePack_ThoughTheAuditRowThatNamesItStays()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copy = (await database.BuildCopiesAsync(3))[0];
        await database.GiveOwnerAPasswordAsync(copy);
        await database.MarkClaimedAsync(copy.Id, DateTime.UtcNow);
        await UseAsAVisitorAsync(database, copy);

        AuditEvent named;
        string number;
        await using (var db = database.NewContext())
        {
            named = await db.AuditEvents.AsNoTracking().SingleAsync(e => e.Event == SecurityEvents.MoneyTransferred);
            number = await db.Transactions.Where(t => t.Id == named.SubjectId).Select(t => t.TransactionNumber).SingleAsync();
        }

        AuditDetails.ConsumedAuthorisationOf(named.Detail).Should().NotBeNull("ARRANGE: the audit row names the authorisation that paid");
        var api = database.Api();
        var (before, assembled) = await EvidenceCommand.RunAsync(api.Services, number, CancellationToken.None);
        before.Should().Be(VerifyCommand.Intact, "ARRANGE: while the copy exists, its transfer has a pack");
        assembled[0].Should().Be($"EVIDENCE PACK for {number}");

        await database.BackdateClaimAsync(copy.Id, ADayAndAnHourAgo);
        await database.ExpireGrantsAsync(copy.UserIds);
        (await database.RecycleAsync()).DeletedExpired.Should().Be(1, "ARRANGE: the copy's time is over and it is deleted");

        var (exitCode, lines) = await EvidenceCommand.RunAsync(api.Services, number, CancellationToken.None);

        exitCode.Should().Be(VerifyCommand.UsageError, "the number names no ledger row any more");
        lines[0].Should().Be(
            $"NOT ASSEMBLED: no transaction is numbered {number}.", "the verb answers as for a number nobody issued");
        await using (var db = database.NewContext())
        {
            (await db.AuditEvents.AsNoTracking().SingleAsync(e => e.Sequence == named.Sequence))
                .Should().BeEquivalentTo(named, "the audit row that names the transfer and its authorisation stays as it was");
        }
    }

    // ── Controls: the recycler's own delete, with one safeguard taken out ────────────────────────

    [SqlServerFact]
    public async Task DeletingTheOwnersRowsAlone_IsRefusedByTheLedgersOwnForeignKey()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copy = (await database.BuildCopiesAsync(3))[0];
        await database.MarkClaimedAsync(copy.Id, ADayAndAnHourAgo);
        var before = await database.RowsOfAsync(copy.UserIds);

        // The owner's transfers point at rows on the contacts' accounts, so the owner cannot go alone.
        var control = await database.RecycleAsync(control: RecyclerControl.OwnerOnly);

        var failure = control.Failures.Should().ContainSingle().Which;
        failure.CopyId.Should().Be(copy.Id);
        failure.ErrorNumber.Should().Be(ConstraintViolated);
        failure.Message.Should().Contain("FK_Transactions_Transactions_RelatedTransactionId");
        (control.DeletedExpired, control.DeleteFailed, control.ExitCode).Should().Be((0, 1, 14));
        (await database.RowsOfAsync(copy.UserIds)).Should().Equal(before, "the failed delete rolled back whole");
        StillWhole(await database.CopyAsync(copy.Id), "a copy whose delete failed is still a copy");

        // TWIN: the recycler as it ships deletes the same copy.
        var shipped = await database.RecycleAsync();

        (shipped.DeletedExpired, shipped.DeleteFailed).Should().Be((1, 0));
        ATombstone(await database.CopyAsync(copy.Id), "all three users go in one transaction");
    }

    [SqlServerFact]
    public async Task LeavingTheSoftDeleteFilterOn_IsRefusedOnTheClosedAccount()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copies = await database.BuildCopiesAsync(4);
        var (copy, untouched) = (copies[0], copies[1]);
        await database.MarkClaimedAsync(copy.Id, ADayAndAnHourAgo);
        await database.MarkClaimedAsync(untouched.Id, ADayAndAnHourAgo);
        await using (var db = database.NewContext())
        {
            // Closed as the API closes an account: the row stays, flagged.
            DateTime? now = DateTime.UtcNow;
            var closed = await db.Accounts
                .Where(a => a.UserId == copy.Owner.Id && !a.IsPrimary)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.IsDeleted, true).SetProperty(a => a.DeletedAt, now));
            closed.Should().Be(1, "ARRANGE: the owner's checking account is closed");
        }

        // The filter hides the closed account, and what it holds, from the delete; the rest of the
        // copy cannot go while they are there. Which foreign key says so depends on the statement
        // that meets them first, so the number is pinned and the name is one of the copy's three.
        var control = await database.RecycleAsync(control: RecyclerControl.KeepSoftDeleteFilter);

        var failure = control.Failures.Should().ContainSingle().Which;
        failure.CopyId.Should().Be(copy.Id, "the copy with the closed account is the one that cannot be deleted");
        failure.ErrorNumber.Should().Be(ConstraintViolated);
        failure.Message.Should().MatchRegex(
            "FK_(Accounts_AspNetUsers_UserId|Transactions_Accounts_AccountId|Transactions_Transactions_RelatedTransactionId)");
        (control.DeletedExpired, control.DeleteFailed, control.ExitCode).Should().Be(
            (1, 1, 14), "the copy with no closed account is deleted by the same run: the filter only matters where an account is closed");
        StillWhole(await database.CopyAsync(copy.Id), "the failed delete rolled back whole");
        ATombstone(await database.CopyAsync(untouched.Id), "nothing of this copy was hidden from the delete");

        // TWIN: the recycler as it ships deletes the same copy, closed account included.
        var shipped = await database.RecycleAsync();

        (shipped.DeletedExpired, shipped.DeleteFailed).Should().Be((1, 0));
        (await database.RowsOfAsync(copy.UserIds)).Should().OnlyContain(table => table.Value == 0);
    }

    // ── One copy that cannot be deleted ──────────────────────────────────────────────────────────

    /// <summary>
    /// Three expired copies (0, 1, 2), a free copy too old to hand out (3), and one fresh free copy
    /// (4): one short of the pool's target of 2, so the run has a copy to build.
    /// </summary>
    private static async Task<IReadOnlyList<BuiltCopy>> ThreeExpiredOneStaleOneFreshAsync(DemoPoolDatabase database)
    {
        var copies = await database.BuildCopiesAsync(5);
        foreach (var expired in copies.Take(3))
        {
            await database.MarkClaimedAsync(expired.Id, ADayAndAnHourAgo);
        }

        await database.BackdateSeedAsync(copies[3].Id, DateTime.UtcNow.AddHours(-45));
        return copies;
    }

    [SqlServerFact]
    public async Task ACopyThatCannotBeDeleted_IsCountedAndSkipped_AndTheRunGoesOn()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var log = new RecordingLoggerProvider();
        database.Log = log;
        var copies = await ThreeExpiredOneStaleOneFreshAsync(database);
        var poisoned = copies[1];
        var poison = FailingCommandInterceptor.OnDeleteNaming(await database.IdsOfAsync(poisoned));

        var summary = await database.RecycleAsync(interceptors: poison);
        output.WriteLine(summary.ToLine());

        poison.Failures.Should().BeGreaterThan(0, "the failure must actually have been injected, else the test proves nothing");
        var failure = summary.Failures.Should().ContainSingle().Which;
        failure.CopyId.Should().Be(poisoned.Id, "the copy that failed is named, so an operator can look at it");
        failure.Message.Should().Contain(FailingCommandInterceptor.Message);
        log.Lines.Should().Contain(
            line => line.Level == LogLevel.Error && line.Message.Contains(poisoned.Id.ToString(), StringComparison.Ordinal),
            "the run writes down the copy it could not delete, by its id: the summary line carries counts only");

        (summary.DeletedExpired, summary.DeleteFailed, summary.DeletedStaleFree).Should().Be(
            (2, 1, 1), "the other two expired copies and the stale free one are deleted all the same");
        StillWhole(await database.CopyAsync(poisoned.Id), "its delete rolled back whole");
        ATombstone(await database.CopyAsync(copies[0].Id), "the copy before the poisoned one is deleted");
        ATombstone(await database.CopyAsync(copies[2].Id), "the copy after the poisoned one is deleted");
        (await database.CopyAsync(copies[3].Id)).Should().BeNull("the stale free copy is deleted, row and all");

        (summary.FreeAtStart, summary.Seeded, summary.Free).Should().Be(
            (2, 1, 2), "two free copies at the start, one of them too old to count; one built, and the pool is at its target");
        summary.ExitCode.Should().Be(14);
    }

    [SqlServerFact]
    public async Task WithoutATryAroundEachCopy_TheSameFailureEndsTheRun()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copies = await ThreeExpiredOneStaleOneFreshAsync(database);
        var poisoned = copies[1];
        var poison = FailingCommandInterceptor.OnDeleteNaming(await database.IdsOfAsync(poisoned));

        // The control: the recycler's own run, with the try around each copy taken out.
        var act = () => database.RecycleAsync(
            control: RecyclerControl.NoTryPerCopy, interceptors: poison);

        (await act.Should().ThrowAsync<Exception>("nothing catches the failure, so the process would exit 1 and print no summary"))
            .Which.GetBaseException().Message.Should().Be(
                FailingCommandInterceptor.Message, "what ended the run is the poisoned copy's failure, and no other");
        poison.Failures.Should().BeGreaterThan(0);
        StillWhole(await database.CopyAsync(poisoned.Id), "its delete rolled back whole");
        (await database.CopyAsync(copies[3].Id)).Should().NotBeNull(
            "the run ended at the poisoned copy: the stale free copy, which comes after every expired one, was never reached");

        // What the visitors wait for was done first: inserts cannot be poisoned by a copy's data,
        // so the pool is at its target even though the run then failed.
        var freshSince = DateTime.UtcNow.AddHours(-44);
        (await database.CopiesAsync()).Count(c => c.Row.ClaimedAt is null && c.Row.CreatedAt > freshSince)
            .Should().Be(2, "the top-up comes before every delete");
    }

    [SqlServerFact]
    public async Task AFaultThatPasses_InTheMiddleOfADelete_IsRunAgain_AndTheCopyIsDeletedOnce()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copy = (await database.BuildCopiesAsync(3))[0];
        await database.MarkClaimedAsync(copy.Id, ADayAndAnHourAgo);

        // The first statement that names the accounts is the ledger's delete: the third of the
        // copy's five, so two have already run in the transaction that is lost with it.
        var fault = new TransientFailureInterceptor("[Accounts]");
        var summary = await database.RecycleAsync(interceptors: fault);

        fault.Fired.Should().BeTrue("the fault must actually have been injected, else the test proves nothing");
        summary.Failures.Should().BeEmpty("a fault the database recovers from is not the copy's failure");
        (summary.DeletedExpired, summary.DeleteFailed).Should().Be((1, 0), "the delete was run again, whole");
        ATombstone(await database.CopyAsync(copy.Id), "the second attempt deleted the copy");
        (await database.RowsOfAsync(copy.UserIds)).Should().OnlyContain(table => table.Value == 0);
    }

    [SqlServerFact]
    public async Task WhenRetriesRunOutOnADelete_TheFailureSaysWhatTheDatabaseSaid()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copy = (await database.BuildCopiesAsync(3))[0];
        await database.MarkClaimedAsync(copy.Id, ADayAndAnHourAgo);

        // The same fault on a run that is given no retry: the strategy reports that it ran out,
        // with the fault inside it.
        var fault = new TransientFailureInterceptor("[Accounts]");
        var summary = await database.RecycleAsync(settings: new() { ["Database:MaxRetryCount"] = "0" }, interceptors: fault);

        fault.Fired.Should().BeTrue("the fault must actually have been injected, else the test proves nothing");
        var failure = summary.Failures.Should().ContainSingle().Which;
        failure.CopyId.Should().Be(copy.Id);
        failure.Message.Should().Be(
            "Injected transient fault on: [Accounts]",
            "the root cause: 'the maximum number of retries was exceeded' is all an operator would read otherwise");
        (summary.DeletedExpired, summary.DeleteFailed, summary.ExitCode).Should().Be((0, 1, 14));
        StillWhole(await database.CopyAsync(copy.Id), "its delete rolled back whole");
    }

    [SqlServerFact]
    public async Task ARunThatIsStopped_Ends_AndBlamesNoCopy()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var log = new RecordingLoggerProvider();
        database.Log = log;
        var copy = (await database.BuildCopiesAsync(3))[0];
        await database.MarkClaimedAsync(copy.Id, ADayAndAnHourAgo);

        // The job is stopped while the run is inside the copy's delete.
        using var stop = new CancellationTokenSource();
        var (run, hold) = await ARunParkedBeforeItReadsACopysGrantsAsync(database, stop.Token);
        await stop.CancelAsync();
        hold.Release();

        await FluentActions.Awaiting(() => run).Should().ThrowAsync<OperationCanceledException>(
            "a run that is stopped ends: it prints no summary, and the next run starts from the rows as they are");
        log.Lines.Should().NotContain(
            line => line.Level == LogLevel.Error && line.Message.Contains(copy.Id.ToString(), StringComparison.Ordinal),
            "being stopped is nothing the copy did: it is not written down as a copy that could not be deleted");
        StillWhole(await database.CopyAsync(copy.Id), "the delete that was under way rolled back whole");
    }

    [SqlServerFact]
    public async Task ARunThatIsStopped_WhileADeleteWaitsOnTheServer_Ends_AndBlamesNoCopy()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var log = new RecordingLoggerProvider();
        database.Log = log;
        var copy = (await database.BuildCopiesAsync(3))[0];
        await database.MarkClaimedAsync(copy.Id, ADayAndAnHourAgo);

        // Another session holds the owner's accounts, so the copy's delete is sent and then waits
        // for them. The job is stopped while it waits: the stop reaches a statement that is
        // already with the database.
        using var stop = new CancellationTokenSource();
        Exception ended;
        await using (var held = await database.HoldAsync(
            "UPDATE [Accounts] SET [Name] = [Name] WHERE [UserId] = @owner", new SqlParameter("@owner", copy.Owner.Id)))
        {
            var run = database.RecycleAsync(stop: stop.Token);
            await held.AStatementWaitsAsync();
            await stop.CancelAsync();

            ended = (await FluentActions.Awaiting(() => run).Should().ThrowAsync<Exception>(
                "a run that is stopped ends: it prints no summary, and the next run starts from the rows as they are")).Which;
        }

        log.Lines.Should().NotContain(
            line => line.Level == LogLevel.Error && line.Message.Contains(copy.Id.ToString(), StringComparison.Ordinal),
            "being stopped is nothing the copy did, whichever way the stop comes back: it is not written down as a copy that could not be deleted");

        // Measured on SQL Server 17.0 (LocalDB): SqlException, error 0, "Operation cancelled by
        // user." Were it a cancellation, this test would be the one above over again.
        ended.GetBaseException().Should().BeOfType<SqlException>(
            "ARRANGE: a stop that reaches a statement on the server comes back as the database's own error, not as a cancellation");
        StillWhole(await database.CopyAsync(copy.Id), "the delete that was under way rolled back whole");
    }

    [SqlServerFact]
    public async Task ACopyThatCannotBeDeleted_IsTriedOnceInARun_EvenPastTheBackstop()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copy = (await database.BuildCopiesAsync(3))[0];

        // Past its lifetime and past the backstop: two rules name this copy, and it is still one copy.
        await database.MarkClaimedAsync(copy.Id, DateTime.UtcNow.AddHours(-100));
        var poison = FailingCommandInterceptor.OnDeleteNaming(await database.IdsOfAsync(copy));

        var summary = await database.RecycleAsync(interceptors: poison);

        poison.Failures.Should().Be(
            1, "its delete is sent once: a second try in the same run would fail the same way and count the copy twice");
        summary.Failures.Should().ContainSingle().Which.CopyId.Should().Be(copy.Id);
        (summary.DeletedExpired, summary.DeletedHardStop, summary.DeleteFailed, summary.ExitCode).Should().Be((0, 0, 1, 14));
        StillWhole(await database.CopyAsync(copy.Id), "its delete rolled back whole");
    }

    // ── A copy in use ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts a run and parks it just before it reads a copy's grants: it has counted what it
    /// found, its top-up is done, and it is inside the transaction that deletes the first copy
    /// whose time is over. What a test does before it releases the run has committed by the time
    /// the run reads.
    /// </summary>
    private static async Task<(Task<PoolRunSummary> Run, HoldingReadInterceptor Hold)> ARunParkedBeforeItReadsACopysGrantsAsync(
        DemoPoolDatabase database, CancellationToken stop = default, Dictionary<string, string?>? settings = null)
    {
        var hold = new HoldingReadInterceptor("[RevokedAt] IS NULL");
        var run = database.RecycleAsync(settings, stop: stop, interceptors: hold);
        (await Task.WhenAny(hold.Held, run)).Should().BeSameAs(
            hold.Held, "the run must be parked before its read of the grants, or the order below is not the one under test");
        return (run, hold);
    }

    [SqlServerFact]
    public async Task ACopyWithALiveGrant_IsSkipped_AndDeletedOnceTheGrantIsRevoked()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copy = (await database.BuildCopiesAsync(3))[0];
        await database.ClaimForAVisitorAsync(copy, DateTime.UtcNow);
        using (var client = database.Api().CreateClient())
        {
            await DemoVisitor.SignInAsync(client, copy.Owner.Email!, DemoPoolDatabase.VisitorPassword);
        }

        // Its time is over, but a session opened just before the end is still running.
        await database.BackdateClaimAsync(copy.Id, ADayAndAnHourAgo);

        var skipped = await database.RecycleAsync();

        (skipped.DeletedExpired, skipped.DeletedHardStop, skipped.DeleteFailed, skipped.Claimed).Should().Be(
            (0, 0, 0, 1), "a copy is never deleted under a live session");
        StillWhole(await database.CopyAsync(copy.Id), "the grant is live");

        // TWIN: the session ends, and the next run deletes the copy.
        (await database.RevokeGrantsAsync(copy.UserIds)).Should().Be(1, "ARRANGE: the one grant the sign-in issued");

        var deleted = await database.RecycleAsync();

        (deleted.DeletedExpired, deleted.DeletedHardStop, deleted.Claimed).Should().Be((1, 0, 0));
        ATombstone(await database.CopyAsync(copy.Id), "no grant is live any more");
    }

    [SqlServerFact]
    public async Task AGrantThatHasExpired_KeepsNoCopy_EvenWhenTheSweepHasNotTakenIt()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copy = (await database.BuildCopiesAsync(3))[0];
        await database.MarkClaimedAsync(copy.Id, ADayAndAnHourAgo);

        // A grant that nobody revoked and that has expired. The sweep that removes such grants
        // comes after the copies, so this one is there when the copy's grants are read.
        await using (var db = database.NewContext())
        {
            db.RefreshTokens.Add(AGrant(copy.Owner.Id, expiresAt: DateTime.UtcNow.AddMinutes(-1)));
            await db.SaveChangesAsync();
        }

        var summary = await database.RecycleAsync();

        summary.Failures.Should().BeEmpty();
        (summary.SweptGrants, summary.DeletedExpired).Should().Be(
            (0, 1), "a session that has ended is not a session: what keeps a copy is a grant that is neither revoked nor expired");
        ATombstone(await database.CopyAsync(copy.Id), "the expired grant left with its user");
        (await database.RowsOfAsync(copy.UserIds)).Should().OnlyContain(table => table.Value == 0);
    }

    /// <summary>
    /// A GUARD for the delete as it is: the copy's grants leave with its users, and the recycler
    /// has no statement of its own for them. A grant can name the one that replaced it, through a
    /// foreign key that restricts; the database checks that key when the statement ends, and by
    /// then both grants are gone. So the links need no clearing first, and this is what would
    /// say so if that stopped being true.
    /// </summary>
    [SqlServerFact]
    public async Task ACopyWhoseGrantsNameEachOther_IsDeletedLikeAnyOther()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copy = (await database.BuildCopiesAsync(3))[0];
        await database.MarkClaimedAsync(copy.Id, ADayAndAnHourAgo);

        // Two grants the sweep leaves alone (neither has expired) and that keep nothing alive (both
        // are revoked), the older naming the newer.
        var now = DateTime.UtcNow;
        var newer = AGrant(copy.Owner.Id, expiresAt: now.AddHours(1));
        var older = AGrant(copy.Owner.Id, expiresAt: now.AddHours(1));
        newer.RevokedAt = older.RevokedAt = now;
        older.ReplacedByTokenId = newer.Id;
        await using (var db = database.NewContext())
        {
            db.RefreshTokens.Add(newer);
            await db.SaveChangesAsync();
            db.RefreshTokens.Add(older);
            await db.SaveChangesAsync();
        }

        var summary = await database.RecycleAsync();

        summary.Failures.Should().BeEmpty();
        (summary.SweptGrants, summary.DeletedExpired, summary.DeleteFailed).Should().Be(
            (0, 1, 0), "the sweep took neither grant, and the copy went with both");
        (await database.RowsOfAsync(copy.UserIds)).Should().OnlyContain(table => table.Value == 0);
        ATombstone(await database.CopyAsync(copy.Id), "a link between two grants of the same user stops nothing");
    }

    [SqlServerFact]
    public async Task PastTheBackstop_ACopyIsDeleted_EvenWithALiveGrant()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copies = await database.BuildCopiesAsync(4);
        var (insideTheBackstop, pastTheBackstop) = (copies[0], copies[1]);
        using (var client = database.Api().CreateClient())
        {
            foreach (var copy in new[] { insideTheBackstop, pastTheBackstop })
            {
                await database.ClaimForAVisitorAsync(copy, DateTime.UtcNow);
                await DemoVisitor.SignInAsync(client, copy.Owner.Email!, DemoPoolDatabase.VisitorPassword);
            }
        }

        // The backstop is the lifetime plus 48 hours: 72 hours with the default lifetime of 24.
        await database.BackdateClaimAsync(insideTheBackstop.Id, DateTime.UtcNow.AddHours(-71));
        await database.BackdateClaimAsync(pastTheBackstop.Id, DateTime.UtcNow.AddHours(-73));

        var summary = await database.RecycleAsync();

        (summary.DeletedExpired, summary.DeletedHardStop, summary.DeleteFailed).Should().Be(
            (0, 1, 0), "hardStop is its own count: above 0 it says sign-in went on being accepted past a copy's end");
        ATombstone(await database.CopyAsync(pastTheBackstop.Id), "no copy outlives the backstop, grant or no grant");
        StillWhole(await database.CopyAsync(insideTheBackstop.Id), "inside the backstop a live grant still protects the copy");
    }

    [SqlServerFact]
    public async Task TheBackstop_IsTheConfiguredLifetimePlusFortyEightHours()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copy = (await database.BuildCopiesAsync(3))[0];
        await database.ClaimForAVisitorAsync(copy, DateTime.UtcNow);
        using (var client = database.Api().CreateClient())
        {
            await DemoVisitor.SignInAsync(client, copy.Owner.Email!, DemoPoolDatabase.VisitorPassword);
        }

        // Claimed 51 hours ago, and its session is still running.
        await database.BackdateClaimAsync(copy.Id, DateTime.UtcNow.AddHours(-51));

        // CONTROL: with the default lifetime of 24 hours the backstop is at 72, so the grant
        // still protects the copy.
        var inside = await database.RecycleAsync();

        (inside.DeletedExpired, inside.DeletedHardStop).Should().Be((0, 0));
        StillWhole(await database.CopyAsync(copy.Id), "51 hours of 72, and a live grant");

        // With a lifetime of 2 hours the backstop is at 50.
        var summary = await database.RecycleAsync(settings: new() { ["Demo:CopyLifetimeHours"] = "2" });

        (summary.DeletedExpired, summary.DeletedHardStop, summary.DeleteFailed).Should().Be((0, 1, 0));
        ATombstone(await database.CopyAsync(copy.Id), "51 hours of 50: past the backstop, grant or no grant");
    }

    [SqlServerFact]
    public async Task ACopy_IsLeftAloneUntilFiveMinutesPastItsLifetime()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copy = (await database.BuildCopiesAsync(3))[0];

        // 24 hours and 2 minutes: sign-in has ended, and a request that started just before the end
        // may still be running.
        await database.MarkClaimedAsync(copy.Id, DateTime.UtcNow.AddHours(-24).AddMinutes(-2));

        var early = await database.RecycleAsync();

        early.DeletedExpired.Should().Be(0);
        StillWhole(await database.CopyAsync(copy.Id), "the copy ended less than 5 minutes ago");

        // TWIN: 24 hours and 7 minutes.
        await database.BackdateClaimAsync(copy.Id, DateTime.UtcNow.AddHours(-24).AddMinutes(-7));

        var late = await database.RecycleAsync();

        late.DeletedExpired.Should().Be(1);
        ATombstone(await database.CopyAsync(copy.Id), "the copy ended more than 5 minutes ago");
    }

    [SqlServerFact]
    public async Task TheLifetime_IsTheConfiguredOne()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copy = (await database.BuildCopiesAsync(3))[0];
        await database.MarkClaimedAsync(copy.Id, DateTime.UtcNow.AddHours(-3));

        // CONTROL: with the default of 24 hours, a copy claimed 3 hours ago is in use.
        (await database.RecycleAsync()).DeletedExpired.Should().Be(0);
        StillWhole(await database.CopyAsync(copy.Id), "3 hours of 24");

        var summary = await database.RecycleAsync(settings: new() { ["Demo:CopyLifetimeHours"] = "2" });

        summary.DeletedExpired.Should().Be(1);
        ATombstone(await database.CopyAsync(copy.Id), "3 hours of 2");
    }

    // ── Free copies too old to hand out ──────────────────────────────────────────────────────────

    [SqlServerFact]
    public async Task AStaleFreeCopy_IsDeletedAndLeavesNoRow_AndThePoolIsBackAtItsTarget()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copies = await database.BuildCopiesAsync(2);
        var stale = copies[0];
        await database.BackdateSeedAsync(stale.Id, DateTime.UtcNow.AddHours(-45));

        var summary = await database.RecycleAsync();
        output.WriteLine(summary.ToLine());

        (summary.FreeAtStart, summary.Seeded, summary.DeletedStaleFree, summary.Free, summary.Tombstones).Should().Be(
            (2, 1, 1, 2, 0), "two copies were free, only the fresh one counted towards the target, one was built first, and the stale one was then deleted");
        (await database.CopyAsync(stale.Id)).Should().BeNull("nothing ever pointed at a free copy, so no record of it is kept");
        (await database.RowsOfAsync(stale.UserIds)).Should().OnlyContain(table => table.Value == 0);
        StillWhole(await database.CopyAsync(copies[1].Id), "a fresh free copy is not touched");
        summary.ExitCode.Should().Be(0, "one fresh free copy is not below a low mark of 0, and the pool was not empty");
    }

    [SqlServerFact]
    public async Task HowLongAFreeCopyIsKept_IsTheConfiguredAge()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copies = await database.BuildCopiesAsync(2);
        var older = copies[0];
        await database.BackdateSeedAsync(older.Id, DateTime.UtcNow.AddHours(-11));

        // CONTROL: 11 hours of the default 44. The copy is fresh: it counts, and it stays.
        var kept = await database.RecycleAsync();

        (kept.FreeAtStart, kept.Seeded, kept.DeletedStaleFree).Should().Be((2, 0, 0));
        StillWhole(await database.CopyAsync(older.Id), "11 hours of 44");

        // 11 hours of 10: it no longer counts, one is built in its place, and it is deleted.
        var summary = await database.RecycleAsync(settings: new() { ["Demo:Pool:MaxFreeAgeHours"] = "10" });

        (summary.FreeAtStart, summary.Seeded, summary.DeletedStaleFree, summary.Free).Should().Be((2, 1, 1, 2));
        (await database.CopyAsync(older.Id)).Should().BeNull("11 hours of 10");
    }

    [SqlServerFact]
    public async Task WhenTheAnswerToADeletesCommitIsLost_TheCopyIsCountedOnce_AndIsNotAFailure()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copies = await database.BuildCopiesAsync(2);
        var stale = copies[0];
        await database.BackdateSeedAsync(stale.Id, DateTime.UtcNow.AddHours(-45));

        // The delete of the stale copy commits, and the answer never arrives. The last statement of
        // that delete is the one that removes the pool row.
        var lost = new LostCommitAnswerInterceptor("DELETE", "[DemoCopies]");
        var summary = await database.RecycleAsync(interceptors: lost);

        lost.Fired.Should().BeTrue("the answer must actually have been lost, else the test proves nothing");
        summary.Failures.Should().BeEmpty();
        (summary.DeletedStaleFree, summary.DeleteFailed, summary.Free).Should().Be(
            (1, 0, 2),
            "the copy is gone, so it is counted: a recycler that ran the delete again would find nothing to take and report that it skipped a copy it had deleted");
        (await database.CopyAsync(stale.Id)).Should().BeNull();
        (await database.RowsOfAsync(stale.UserIds)).Should().OnlyContain(table => table.Value == 0);
    }

    [SqlServerFact]
    public async Task WhenAStaleCopysDeleteIsRunAgain_AndAVisitorHasClaimedItSince_TheCopyIsNotCountedAsDeleted()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copies = await database.BuildCopiesAsync(2);
        var stale = copies[0];
        await database.BackdateSeedAsync(stale.Id, DateTime.UtcNow.AddHours(-45));

        // The delete of the stale copy reaches its commit, the commit fails and nothing of it
        // lands: the copy is free again. A visitor claims it before the delete is run again.
        var failed = new FailedCommitInterceptor(
            () => database.MarkClaimedAsync(stale.Id, DateTime.UtcNow), "DELETE", "[DemoCopies]");
        var summary = await database.RecycleAsync(interceptors: failed);

        failed.Fired.Should().BeTrue("the commit must actually have failed, else the test proves nothing");
        summary.Failures.Should().BeEmpty("losing the copy to a visitor is not a failure");
        (summary.DeletedStaleFree, summary.DeleteFailed).Should().Be(
            (0, 0), "a run reports what its last attempt at a copy did: the first got as far as its commit, and the second found the copy taken");
        var claimed = await database.CopyAsync(stale.Id);
        StillWhole(claimed, "the visitor has it");
        claimed!.Row.ClaimedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1), "the claim that stands is the visitor's");
    }

    [SqlServerFact]
    public async Task AStaleFreeCopyAVisitorClaimsFirst_IsSkipped()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copies = await database.BuildCopiesAsync(3);
        var stale = copies[0];
        await database.BackdateSeedAsync(stale.Id, DateTime.UtcNow.AddHours(-45));

        // The claim commits between the recycler's choice of the copy and its guard: the guard is
        // the visitor's own conditional UPDATE, so it now affects no row.
        var race = new OutOfBandClaimInterceptor(database.ConnectionString, stale.Id);
        var summary = await database.RecycleAsync(interceptors: race);

        race.Fired.Should().BeTrue("the out-of-band claim must actually have run, else the test proves nothing");
        race.OutOfBandRowsAffected.Should().Be(1, "and it must have won: the copy was still free when it ran");

        (summary.DeletedStaleFree, summary.DeleteFailed).Should().Be((0, 0), "a copy a visitor holds is not deleted, and losing the race is not a failure");
        var claimed = await database.CopyAsync(stale.Id);
        StillWhole(claimed, "the visitor has it");
        claimed!.Row.ClaimedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1), "the claim that stands is the visitor's");
        (await database.RowsOfAsync(stale.UserIds))["Transactions"].Should().Be(26);
    }

    // ── The wrong database, and a user that is nobody's copy ─────────────────────────────────────

    [SqlServerFact]
    public async Task OnADatabaseWithUsersAndNoPool_RecycleExitsThirteen_AndWritesNothing()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        using var client = database.Api().CreateClient();
        var (resident, _, account) = await DemoVisitor.RegisterAsync(client, "resident");
        (await resident.DepositAsync(account, 75m)).StatusCode.Should().Be(HttpStatusCode.Created);

        // Rows the sweeps would take on a pool's database: an expired grant and an expired record.
        await database.ExpireGrantsAsync([resident.UserId]);
        await using (var db = database.NewContext())
        {
            var expired = DateTime.UtcNow.AddHours(-1);
            await db.IdempotencyRecords.ExecuteUpdateAsync(s => s.SetProperty(r => r.ExpiresAt, expired));
        }

        var before = await database.RowsOfAsync([resident.UserId]);
        before["RefreshTokens"].Should().Be(1, "ARRANGE");
        before["IdempotencyRecords"].Should().Be(1, "ARRANGE");

        var summary = await database.RecycleAsync();
        output.WriteLine(summary.ToLine());

        (summary.ForeignUsers, summary.Seeded, summary.SweptGrants, summary.SweptIdempotency, summary.ExitCode).Should().Be(
            (1, 0, 0, 0, 13), "users and not one pool row is the signature of the wrong database");
        (await database.CopiesAsync()).Should().BeEmpty("no copy was built beside somebody's users");
        (await database.RowsOfAsync([resident.UserId])).Should().Equal(before, "nothing was swept either: the refusal comes before any write");
    }

    [SqlServerFact]
    public async Task BesideAPool_AnOrdinaryUserLosesNothing_AndAnExpiredCopyIsStillDeleted()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copy = (await database.BuildCopiesAsync(3))[0];
        await database.MarkClaimedAsync(copy.Id, ADayAndAnHourAgo);

        using var client = database.Api().CreateClient();
        var (resident, _, account) = await DemoVisitor.RegisterAsync(client, "resident");
        (await resident.DepositAsync(account, 75m)).StatusCode.Should().Be(HttpStatusCode.Created);
        var before = await database.RowsOfAsync([resident.UserId]);
        before["Transactions"].Should().Be(1, "ARRANGE: the ordinary user has a ledger row");
        before["RefreshTokens"].Should().Be(1, "ARRANGE: and a grant that has not expired");
        before["IdempotencyRecords"].Should().Be(1, "ARRANGE: and an idempotency record that has not expired");

        // Beside those, a grant and a record of the same user that HAVE expired.
        var anHourAgo = DateTime.UtcNow.AddHours(-1);
        await using (var db = database.NewContext())
        {
            db.RefreshTokens.Add(AGrant(resident.UserId, expiresAt: anHourAgo));
            db.IdempotencyRecords.Add(ARecord(resident.UserId, expiresAt: anHourAgo));
            await db.SaveChangesAsync();
        }

        var summary = await database.RecycleAsync();

        (summary.ForeignUsers, summary.ExitCode).Should().Be((1, 13), "a user outside every copy is reported");

        // The two sweeps take what has expired, whoever it belongs to, as the API's own clean-up
        // does. Everything else of the ordinary user is still there: the delete of a copy is keyed
        // on the copy.
        (summary.SweptGrants, summary.SweptIdempotency).Should().Be((1, 1), "expired grants and records are swept for everyone, not only for copies");
        (await database.RowsOfAsync([resident.UserId])).Should().Equal(
            before, "a user of no copy loses nothing that has not expired: not the live grant, not the live record, not a ledger row");

        // TWIN: in the same run, the copy whose time was over is deleted.
        summary.DeletedExpired.Should().Be(1);
        ATombstone(await database.CopyAsync(copy.Id), "the run went on past the report");
    }

    // ── The sweeps ───────────────────────────────────────────────────────────────────────────────

    [SqlServerFact]
    public async Task ExpiredGrantsAndIdempotencyRecords_AreSwept_AndLiveOnesStay()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copy = (await database.BuildCopiesAsync(3))[0];
        await database.MarkClaimedAsync(copy.Id, DateTime.UtcNow);
        var owner = copy.Owner.Id;

        var hour = TimeSpan.FromHours(1);
        var now = DateTime.UtcNow;
        var expiredGrant = AGrant(owner, expiresAt: now - hour);
        var liveGrant = AGrant(owner, expiresAt: now + hour);
        // A live grant that names an expired one: the link has to be cleared before the delete, or
        // the self-reference refuses it.
        liveGrant.ReplacedByTokenId = expiredGrant.Id;

        // And a grant that was revoked and has not expired: a session its user signed out of.
        var revokedGrant = AGrant(owner, expiresAt: now + hour);
        revokedGrant.RevokedAt = now;
        await using (var db = database.NewContext())
        {
            db.RefreshTokens.Add(expiredGrant);
            await db.SaveChangesAsync();
            db.RefreshTokens.AddRange(liveGrant, revokedGrant);
            db.IdempotencyRecords.AddRange(
                ARecord(owner, expiresAt: now - hour), ARecord(owner, expiresAt: now - hour), ARecord(owner, expiresAt: now + hour));
            await db.SaveChangesAsync();
        }

        var summary = await database.RecycleAsync();

        (summary.SweptGrants, summary.SweptIdempotency, summary.DeleteFailed).Should().Be(
            (1, 2, 0), "one expired grant and two expired records: each sweep is counted under its own name");
        await using (var db = database.NewContext())
        {
            var grants = await db.RefreshTokens.AsNoTracking().ToListAsync();
            grants.Select(grant => grant.Id).Should().BeEquivalentTo(
                new[] { liveGrant.Id, revokedGrant.Id },
                "a grant is swept when it has expired, as the API's clean-up does it: not when it is revoked, and not while it is live");
            grants.Single(grant => grant.Id == liveGrant.Id).ReplacedByTokenId.Should().BeNull("the link to the swept grant was cleared");
            (await db.IdempotencyRecords.AsNoTracking().SingleAsync()).ExpiresAt.Should().BeAfter(now);
        }

        StillWhole(await database.CopyAsync(copy.Id), "a copy claimed today is in use");
    }

    [SqlServerFact]
    public async Task AnExpiredRecordOfAnOperationThatNeverStoredItsAnswer_IsWrittenDown_BeforeItIsSwept()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var log = new RecordingLoggerProvider();
        database.Log = log;
        var copy = (await database.BuildCopiesAsync(3))[0];
        await database.MarkClaimedAsync(copy.Id, DateTime.UtcNow);
        var owner = copy.Owner.Id;

        // Three records of one user. The first committed its operation and never stored the
        // answer, and has expired: the API's own clean-up writes a Warning for such a record
        // before it removes it. The second stored its answer. The third is still inside its day.
        var hour = TimeSpan.FromHours(1);
        var now = DateTime.UtcNow;
        var unanswered = ARecord(owner, expiresAt: now - hour, IdempotencyStatus.Executed);
        var answered = ARecord(owner, expiresAt: now - hour);
        var underWay = ARecord(owner, expiresAt: now + hour, IdempotencyStatus.Executed);
        await using (var db = database.NewContext())
        {
            db.IdempotencyRecords.AddRange(unanswered, answered, underWay);
            await db.SaveChangesAsync();
        }

        var summary = await database.RecycleAsync();

        summary.SweptIdempotency.Should().Be(2, "ARRANGE: both expired records are swept");
        var written = log.Lines
            .Where(line => new[] { unanswered, answered, underWay }.Any(
                record => line.Message.Contains(record.Key.ToString(), StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var warning = written.Should().ContainSingle(
            "the record that is removed without ever having been answered is the one a run writes down, as the API's clean-up does").Which;
        warning.Level.Should().Be(LogLevel.Warning);
        warning.Message.Should().Contain(unanswered.Key.ToString()).And.Contain(owner.ToString()).And.Contain(unanswered.Endpoint);
    }

    [SqlServerFact]
    public async Task WhenASweepFails_TheRunFails_AndTheCopiesWhoseTimeIsOverAreDeletedAllTheSame()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copies = await database.BuildCopiesAsync(3);
        var (expired, stale) = (copies[0], copies[1]);
        await database.MarkClaimedAsync(expired.Id, ADayAndAnHourAgo);
        await database.BackdateSeedAsync(stale.Id, DateTime.UtcNow.AddHours(-45));

        // The sweep of expired idempotency records is refused. That is no copy's failure, so no
        // exit code of the pool's names it: the run ends as a failed one, with no summary.
        var refused = FailingCommandInterceptor.OnText("DELETE", "[IdempotencyRecords]", "[ExpiresAt]");
        var act = () => database.RecycleAsync(interceptors: refused);

        (await act.Should().ThrowAsync<Exception>("a sweep that fails is not passed over in silence: the process would exit 1"))
            .Which.GetBaseException().Message.Should().Be(FailingCommandInterceptor.Message);
        refused.Failures.Should().Be(1, "the failure must actually have been injected, and into the sweep alone");

        // But the sweeps are housekeeping the API does as well, and they come last: what only a
        // run does was done before the sweep was reached.
        ATombstone(await database.CopyAsync(expired.Id), "the copy whose time was over is deleted before the sweeps");
        (await database.CopyAsync(stale.Id)).Should().BeNull("and so is the free copy that was too old to hand out");
        var freshSince = DateTime.UtcNow.AddHours(-44);
        (await database.CopiesAsync()).Count(c => c.Row.ClaimedAt is null && c.Row.CreatedAt > freshSince)
            .Should().Be(2, "and the pool was topped up first of all");
    }

    // ── When a run reads the time ────────────────────────────────────────────────────────────────

    [SqlServerFact]
    public async Task ACopyWhoseTimeEndsWhileTheTopUpRuns_IsDeletedByTheSameRun()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copies = await database.BuildCopiesAsync(2);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var started = clock.GetUtcNow().UtcDateTime;

        // Ten minutes short of 24 hours and 5 minutes: when the run starts, the copy's time is not
        // over yet. The other copy is free, one of a target of two, so the run has a copy to build.
        await database.MarkClaimedAsync(copies[0].Id, started.AddHours(-24).AddMinutes(-5).AddMinutes(10));
        database.AlsoRegister = services => services.AddSingleton<TimeProvider>(clock);

        // The run is parked at the first read of its top-up, and half an hour passes there: a
        // top-up of fifty copies on a small database is not instant.
        var hold = new HoldingReadInterceptor("[AspNetRoles]");
        var run = database.RecycleAsync(interceptors: hold);
        (await Task.WhenAny(hold.Held, run)).Should().BeSameAs(
            hold.Held, "the run must be parked inside its top-up, or the order below is not the one under test");
        clock.Advance(TimeSpan.FromMinutes(30));
        hold.Release();
        var summary = await run;

        (summary.Seeded, summary.DeletedExpired).Should().Be(
            (1, 1), "the copies whose time is over are chosen as of the end of the top-up, not as of the start of the run");
        var tombstone = await database.CopyAsync(copies[0].Id);
        ATombstone(tombstone, "its time ended while the top-up ran");
        tombstone!.Row.DeletedAt.Should().Be(started.AddMinutes(30), "the record carries the instant of the delete");
    }

    /// <summary>
    /// The other way round from <see cref="ACopyWhoseTimeEndsWhileTheTopUpRuns_IsDeletedByTheSameRun"/>:
    /// a FREE copy that grows too old while the top-up runs was counted as fresh by that top-up, so
    /// no copy was built in its place. Deleted by the same run, it would leave the pool one short
    /// until the next, with no code that says so. It is left for the next run, which builds its
    /// replacement first.
    /// </summary>
    [SqlServerFact]
    public async Task AFreeCopyThatGrowsTooOldWhileTheTopUpRuns_IsLeftForTheNextRun()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copies = await database.BuildCopiesAsync(2);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var started = clock.GetUtcNow().UtcDateTime;

        // One copy claimed now, so the run has one to build towards its target of two. The other
        // is free and ten minutes short of the 44 hours: fresh when the run counts.
        await database.MarkClaimedAsync(copies[1].Id, started);
        await database.BackdateSeedAsync(copies[0].Id, started.AddHours(-44).AddMinutes(10));
        database.AlsoRegister = services => services.AddSingleton<TimeProvider>(clock);

        // Parked at the first read of its top-up, and half an hour passes there.
        var hold = new HoldingReadInterceptor("[AspNetRoles]");
        var run = database.RecycleAsync(interceptors: hold);
        (await Task.WhenAny(hold.Held, run)).Should().BeSameAs(
            hold.Held, "the run must be parked inside its top-up, or the order below is not the one under test");
        clock.Advance(TimeSpan.FromMinutes(30));
        hold.Release();
        var summary = await run;
        output.WriteLine(summary.ToLine());

        (summary.FreeAtStart, summary.Seeded, summary.DeletedStaleFree).Should().Be(
            (1, 1, 0), "the top-up counted the copy as fresh and built none in its place, so this run does not delete it");
        StillWhole(await database.CopyAsync(copies[0].Id), "the next run builds its replacement first, and then deletes it");
        (await database.CopiesAsync()).Count(c => c.Row.ClaimedAt == null).Should().Be(
            2, "until then the pool holds the two free copies its target asks for");
    }

    [SqlServerFact]
    public async Task TimeThatPassesInsideARun_IsSeenByEachDeleteAfterIt_ByTheSweeps_AndByTheLastCount()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copies = await database.BuildCopiesAsync(5);
        var (first, second, ageing, inUse) = (copies[0], copies[1], copies[2], copies[4]);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var started = clock.GetUtcNow().UtcDateTime;

        // Two copies whose time is over, the first claimed before the second, so the first is
        // deleted first. In the second a session is running that has ten minutes left. A third
        // copy was claimed just now, and a session in it has ten minutes left as well.
        await database.MarkClaimedAsync(first.Id, started.AddHours(-26));
        await database.MarkClaimedAsync(second.Id, started.AddHours(-25));
        await database.MarkClaimedAsync(inUse.Id, started);
        await using (var db = database.NewContext())
        {
            db.RefreshTokens.AddRange(
                AGrant(second.Owner.Id, expiresAt: started.AddMinutes(10)),
                AGrant(inUse.Owner.Id, expiresAt: started.AddMinutes(10)));
            await db.SaveChangesAsync();
        }

        // And of the two free copies, one has ten minutes left before it is too old to hand out.
        await database.BackdateSeedAsync(ageing.Id, started.AddHours(-44).AddMinutes(10));
        database.AlsoRegister = services => services.AddSingleton<TimeProvider>(clock);

        // The run is parked inside the first copy's delete, and half an hour passes there.
        var (run, hold) = await ARunParkedBeforeItReadsACopysGrantsAsync(database);
        clock.Advance(TimeSpan.FromMinutes(30));
        hold.Release();
        var summary = await run;

        summary.Failures.Should().BeEmpty();
        summary.DeletedExpired.Should().Be(
            2, "the second copy's session is judged when its own delete begins, and by then it has ended");
        ATombstone(await database.CopyAsync(second.Id), "no session was running in it any more");
        (await database.CopyAsync(first.Id))!.Row.DeletedAt.Should().Be(started, "the first copy's delete began before the half hour");
        (await database.CopyAsync(second.Id))!.Row.DeletedAt.Should().Be(started.AddMinutes(30), "and the second's after it");

        summary.SweptGrants.Should().Be(
            1, "the sweeps take what has expired by the time they run: the grant of the copy in use, since the second copy's left with its user");
        StillWhole(await database.CopyAsync(inUse.Id), "a copy claimed today is in use, whatever became of its session");

        (summary.FreeAtStart, summary.DeletedStaleFree, summary.Free).Should().Be(
            (2, 0, 1), "what the run LEFT is counted at its end: the copy that grew too old meanwhile is no longer one to hand out");
        StillWhole(await database.CopyAsync(ageing.Id), "it was not too old when the run chose the copies to delete: the next run takes it");
    }

    private static RefreshToken AGrant(Guid userId, DateTime expiresAt) => new()
    {
        Id = Guid.CreateVersion7(),
        UserId = userId,
        TokenHash = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
        ExpiresAt = expiresAt,
        CreatedAt = expiresAt.AddHours(-1),
        IpAddress = "203.0.113.7",
        UserAgent = "pool-test",
    };

    private static IdempotencyRecord ARecord(Guid userId, DateTime expiresAt, IdempotencyStatus status = IdempotencyStatus.Completed) => new()
    {
        UserId = userId,
        Endpoint = "POST /api/transactions/deposit",
        Key = Guid.NewGuid(),
        ClaimId = Guid.NewGuid(),
        RequestHash = new string('a', 64),
        Status = status,
        CreatedAt = expiresAt.AddHours(-24),
        ExpiresAt = expiresAt,
    };

    // ── What the run reports ─────────────────────────────────────────────────────────────────────

    [SqlServerFact]
    public async Task TheFirstFillOfAnEmptyDatabase_ExitsZero()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();

        var summary = await database.RecycleAsync();
        output.WriteLine(summary.ToLine());

        (summary.RowsAtStart, summary.FreeAtStart, summary.Seeded, summary.Free, summary.ExitCode).Should().Be(
            (0, 0, 2, 2, 0), "an empty pool on an empty database is a first fill, not visitors turned away");
        summary.ToLine().Should().Be(
            "pool: free=2 was=0 claimed=0 claims24h=0 clientsAtCap=0 seeded=2 "
            + "deleted(expired=0 hardStop=0 staleFree=0 failed=0) swept(idempotency=0 grants=0) "
            + "tombstones=0 foreignUsers=0 ceiling=no result=PoolOk");
    }

    /// <summary>
    /// A record is not a copy, so a pool whose every copy was deleted is read as a first fill: the
    /// next run exits 0, not 11, although visitors may have been turned away since the run before.
    /// The run that left no copy behind said so with its own code, 15 here: the day's one claim
    /// held its top-up at 0.
    /// </summary>
    [SqlServerFact]
    public async Task APoolLeftWithOnlyRecords_IsReadAsAFirstFill_AndExitsZero()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copy = (await database.BuildCopiesAsync(1))[0];
        await database.MarkClaimedAsync(copy.Id, DateTime.UtcNow.AddHours(-2));

        var emptied = await database.RecycleAsync(settings: new()
        {
            ["Demo:CopyLifetimeHours"] = "1",
            ["Demo:Pool:TargetFree"] = "1",
            ["Demo:Pool:MaxClaimsPerDay"] = "1",
        });
        output.WriteLine(emptied.ToLine());
        (emptied.Seeded, emptied.DeletedExpired, emptied.Free, emptied.Claimed, emptied.Tombstones, emptied.ExitCode).Should().Be(
            (0, 1, 0, 0, 1, 15), "ARRANGE: the copy is deleted, the ceiling built none, and only the copy's record is left");

        var next = await database.RecycleAsync();
        output.WriteLine(next.ToLine());

        (next.RowsAtStart, next.FreeAtStart, next.Seeded, next.Tombstones, next.ExitCode).Should().Be(
            (0, 0, 2, 1, 0), "a pool that holds only records is read as a first fill, not as visitors turned away");
    }

    [SqlServerFact]
    public async Task APoolBelowItsLowMark_ExitsTen_AndIsToppedUpAllTheSame()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copies = await database.BuildCopiesAsync(3);
        await database.MarkClaimedAsync(copies[0].Id, DateTime.UtcNow);
        await database.MarkClaimedAsync(copies[1].Id, DateTime.UtcNow);

        var summary = await database.RecycleAsync(settings: new() { ["Demo:Pool:TargetFree"] = "3", ["Demo:Pool:LowMark"] = "2" });

        (summary.RowsAtStart, summary.FreeAtStart, summary.Seeded, summary.Free, summary.Claimed, summary.ExitCode).Should().Be(
            (3, 1, 2, 3, 2, 10), "one free copy was below the mark of 2 when the run started");
        summary.ToLine().Should().EndWith("result=PoolLow");
    }

    [SqlServerFact]
    public async Task APoolWithNoFreeCopy_ExitsEleven_AndIsToppedUpAllTheSame()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copies = await database.BuildCopiesAsync(2);
        await database.MarkClaimedAsync(copies[0].Id, DateTime.UtcNow);
        await database.MarkClaimedAsync(copies[1].Id, DateTime.UtcNow);

        var summary = await database.RecycleAsync();

        (summary.RowsAtStart, summary.FreeAtStart, summary.Seeded, summary.Free, summary.ExitCode).Should().Be(
            (2, 0, 2, 2, 11), "every copy was claimed when the run started: visitors may have been turned away");
        summary.ToLine().Should().EndWith("result=PoolEmpty");
    }

    /// <summary>
    /// A QUIET POOL. Nobody claimed a copy, and between two runs every free copy grew older than a
    /// free copy is kept, as the whole of a first fill does once a day and a bit later. None was
    /// claimed, so nobody was turned away: each one was still free, too old to count towards the
    /// target and free all the same, until this run built the new ones and deleted it. The twin is
    /// <see cref="APoolWithNoFreeCopy_ExitsEleven_AndIsToppedUpAllTheSame"/>: there every copy was
    /// claimed.
    /// </summary>
    [SqlServerFact]
    public async Task APoolWhoseFreeCopiesAllGrewTooOld_IsRebuilt_AndExitsZero_NotEleven()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copies = await database.BuildCopiesAsync(3);
        foreach (var copy in copies)
        {
            await database.BackdateSeedAsync(copy.Id, DateTime.UtcNow.AddHours(-45));
        }

        var summary = await database.RecycleAsync(settings: new() { ["Demo:Pool:TargetFree"] = "3", ["Demo:Pool:LowMark"] = "1" });
        output.WriteLine(summary.ToLine());

        (summary.RowsAtStart, summary.FreeAtStart, summary.Seeded, summary.DeletedStaleFree, summary.Free, summary.ExitCode).Should().Be(
            (3, 3, 3, 3, 3, 0), "three copies were free when the run started: too old to count towards the target, not too old to be given");
        summary.ToLine().Should().Contain(" was=3 ").And.EndWith("result=PoolOk");
    }

    [SqlServerFact]
    public async Task WhenNoCopyCanBeBuilt_RecycleStopsAfterThreeFailuresInARow_AndExitsTwelve()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();

        var refused = FailingCommandInterceptor.OnText("INSERT INTO [DemoCopies]");
        var summary = await database.RecycleAsync(settings: new() { ["Demo:Pool:TargetFree"] = "5" }, interceptors: refused);

        refused.Failures.Should().Be(3, "the top-up gives up after three failures in a row");
        (summary.Seeded, summary.BuildFailed, summary.Free, summary.ExitCode).Should().Be((0, 3, 0, 12));
        summary.Failures.Select(failure => failure.CopyId).Distinct().Should().HaveCount(
            3, "a copy the top-up could not build is named in the run's summary, like one that could not be deleted");
        summary.ToLine().Should().EndWith("result=TopUpIncomplete");
    }

    [SqlServerFact]
    public async Task WhenTheDaysClaimsNearTheCeiling_TheTopUpIsLimited_AndTheRunExitsFifteen()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copies = await database.BuildCopiesAsync(2);
        await database.MarkClaimedAsync(copies[0].Id, DateTime.UtcNow.AddHours(-1));
        await database.MarkClaimedAsync(copies[1].Id, DateTime.UtcNow.AddHours(-23));
        Dictionary<string, string?> Ceiling(int perDay) =>
            new() { ["Demo:Pool:TargetFree"] = "3", ["Demo:Pool:MaxClaimsPerDay"] = $"{perDay}" };

        // Two claims in the last 24 hours against a ceiling of 4: room for 2, not for the target of 3.
        var limited = await database.RecycleAsync(settings: Ceiling(4));
        output.WriteLine(limited.ToLine());

        (limited.Claims24h, limited.Target, limited.Seeded, limited.Free, limited.Ceiling, limited.ExitCode).Should().Be(
            (2, 2, 2, 2, true, 15), "the ceiling outranks the empty pool the run started with");
        limited.ToLine().Should().Contain("ceiling=yes").And.EndWith("result=ClaimCeiling");

        // TWIN: the same pool under the default ceiling is filled to its target.
        var unlimited = await database.RecycleAsync(settings: Ceiling(150));

        (unlimited.Target, unlimited.Seeded, unlimited.Free, unlimited.Ceiling, unlimited.ExitCode).Should().Be((3, 1, 3, false, 0));
    }

    [SqlServerFact]
    public async Task WhenThePoolIsAlreadyFull_TheCeilingLimitsNothing_AndIsNotASignal()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copies = await database.BuildCopiesAsync(5);
        await database.MarkClaimedAsync(copies[0].Id, DateTime.UtcNow.AddHours(-1));
        await database.MarkClaimedAsync(copies[1].Id, DateTime.UtcNow.AddHours(-2));

        // The same two claims against the same ceiling of 4, but three free copies are there
        // already: the run had nothing to build, so the ceiling held nothing back.
        var summary = await database.RecycleAsync(
            settings: new() { ["Demo:Pool:TargetFree"] = "3", ["Demo:Pool:MaxClaimsPerDay"] = "4" });

        (summary.FreeAtStart, summary.Seeded, summary.Free, summary.Ceiling, summary.ExitCode).Should().Be(
            (3, 0, 3, false, 0), "exit 15 says the pool is short because of the day's claims, and it is not short");
    }

    [SqlServerFact]
    public async Task ACopyAVisitorClaimsWhileTheRunWorks_DoesNotMakeTheCeilingASignal()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copies = await database.BuildCopiesAsync(6);
        await database.MarkClaimedAsync(copies[0].Id, ADayAndAnHourAgo);
        await database.MarkClaimedAsync(copies[1].Id, DateTime.UtcNow.AddHours(-1));
        await database.MarkClaimedAsync(copies[2].Id, DateTime.UtcNow.AddHours(-2));

        // Two claims in the rolling day against a ceiling of 4: room for 2 of the target of 3. But
        // the run finds three free copies, so it has nothing to build and the ceiling holds
        // nothing back. While it is deleting the copy whose time is over, a visitor claims one.
        var (run, hold) = await ARunParkedBeforeItReadsACopysGrantsAsync(
            database, settings: new() { ["Demo:Pool:TargetFree"] = "3", ["Demo:Pool:MaxClaimsPerDay"] = "4" });
        await database.MarkClaimedAsync(copies[3].Id, DateTime.UtcNow);
        hold.Release();
        var summary = await run;

        (summary.Target, summary.FreeAtStart, summary.Seeded, summary.Free).Should().Be(
            (2, 3, 0, 2), "ARRANGE: the pool was full when the run counted it, and one copy short when it ended");
        (summary.Ceiling, summary.ExitCode).Should().Be(
            (false, 0), "the ceiling is a signal when it limited the top-up, and the top-up had nothing to build: a claim made afterwards is the next run's to see");
    }

    [SqlServerFact]
    public async Task TheSummary_CountsTheDaysClaims_AndTheClientsAtTheirCap()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copies = await database.BuildCopiesAsync(6);
        var busy = Enumerable.Repeat((byte)0xA1, 32).ToArray();
        var quiet = Enumerable.Repeat((byte)0xB2, 32).ToArray();
        await database.MarkClaimedAsync(copies[0].Id, DateTime.UtcNow.AddHours(-1), busy);
        await database.MarkClaimedAsync(copies[1].Id, DateTime.UtcNow.AddHours(-2), busy);
        await database.MarkClaimedAsync(copies[2].Id, DateTime.UtcNow.AddHours(-3), quiet);
        // Claimed by the busy client too, but 24 hours and 2 minutes ago: outside the rolling day.
        await database.MarkClaimedAsync(copies[3].Id, DateTime.UtcNow.AddHours(-24).AddMinutes(-2), busy);

        var summary = await database.RecycleAsync(settings: new() { ["Demo:Claim:MaxPerClientPerDay"] = "2" });
        output.WriteLine(summary.ToLine());

        (summary.Claims24h, summary.ClientsAtCap, summary.Claimed).Should().Be(
            (3, 1, 4), "three claims in the rolling day, one client with its two, four claimed copies in all");
    }

    [SqlServerFact]
    public async Task WhatARunFound_IsCountedBeforeItDeletesAnything()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copies = await database.BuildCopiesAsync(4);
        var busy = Enumerable.Repeat((byte)0xA1, 32).ToArray();

        // Two claims by one client three hours ago: the client is at a cap of 2, and under a
        // lifetime of 2 hours both copies' time is over.
        await database.MarkClaimedAsync(copies[0].Id, DateTime.UtcNow.AddHours(-3), busy);
        await database.MarkClaimedAsync(copies[1].Id, DateTime.UtcNow.AddHours(-3), busy);

        var summary = await database.RecycleAsync(
            settings: new() { ["Demo:CopyLifetimeHours"] = "2", ["Demo:Claim:MaxPerClientPerDay"] = "2" });

        (summary.DeletedExpired, summary.Claimed, summary.Tombstones).Should().Be(
            (2, 0, 2), "ARRANGE: the run deleted both copies, and a record no longer says which client claimed it");
        (summary.Claims24h, summary.ClientsAtCap).Should().Be(
            (2, 1), "the day's claims and the clients at their cap are what the run found, before it deleted anything");
    }

    [SqlServerFact]
    public async Task ATombstone_IsNeverTakenAgain_AndIsNotAPoolRowAnyMore()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copy = (await database.BuildCopiesAsync(3))[0];
        // Past the lifetime AND past the backstop, and seeded longer ago than a free copy is kept:
        // every rule that picks a copy would pick it again if it forgot that it is already deleted.
        await database.MarkClaimedAsync(copy.Id, DateTime.UtcNow.AddHours(-100));
        await database.BackdateSeedAsync(copy.Id, DateTime.UtcNow.AddHours(-101));

        var first = await database.RecycleAsync();
        (first.DeletedExpired, first.DeletedHardStop, first.Tombstones, first.RowsAtStart).Should().Be((1, 0, 1, 3));
        var tombstone = (await database.CopyAsync(copy.Id))!.Row;

        var sent = new CommandTimeoutRecordingInterceptor();
        var second = await database.RecycleAsync(interceptors: sent);

        (second.DeletedExpired, second.DeletedHardStop, second.DeleteFailed, second.Tombstones, second.RowsAtStart, second.Claimed)
            .Should().Be((0, 0, 0, 1, 2, 0), "a tombstone is a record: it is counted as one and as nothing else");
        (await database.CopyAsync(copy.Id))!.Row.Should().BeEquivalentTo(tombstone, "the record is not rewritten");

        // And it costs a run nothing. The records are kept for ever, about 150 more each day at the
        // ceiling: a run that opened a transaction for each would spend its time on them.
        TablesDeletedFrom(sent).Should().BeEquivalentTo(
            new[] { "IdempotencyRecords", "RefreshTokens" }, "the two sweeps are the only deletes of a run that has no copy to delete");
        sent.Commands.Should().NotContain(
            command => command.Text.Contains("UPDATE", StringComparison.Ordinal) && command.Text.Contains("[DemoCopies]", StringComparison.Ordinal),
            "no statement tries to take a record, as a claimed copy or as a free one");
    }

    [SqlServerFact]
    public async Task WhenAnotherRunDeletesACopyFirst_ThisRunCountsNothing_AndLeavesTheRecordAsItIs()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copy = (await database.BuildCopiesAsync(3))[0];
        await database.MarkClaimedAsync(copy.Id, ADayAndAnHourAgo);

        // Two runs at once, a scheduled one and one started by hand. This one has chosen the copy
        // and is on its way to it; the other deletes it first.
        var (run, hold) = await ARunParkedBeforeItReadsACopysGrantsAsync(database);
        var other = await database.RecycleAsync();
        other.DeletedExpired.Should().Be(1, "ARRANGE: the other run deleted the copy while this one waited");
        var record = (await database.CopyAsync(copy.Id))!.Row;

        hold.Release();
        var summary = await run;

        summary.Failures.Should().BeEmpty("finding nothing left to delete is not a failure");
        (summary.DeletedExpired, summary.DeletedHardStop, summary.DeleteFailed, summary.Tombstones).Should().Be(
            (0, 0, 0, 1), "a copy is deleted once, and counted by the run that deleted it");
        (await database.CopyAsync(copy.Id))!.Row.Should().BeEquivalentTo(record, "the record is written once");
    }

    [SqlServerFact]
    public async Task WhenTheAnswerToAClaimedCopysDeleteIsLost_TheCopyIsCountedOnce_AndIsNotAFailure()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copy = (await database.BuildCopiesAsync(3))[0];
        await database.MarkClaimedAsync(copy.Id, ADayAndAnHourAgo);

        // The delete commits, and the answer never arrives. The last statement of that delete is
        // the one that makes the pool row a record.
        var lost = new LostCommitAnswerInterceptor("UPDATE", "[DemoCopies]", "[DeletedAt]");
        var summary = await database.RecycleAsync(interceptors: lost);

        lost.Fired.Should().BeTrue("the answer must actually have been lost, else the test proves nothing");
        summary.Failures.Should().BeEmpty();
        (summary.DeletedExpired, summary.DeleteFailed, summary.Tombstones).Should().Be(
            (1, 0, 1),
            "the copy is gone, so it is counted: a recycler that ran the delete again would find the record written and report that it left alone a copy it had deleted");
        ATombstone(await database.CopyAsync(copy.Id), "the delete landed");
    }

    [SqlServerFact]
    public async Task WhenAClaimedCopysDeleteIsRunAgain_AndASessionHasBegunSince_TheCopyIsNotCountedAsDeleted()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copy = (await database.BuildCopiesAsync(3))[0];
        await database.MarkClaimedAsync(copy.Id, ADayAndAnHourAgo);

        // The delete reaches its commit, the commit fails and nothing of it lands: the copy is
        // whole again. A session begins in it before the delete is run again.
        var failed = new FailedCommitInterceptor(
            async () =>
            {
                await using var db = database.NewContext();
                db.RefreshTokens.Add(AGrant(copy.Owner.Id, expiresAt: DateTime.UtcNow.AddHours(1)));
                await db.SaveChangesAsync();
            },
            "UPDATE",
            "[DemoCopies]",
            "[DeletedAt]");
        var summary = await database.RecycleAsync(interceptors: failed);

        failed.Fired.Should().BeTrue("the commit must actually have failed, else the test proves nothing");
        summary.Failures.Should().BeEmpty("a copy in use is not a failure");
        (summary.DeletedExpired, summary.DeletedHardStop, summary.DeleteFailed, summary.Claimed).Should().Be(
            (0, 0, 0, 1), "a run reports what its last attempt at a copy did: the first got as far as its commit, and the second found a live grant");
        StillWhole(await database.CopyAsync(copy.Id), "the grant is live");
    }

    // ── What a run writes down ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// A copy's email address is the name its visitor signs in with, and a job's log is kept
    /// somewhere else, for longer, and read by other people than the database is. A run names a
    /// copy by its id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The recorder takes every line of every category at every level, with the text of any
    /// exception attached: the builder's and the recycler's own lines, Identity's, and EF's. The
    /// run is one in which each of the pool's own log statements has a reason to speak: a copy is
    /// built, a collision is retried, three copies are deleted and one delete fails.
    /// </para>
    /// <para>
    /// NOT COVERED, because it does not happen: two copies that draw the same sixteen characters.
    /// A failure's message is the database's own, and SQL Server's message for a duplicate names
    /// the value that was refused.
    /// </para>
    /// </remarks>
    [SqlServerFact]
    public async Task ARun_NeverWritesACopysEmailAddressToItsLog()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var log = new RecordingLoggerProvider();
        database.Log = log;

        var copies = await ThreeExpiredOneStaleOneFreshAsync(database);
        var poison = FailingCommandInterceptor.OnDeleteNaming(await database.IdsOfAsync(copies[1]));
        string taken;
        await using (var db = database.NewContext())
        {
            taken = await db.Accounts.Select(a => a.AccountNumber).FirstAsync();
        }

        var collision = new CollidingAccountNumberInterceptor(taken);
        var summary = await database.RecycleAsync(interceptors: [poison, collision]);

        collision.Fired.Should().BeTrue("ARRANGE: a build collided and was retried");
        (summary.Seeded, summary.DeletedExpired, summary.DeleteFailed, summary.DeletedStaleFree).Should().Be(
            (1, 2, 1, 1), "ARRANGE: the run built a copy, deleted three and failed to delete one");

        var lines = log.Lines;
        lines.Should().Contain(
            line => line.Message.Contains("[AspNetUsers]", StringComparison.Ordinal),
            "the recorder saw the statements that carried the addresses: a recorder that heard nothing reports clean for ever");

        var everyCopy = copies.Concat(await database.CopiesAsync()).SelectMany(c => c.Users).Select(u => u.Email!).Distinct().ToList();
        everyCopy.Should().HaveCount(18, "ARRANGE: five copies built for the test and one by the run, three users each");
        var written = lines
            .Where(line => line.Message.Contains("@azurebank.example", StringComparison.OrdinalIgnoreCase)
                || everyCopy.Any(email => line.Message.Contains(email[..email.IndexOf('@')], StringComparison.OrdinalIgnoreCase)))
            .Select(line => $"{line.Level}: {line.Message}")
            .ToList();
        written.Should().BeEmpty("a run names a copy by its id, never by the address its visitor signs in with");
    }

    // ── The flag ─────────────────────────────────────────────────────────────────────────────────

    [SqlServerFact]
    public async Task WithTheDemoOff_RecycleRefuses_AndDeletesNothing()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copy = (await database.BuildCopiesAsync(3))[0];
        await database.MarkClaimedAsync(copy.Id, ADayAndAnHourAgo);

        var act = () => database.RecycleAsync(settings: new() { ["Demo:Enabled"] = "false" });

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*Demo:Enabled*");
        StillWhole(await database.CopyAsync(copy.Id), "a command that deletes users does nothing outside the demo");

        // CONTROL: the same run with the demo on deletes the copy, so the refusal was the flag's.
        (await database.RecycleAsync()).DeletedExpired.Should().Be(1);
    }
}
