extern alias seeder;

using System.Net;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;
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
/// Time is moved by back-dating rows (a claim, a seed instant, a grant's expiry), never by a fake
/// clock: the recycler runs from the Seeder's own container, as the job does.
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
        deletes.Should().HaveCountGreaterThanOrEqualTo(5, "the copy's delete is at least five statements");
        deletes.Should().OnlyContain(c => c.TimeoutSeconds == 120);

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
        var copies = await ThreeExpiredOneStaleOneFreshAsync(database);
        var poisoned = copies[1];
        var poison = FailingCommandInterceptor.OnDeleteNaming(await database.IdsOfAsync(poisoned));

        var summary = await database.RecycleAsync(interceptors: poison);
        output.WriteLine(summary.ToLine());

        poison.Failures.Should().BeGreaterThan(0, "the failure must actually have been injected, else the test proves nothing");
        var failure = summary.Failures.Should().ContainSingle().Which;
        failure.CopyId.Should().Be(poisoned.Id, "the copy that failed is named, so an operator can look at it");
        failure.Message.Should().Contain(FailingCommandInterceptor.Message);

        (summary.DeletedExpired, summary.DeleteFailed, summary.DeletedStaleFree).Should().Be(
            (2, 1, 1), "the other two expired copies and the stale free one are deleted all the same");
        StillWhole(await database.CopyAsync(poisoned.Id), "its delete rolled back whole");
        ATombstone(await database.CopyAsync(copies[0].Id), "the copy before the poisoned one is deleted");
        ATombstone(await database.CopyAsync(copies[2].Id), "the copy after the poisoned one is deleted");
        (await database.CopyAsync(copies[3].Id)).Should().BeNull("the stale free copy is deleted, row and all");

        (summary.FreeAtStart, summary.Seeded, summary.Free).Should().Be((1, 1, 2), "the pool is at its target");
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

        await act.Should().ThrowAsync<Exception>("nothing catches the failure, so the process would exit 1 and print no summary");
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

    // ── A copy in use ────────────────────────────────────────────────────────────────────────────

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
            (1, 1, 1, 2, 0), "only the fresh copy counted, one was built first, and the stale one was then deleted");
        (await database.CopyAsync(stale.Id)).Should().BeNull("nothing ever pointed at a free copy, so no record of it is kept");
        (await database.RowsOfAsync(stale.UserIds)).Should().OnlyContain(table => table.Value == 0);
        StillWhole(await database.CopyAsync(copies[1].Id), "a fresh free copy is not touched");
        summary.ExitCode.Should().Be(0, "one fresh free copy is not below a low mark of 0, and the pool was not empty");
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

        var summary = await database.RecycleAsync();

        (summary.ForeignUsers, summary.ExitCode).Should().Be((1, 13), "a user outside every copy is reported");
        (await database.RowsOfAsync([resident.UserId])).Should().Equal(
            before, "every delete is keyed on a copy, so a user of no copy loses nothing: not a grant, not a record, not a row");

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
        await using (var db = database.NewContext())
        {
            db.RefreshTokens.Add(expiredGrant);
            await db.SaveChangesAsync();
            db.RefreshTokens.Add(liveGrant);
            db.IdempotencyRecords.AddRange(ARecord(owner, expiresAt: now - hour), ARecord(owner, expiresAt: now + hour));
            await db.SaveChangesAsync();
        }

        var summary = await database.RecycleAsync();

        (summary.SweptGrants, summary.SweptIdempotency, summary.DeleteFailed).Should().Be((1, 1, 0));
        await using (var db = database.NewContext())
        {
            var grant = (await db.RefreshTokens.AsNoTracking().ToListAsync()).Should().ContainSingle().Which;
            grant.Id.Should().Be(liveGrant.Id);
            grant.ReplacedByTokenId.Should().BeNull("the link to the swept grant was cleared");
            (await db.IdempotencyRecords.AsNoTracking().SingleAsync()).ExpiresAt.Should().BeAfter(now);
        }

        StillWhole(await database.CopyAsync(copy.Id), "a copy claimed today is in use");
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

    private static IdempotencyRecord ARecord(Guid userId, DateTime expiresAt) => new()
    {
        UserId = userId,
        Endpoint = "POST /api/transactions/deposit",
        Key = Guid.NewGuid(),
        ClaimId = Guid.NewGuid(),
        RequestHash = new string('a', 64),
        Status = IdempotencyStatus.Completed,
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

    [SqlServerFact]
    public async Task WhenNoCopyCanBeBuilt_RecycleStopsAfterThreeFailuresInARow_AndExitsTwelve()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();

        var refused = FailingCommandInterceptor.OnText("INSERT INTO [DemoCopies]");
        var summary = await database.RecycleAsync(settings: new() { ["Demo:Pool:TargetFree"] = "5" }, interceptors: refused);

        refused.Failures.Should().Be(3, "the top-up gives up after three failures in a row");
        (summary.Seeded, summary.BuildFailed, summary.Free, summary.ExitCode).Should().Be((0, 3, 0, 12));
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
    public async Task ATombstone_IsNeverTakenAgain_AndIsNotAPoolRowAnyMore()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copy = (await database.BuildCopiesAsync(3))[0];
        // Past the lifetime AND past the backstop, so both deletes would pick it again if either
        // forgot that it is already deleted.
        await database.MarkClaimedAsync(copy.Id, DateTime.UtcNow.AddHours(-100));

        var first = await database.RecycleAsync();
        (first.DeletedExpired, first.DeletedHardStop, first.Tombstones, first.RowsAtStart).Should().Be((1, 0, 1, 3));
        var tombstone = (await database.CopyAsync(copy.Id))!.Row;

        var second = await database.RecycleAsync();

        (second.DeletedExpired, second.DeletedHardStop, second.DeleteFailed, second.Tombstones, second.RowsAtStart, second.Claimed)
            .Should().Be((0, 0, 0, 1, 2, 0), "a tombstone is a record: it is counted as one and as nothing else");
        (await database.CopyAsync(copy.Id))!.Row.Should().BeEquivalentTo(tombstone, "the record is not rewritten");
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
