using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzureBank.Seeder.Pool;

/// <summary>How the rows of one table leave when a copy is deleted.</summary>
public enum CopyRowFate
{
    /// <summary>The recycler deletes them with a statement of its own.</summary>
    Deleted,

    /// <summary>The database deletes them with the user they belong to.</summary>
    CascadesFromUser,
}

/// <summary>
/// What a control run leaves out of the delete. The tests' controls run the recycler's own
/// statements with one safeguard removed, so a control cannot drift from the code it is about.
/// </summary>
internal enum RecyclerControl
{
    /// <summary>The recycler as it ships.</summary>
    None,

    /// <summary>Delete the owner's rows only, not the two contacts'.</summary>
    OwnerOnly,

    /// <summary>Leave the soft-delete filter on, so closed accounts are not deleted.</summary>
    KeepSoftDeleteFilter,

    /// <summary>Let one copy's failure end the run.</summary>
    NoTryPerCopy,
}

/// <summary>Tops the pool up and deletes the copies whose time is over: what <c>recycle</c> runs.</summary>
/// <remarks>
/// <para>
/// THE TOP-UP COMES FIRST. Free copies are what visitors wait for, and building one writes only
/// new rows, which nothing a visitor did can stop. A delete reads what a visitor wrote. So a copy
/// that cannot be deleted never costs the pool a copy.
/// </para>
/// <para>
/// THE SWEEPS COME LAST. Removing what has expired is housekeeping the API does as well, on timers
/// of its own; deleting a copy whose time is over is what only a run does. A sweep that fails is
/// no copy's failure and no exit code of the pool's names it: it ends the run as a failed one,
/// with no summary. By then the copies are deleted.
/// </para>
/// <para>
/// ONE COPY, ONE TRANSACTION, ONE TRY. A copy is deleted whole or not at all, and a copy whose
/// delete throws is counted, logged by its id and left as it was: the run goes on to the next. It
/// is tried once in a run.
/// </para>
/// <para>
/// EVERY DELETE IS KEYED ON THE COPY: the users whose <c>DemoCopyId</c> is the copy's id, and the
/// rows of those users. Never an age, a name or an address. A user that belongs to no copy matches
/// no statement here, whatever else is true of it. The two sweeps are the exception, and they are
/// the API's own: a grant or an idempotency record that has expired, whoever it belongs to.
/// </para>
/// <para>
/// NEVER A COPY IN USE. A claimed copy is left alone until five minutes past its lifetime, and
/// after that for as long as one of its users holds a grant that is not revoked and did not end
/// more than five minutes ago: that is read inside the transaction that deletes the copy, not
/// when the copy was chosen. The
/// one limit is the backstop, two days past the lifetime, which no copy outlives. A free copy is
/// taken with the statement a visitor's claim uses, so of the two only one can have it.
/// </para>
/// <para>
/// WHAT STAYS. Every audit row: the table has no foreign key, the rows name their actor by id, and
/// nothing here adds, changes or removes one, so the chain verifies after a delete as it did
/// before. And the pool row of a claimed copy, as the record that the actor those rows name was a
/// demo copy.
/// </para>
/// <para>
/// THE LEDGER'S OWN GUARD DOES NOT SEE THESE DELETES, and that is deliberate. The context refuses
/// a change to a ledger row it tracks; a set-based statement tracks nothing. A demo copy's ledger
/// is invented data that belongs to nobody, and it is the only ledger this class can reach.
/// </para>
/// <para>
/// No audit row is written: the Seeder holds no audit chain key.
/// </para>
/// </remarks>
public sealed class DemoCopyRecycler
{
    /// <summary>
    /// How long past its lifetime a claimed copy is left alone, and how long past its end a grant
    /// still keeps the copy: a request that began just before either end may still be running.
    /// </summary>
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long past its lifetime a live grant can keep a copy. After that the copy is deleted,
    /// grant or no grant: no copy outlives it.
    /// </summary>
    private static readonly TimeSpan Backstop = TimeSpan.FromHours(48);

    /// <summary>
    /// What each statement of a run is given. The delete of a copy a visitor filled is the one
    /// statement here that can be slow, and a statement that times out is not sent again.
    /// </summary>
    private static readonly TimeSpan StatementTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Every table that holds rows of a copy's users, and how those rows leave.</summary>
    /// <remarks>
    /// A table that is missing here either stops the delete of every copy that wrote to it or
    /// keeps its rows for ever, so <c>DemoCopyBoundaryTests</c> walks the model and fails on a
    /// table this list does not name.
    /// </remarks>
    public static IReadOnlyDictionary<string, CopyRowFate> TablesOfACopy { get; } =
        new Dictionary<string, CopyRowFate>
        {
            // No foreign key reaches these two, so nothing but a statement of the recycler's
            // would ever remove their rows.
            ["StepUpAuthorizations"] = CopyRowFate.Deleted,
            ["IdempotencyRecords"] = CopyRowFate.Deleted,

            // Each of these restricts the delete of the next: ledger rows before their accounts,
            // accounts before their users.
            ["Transactions"] = CopyRowFate.Deleted,
            ["Accounts"] = CopyRowFate.Deleted,
            ["AspNetUsers"] = CopyRowFate.Deleted,

            ["RefreshTokens"] = CopyRowFate.CascadesFromUser,
            ["SubscriberNotices"] = CopyRowFate.CascadesFromUser,
            ["AspNetUserRoles"] = CopyRowFate.CascadesFromUser,
            ["AspNetUserClaims"] = CopyRowFate.CascadesFromUser,
            ["AspNetUserLogins"] = CopyRowFate.CascadesFromUser,
            ["AspNetUserTokens"] = CopyRowFate.CascadesFromUser,
        };

    private readonly AzureBankDbContext _context;
    private readonly DemoCopyBuilder _builder;
    private readonly DemoOptions _options;
    private readonly ILogger<DemoCopyRecycler> _logger;
    private readonly TimeProvider _clock;

    public DemoCopyRecycler(
        AzureBankDbContext context,
        DemoCopyBuilder builder,
        IOptions<DemoOptions> options,
        ILogger<DemoCopyRecycler> logger,
        TimeProvider? clock = null)
    {
        _context = context;
        _builder = builder;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Set by a test's control run only.</summary>
    internal RecyclerControl Control { get; set; }

    /// <summary>What became of one copy the run looked at.</summary>
    private enum Outcome
    {
        /// <summary>In use, or a visitor claimed it first: nothing was changed.</summary>
        LeftAlone,

        /// <summary>A claimed copy whose time was over and whose sessions had ended.</summary>
        Expired,

        /// <summary>A claimed copy past the backstop, deleted under a grant that was still live.</summary>
        HardStop,

        /// <summary>A free copy too old to hand out.</summary>
        StaleFree,

        /// <summary>Its delete threw, and it is as it was.</summary>
        Failed,
    }

    /// <summary>One run: count, top up, delete, sweep, count again.</summary>
    /// <remarks>
    /// <para>
    /// REFUSED OUTSIDE THE DEMO, here and not only in the command that calls this: a caller that
    /// forgot the flag must not be able to delete users.
    /// </para>
    /// <para>
    /// AND REFUSED ON THE WRONG DATABASE: users, and not one pool row. Nothing is written, the
    /// sweeps included, and the summary carries the count that says why.
    /// </para>
    /// <para>
    /// A run can be started at any interval and beside the API. It can also be started again
    /// after one that failed: everything it does is decided from the rows as they are.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException"><c>Demo:Enabled</c> is off.</exception>
    public async Task<PoolRunSummary> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            throw new InvalidOperationException(
                "recycle tops up and deletes demo copies only in demo mode: Demo:Enabled is not true.");
        }

        // For every statement this scope sends from here on, the top-up's included.
        _context.Database.SetCommandTimeout(StatementTimeout);

        // What the run found, before it changes anything, and the instant it counted at.
        var countedAt = Now();
        var start = await PoolCounts.ReadAsync(_context, _options, countedAt, cancellationToken);

        // The day's claims cap the pool: a pool that is drained again and again is refilled only
        // up to the ceiling, so what a day can add to the database has an end.
        var room = Math.Max(0, _options.Pool.MaxClaimsPerDay - start.Claims24h);
        var target = Math.Min(_options.Pool.TargetFree, room);
        if (start.IsTheWrongDatabase)
        {
            // Every count but this one is 0: there is no pool row to count.
            return Finish(new PoolRunSummary { Target = target, ForeignUsers = start.ForeignUsers });
        }

        // "Limited" means copies were held back. A pool that already holds its full target had
        // nothing to build, so a lowered target took nothing from it.
        var ceiling = target < _options.Pool.TargetFree && start.Free < _options.Pool.TargetFree;
        var topUp = await _builder.BuildAsync(target - start.Free, cancellationToken);

        // The instant the copies whose time is over are chosen at, read after the top-up, however
        // long that took.
        var now = Now();

        var outcomes = new List<Outcome>();
        var failures = new List<PoolCopyFailure>();

        // Claimed copies whose time is over, oldest first. A record of a deleted copy is still a
        // claimed row, so it is left out by name: it is never taken again.
        var lifetime = TimeSpan.FromHours(_options.CopyLifetimeHours);
        DateTime? over = now - lifetime - Margin;
        var backstop = now - lifetime - Backstop;
        var ended = await _context.DemoCopies.AsNoTracking()
            .Where(copy => copy.ClaimedAt < over && copy.DeletedAt == null)
            .OrderBy(copy => copy.ClaimedAt)
            .Select(copy => new { copy.Id, copy.ClaimedAt })
            .ToListAsync(cancellationToken);
        foreach (var copy in ended)
        {
            var pastTheBackstop = copy.ClaimedAt < backstop;
            outcomes.Add(await InItsOwnTryAsync(
                copy.Id, failures, () => DeleteClaimedAsync(copy.Id, pastTheBackstop, cancellationToken), cancellationToken));
        }

        // Free copies too old to hand out AS OF THE INSTANT THE TOP-UP COUNTED: those are the ones
        // it built replacements for, first, so the pool is never short while this goes on. A copy
        // that grew too old while the top-up ran was counted as fresh and has no replacement yet;
        // the next run builds one and then deletes it. Unlike the claimed copies above, where the
        // later instant is the one that matters: a copy whose time ended meanwhile is in nobody's way.
        var freshSince = countedAt.AddHours(-_options.Pool.MaxFreeAgeHours);
        var stale = await _context.DemoCopies.AsNoTracking()
            .Where(copy => copy.ClaimedAt == null && copy.CreatedAt < freshSince)
            .OrderBy(copy => copy.CreatedAt)
            .Select(copy => copy.Id)
            .ToListAsync(cancellationToken);
        foreach (var copyId in stale)
        {
            outcomes.Add(await InItsOwnTryAsync(
                copyId, failures, () => DeleteStaleFreeAsync(copyId, now, cancellationToken), cancellationToken));
        }

        // Last, and outside every try: see the class remarks.
        var (sweptIdempotency, sweptGrants) = await SweepAsync(cancellationToken);

        // What the run left, as of its end.
        var end = await PoolCounts.ReadAsync(_context, _options, Now(), cancellationToken);
        return Finish(new PoolRunSummary
        {
            RowsAtStart = start.Rows,
            FreeAtStart = start.FreeOfAnyAge,
            Free = end.Free,
            Target = target,
            Claimed = end.Claimed,
            Claims24h = start.Claims24h,
            ClientsAtCap = start.ClientsAtCap,
            Seeded = topUp.Seeded,
            BuildFailed = topUp.Failures.Count,
            DeletedExpired = outcomes.Count(outcome => outcome == Outcome.Expired),
            DeletedHardStop = outcomes.Count(outcome => outcome == Outcome.HardStop),
            DeletedStaleFree = outcomes.Count(outcome => outcome == Outcome.StaleFree),
            DeleteFailed = failures.Count,
            SweptIdempotency = sweptIdempotency,
            SweptGrants = sweptGrants,
            Tombstones = end.Tombstones,
            ForeignUsers = start.ForeignUsers,
            Ceiling = ceiling,
            Failures = [.. topUp.Failures, .. failures],
        });
    }

    private PoolRunSummary Finish(PoolRunSummary summary) =>
        summary with { ExitCode = PoolExitCodes.From(summary, _options.Pool.LowMark) };

    /// <summary>
    /// Removes the idempotency records and the grants that have expired, whoever they belong to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What the API's own two clean-ups do (<c>IdempotencyCleanupService</c>,
    /// <c>RefreshTokenCleanupService</c>). The API runs them on timers inside its process, which
    /// tick only while that process is up; a run does not depend on it.
    /// </para>
    /// <para>
    /// THE WARNING INCLUDED. An expired record that is still <c>Executed</c> is an operation that
    /// committed and never stored its answer. The API's clean-up writes each one down before it
    /// removes it, as a candidate for reconciliation (ADR-0009); a run that removed them first,
    /// in silence, would take that line away. It names the record's key and its user's id, as the
    /// API's does. The records of a copy this run deleted are not among them: they left with the
    /// copy.
    /// </para>
    /// <para>
    /// A grant can name the one that replaced it through a foreign key that restricts, so every
    /// link to a grant that is about to go is cleared first, from live grants too.
    /// </para>
    /// </remarks>
    private async Task<(int Idempotency, int Grants)> SweepAsync(CancellationToken cancellationToken)
    {
        // What has expired by now, the time the copies took included.
        var now = Now();
        var unanswered = await _context.IdempotencyRecords.AsNoTracking()
            .Where(record => record.ExpiresAt <= now && record.Status == IdempotencyStatus.Executed)
            .Select(record => new { record.Endpoint, record.Key, record.UserId })
            .ToListAsync(cancellationToken);
        foreach (var record in unanswered)
        {
            _logger.LogWarning(
                "Cleaning up EXECUTED idempotency record {Endpoint}/{Key} (user {UserId}): "
                + "the operation committed but its response was never stored: reconciliation candidate",
                record.Endpoint,
                record.Key,
                record.UserId);
        }

        var idempotency = await _context.IdempotencyRecords
            .Where(record => record.ExpiresAt <= now)
            .ExecuteDeleteAsync(cancellationToken);

        await _context.RefreshTokens
            .Where(grant => grant.ReplacedByTokenId != null
                && _context.RefreshTokens.Any(next => next.Id == grant.ReplacedByTokenId && next.ExpiresAt <= now))
            .ExecuteUpdateAsync(set => set.SetProperty(grant => grant.ReplacedByTokenId, (Guid?)null), cancellationToken);
        var grants = await _context.RefreshTokens
            .Where(grant => grant.ExpiresAt <= now)
            .ExecuteDeleteAsync(cancellationToken);

        return (idempotency, grants);
    }

    /// <summary>
    /// Runs one copy's delete so that its failure is the copy's and not the run's: counted, logged
    /// by the copy's id with the database's number, and the run goes on.
    /// </summary>
    /// <remarks>
    /// A run that is stopped is the exception: that is nothing the copy did, so it is not counted
    /// against the copy and it ends the run. Whether the run was stopped is read from the token,
    /// not from the kind of failure: a stop that reaches a statement the database already has
    /// comes back as the database's own error, not as a cancellation.
    /// </remarks>
    private async Task<Outcome> InItsOwnTryAsync(
        Guid copyId, List<PoolCopyFailure> failures, Func<Task<Outcome>> delete, CancellationToken cancellationToken)
    {
        try
        {
            return await delete();
        }
        catch (Exception ex) when (Control != RecyclerControl.NoTryPerCopy && !cancellationToken.IsCancellationRequested)
        {
            // The root cause, and SQL Server's number when the failure was the database's: the
            // message of a refused delete names the foreign key that refused it.
            var number = DemoCopyBuilder.SqlErrorNumber(ex);
            failures.Add(new PoolCopyFailure(copyId, number, ex.GetBaseException().Message));
            _logger.LogError(ex, "Demo copy {CopyId} could not be deleted (error {ErrorNumber})", copyId, number);
            return Outcome.Failed;
        }
    }

    /// <summary>
    /// Deletes a claimed copy whose time is over, unless a session is still running in it, and
    /// leaves its pool row as the record.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE GRANTS ARE READ INSIDE THE TRANSACTION, not when the copy was chosen: a session can end
    /// or begin while the run works through the copies before this one. Past the backstop a live
    /// grant no longer protects the copy, and the delete is counted apart: a grant that is still
    /// live two days after sign-in should have ended says that sign-in went on being accepted.
    /// </para>
    /// <para>
    /// THE RECORD. The row keeps the owner's id, which is what the audit rows that stay name as
    /// their actor, and the instant of the claim. It loses the hash of the client's address,
    /// which is kept to count a client's claims over one day and for nothing after. It is an
    /// operator's record, written by the same hand that deleted the rows; anyone who can write
    /// to the database could write one.
    /// </para>
    /// <para>
    /// WRITTEN ONCE. The last statement makes the row a record only if it is not one yet, and the
    /// copy counts as deleted by this run only if that statement changed the row. A second run
    /// that started beside this one and reached the copy first has deleted it and written its
    /// record: this run then finds nothing to delete, leaves the record as it is and counts
    /// nothing.
    /// </para>
    /// <para>
    /// WHEN THE ANSWER TO THE COMMIT IS LOST, the strategy asks whether the row is a record
    /// before it runs the delete again. Run again it would find the record written, and report a
    /// copy it had deleted as one it had left alone.
    /// </para>
    /// <para>
    /// WHEN THE COMMIT FAILS and nothing of it landed, the delete is run again, and what is
    /// reported is what that last attempt did. The first got as far as its last line and had
    /// decided "deleted"; by the time the second looks, a session can have begun in the copy, or
    /// another run can have written its record.
    /// </para>
    /// <para>
    /// A GRANT THAT ENDED LESS THAN FIVE MINUTES AGO STILL COUNTS. Every access token a grant mints
    /// ends with it, so no request is accepted after; but one accepted the moment before has its
    /// deadline still to run, and a delete that began under it could leave behind a row it inserts.
    /// The same margin as the claim's.
    /// </para>
    /// <para>
    /// THE INSTANT IS THE COPY'S OWN, read when its delete begins: a run can spend minutes on the
    /// copies before this one, and a session that ended meanwhile keeps nothing. It is the instant
    /// the record carries.
    /// </para>
    /// </remarks>
    private async Task<Outcome> DeleteClaimedAsync(Guid copyId, bool pastTheBackstop, CancellationToken cancellationToken)
    {
        var outcome = Outcome.LeftAlone;
        var now = Now();
        var liveSince = now - Margin;
        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteInTransactionAsync(
            async token =>
            {
                // Each attempt decides afresh: what an earlier one decided did not land.
                outcome = Outcome.LeftAlone;

                var inUse = await _context.RefreshTokens.AnyAsync(
                    grant => grant.RevokedAt == null
                        && grant.ExpiresAt > liveSince
                        && _context.Users.Any(user => user.Id == grant.UserId && user.DemoCopyId == copyId),
                    token);
                if (inUse && !pastTheBackstop)
                {
                    return;
                }

                await DeleteTheRowsAsync(copyId, token);

                DateTime? deletedAt = now;
                var recorded = await _context.DemoCopies
                    .Where(copy => copy.Id == copyId && copy.DeletedAt == null)
                    .ExecuteUpdateAsync(
                        set => set
                            .SetProperty(copy => copy.DeletedAt, deletedAt)
                            .SetProperty(copy => copy.ClientKey, (byte[]?)null),
                        token);
                if (recorded == 0)
                {
                    return;
                }

                outcome = inUse ? Outcome.HardStop : Outcome.Expired;
            },
            token => _context.DemoCopies.AsNoTracking().AnyAsync(copy => copy.Id == copyId && copy.DeletedAt != null, token),
            cancellationToken);

        return outcome;
    }

    /// <summary>
    /// Deletes a free copy that is too old to hand out, row and all, unless a visitor claims it
    /// first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// TAKEN LIKE A CLAIM. The first statement is the one a visitor's claim uses: it marks the
    /// copy claimed if, and only if, it is still free. Whichever of the two commits first has the
    /// copy, and the other's statement touches no row. When it is the visitor's, nothing here is
    /// deleted. When it is this one, the copy is taken and deleted in the same transaction, so a
    /// run that dies half way leaves it free.
    /// </para>
    /// <para>
    /// NO RECORD IS KEPT. Nobody ever signed in to a free copy, so no audit row names it.
    /// </para>
    /// <para>
    /// WHEN THE ANSWER TO THE COMMIT IS LOST, the strategy asks whether the row is gone before it
    /// runs the delete again. Run again it would find no row to take, and report a copy it had
    /// deleted as one it had left alone.
    /// </para>
    /// <para>
    /// WHEN THE COMMIT FAILS and nothing of it landed, the delete is run again, and what is
    /// reported is what that last attempt did. The first got as far as its last line and had
    /// decided "deleted"; by the time the second looks, a visitor can have claimed the copy.
    /// </para>
    /// <para>
    /// <paramref name="chosenAt"/> is the instant the copy is marked claimed with. Nobody reads
    /// it: the mark leaves with the row, in the same transaction.
    /// </para>
    /// </remarks>
    private async Task<Outcome> DeleteStaleFreeAsync(Guid copyId, DateTime chosenAt, CancellationToken cancellationToken)
    {
        var outcome = Outcome.LeftAlone;
        DateTime? claimedAt = chosenAt;
        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteInTransactionAsync(
            async token =>
            {
                // Each attempt decides afresh: what an earlier one decided did not land.
                outcome = Outcome.LeftAlone;

                Guid? claimId = Guid.CreateVersion7();
                var taken = await _context.DemoCopies
                    .Where(copy => copy.Id == copyId && copy.ClaimedAt == null)
                    .ExecuteUpdateAsync(
                        set => set
                            .SetProperty(copy => copy.ClaimedAt, claimedAt)
                            .SetProperty(copy => copy.ClaimId, claimId),
                        token);
                if (taken == 0)
                {
                    return;
                }

                await DeleteTheRowsAsync(copyId, token);
                await _context.DemoCopies.Where(copy => copy.Id == copyId).ExecuteDeleteAsync(token);

                outcome = Outcome.StaleFree;
            },
            async token => !await _context.DemoCopies.AsNoTracking().AnyAsync(copy => copy.Id == copyId, token),
            cancellationToken);

        return outcome;
    }

    /// <summary>
    /// Deletes everything of a copy but its pool row, inside the caller's transaction: five
    /// statements, in an order every foreign key allows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SET-BASED. One statement for each table, whatever the copy holds: nothing is loaded and
    /// nothing is tracked.
    /// </para>
    /// <para>
    /// ALL THREE USERS TOGETHER. The owner's transfers point at rows on the contacts' accounts and
    /// those point back, through a foreign key that restricts. The database checks it when a
    /// statement ends, so both halves of every transfer have to leave in the same statement.
    /// </para>
    /// <para>
    /// CLOSED ACCOUNTS TOO. The context hides a closed account from every query; left hidden, it
    /// and its ledger rows would stay, and the delete of its owner would be refused.
    /// </para>
    /// <para>
    /// THE GRANTS, THE NOTICES AND THE ROLE ROWS LEAVE WITH THE USERS, by the database's own
    /// cascade. Two grants of one user that name each other go in that one statement, so the link
    /// between them stops nothing.
    /// </para>
    /// </remarks>
    private async Task DeleteTheRowsAsync(Guid copyId, CancellationToken cancellationToken)
    {
        var users = _context.Users.Where(user => user.DemoCopyId == copyId);
        if (Control == RecyclerControl.OwnerOnly)
        {
            users = users.Where(user => _context.DemoCopies.Any(copy => copy.Id == copyId && copy.OwnerUserId == user.Id));
        }

        var userIds = users.Select(user => user.Id);

        // Ignoring the filter is a property of the whole query, so the ledger's statement, which
        // names its accounts through this one, reaches the rows of closed accounts as well.
        var accounts = _context.Accounts.Where(account => userIds.Contains(account.UserId));
        if (Control != RecyclerControl.KeepSoftDeleteFilter)
        {
            accounts = accounts.IgnoreQueryFilters();
        }

        var accountIds = accounts.Select(account => account.Id);

        await _context.StepUpAuthorizations
            .Where(authorization => userIds.Contains(authorization.UserId))
            .ExecuteDeleteAsync(cancellationToken);
        await _context.IdempotencyRecords
            .Where(record => userIds.Contains(record.UserId))
            .ExecuteDeleteAsync(cancellationToken);
        await _context.Transactions
            .Where(row => accountIds.Contains(row.AccountId))
            .ExecuteDeleteAsync(cancellationToken);
        await accounts.ExecuteDeleteAsync(cancellationToken);
        await users.ExecuteDeleteAsync(cancellationToken);
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;
}
