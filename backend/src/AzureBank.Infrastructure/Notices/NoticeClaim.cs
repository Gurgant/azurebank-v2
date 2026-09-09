using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using Microsoft.EntityFrameworkCore;

namespace AzureBank.Infrastructure.Notices;

/// <summary>
/// The claim protocol every runner shares (ADR-0048): a name, one set-based lease over a bounded
/// batch of the free owed rows, and the queries that read what a runner holds and what others hold.
/// </summary>
/// <remarks>
/// <para>
/// ONE STATEMENT, ONE CHANCE TO INTERLEAVE. The claim is a single UPDATE over the oldest free owed
/// rows — no lease, or a lapsed one — up to a batch. The batch is chosen by a subquery, and the
/// UPDATE's own WHERE repeats the free predicate: the database serialises two such statements, the
/// second waits on the first's row locks and re-evaluates that predicate on the committed row, so it
/// finds the lease the first one wrote and takes nothing. A runner that claimed N rows in N round
/// trips would spend N chances to interleave instead of one. Proved on SQL Server by a claim held
/// open in a transaction, which blocks a second runner's sweep until it commits; the second then
/// claims zero.
/// </para>
/// <para>
/// BOUNDED, so a claim is proportional to what one lease can deliver. An unbounded claim over a
/// backlog — five hundred rows, measured in the shared test store — would stamp the whole table,
/// run out of lease part-way, and leave the rest held by a runner no longer delivering them until the
/// lease lapsed, while a second runner found nothing free. A batch keeps the tail free for whoever
/// sweeps next.
/// </para>
/// <para>
/// WHAT A LEASE DOES NOT DO. It stops two runners holding one row at the same moment. It cannot stop
/// a runner that delivers, dies before marking, and is succeeded after the lease lapses. With a
/// sending transport that row would go out twice; with the pickup directory the second attempt is
/// refused by the exclusive create and the row stays owed beside the file it produced, until an
/// operator applies the runbook. Either way the protocol is at-least-once and says so.
/// </para>
/// <para>
/// THE NAME IS FOR THE LOG AND THE RE-READ, never a secret: kind, host, process id and eight random
/// hex digits, bounded to the column and truncated from the host, never from the suffix — two
/// runners on one long-named host must still differ.
/// </para>
/// </remarks>
public static class NoticeClaim
{
    /// <summary>The width of <c>SubscriberNotices.LeasedBy</c>.</summary>
    public const int NameWidth = 64;

    /// <summary>
    /// The KIND prefix of a runner name — the head of what lands in <c>LeasedBy</c>, and the only
    /// part of it a person reads to know which runner holds a row.
    /// </summary>
    /// <remarks>
    /// Constants rather than three string literals in three projects, because the prefix is a
    /// namespace: two kinds that shared one would make every log line and every held-by-another
    /// count ambiguous, and nothing would fail. ADR-0051 names all three.
    /// </remarks>
    public const string ApiKind = "api";

    /// <inheritdoc cref="ApiKind"/>
    public const string VerbKind = "verb";

    /// <inheritdoc cref="ApiKind"/>
    public const string FunctionKind = "func";

    /// <summary><c>{kind}/{host}/{pid}/{8 hex}</c>, at most <see cref="NameWidth"/> characters.</summary>
    public static string RunnerNameFor(string kind, string host, int processId, Guid id)
    {
        var tail = $"/{processId}/{id.ToString("N")[..8]}";
        var head = $"{kind}/{host}";
        var room = NameWidth - tail.Length;
        if (head.Length > room)
        {
            head = head[..room];
        }

        return head + tail;
    }

    /// <summary>
    /// Stamps up to <paramref name="batch"/> of the oldest free owed rows with this runner's name and
    /// lease end, and returns how many; a batch of zero claims nothing, which is what a runner whose
    /// holdings already fill its batch asks for. Set-based on a relational store; the load-and-save fallback
    /// exists for the InMemory test hosts, which cannot translate ExecuteUpdate. The two paths are
    /// not equally atomic and are not covered by the same tests: the unit suite runs the fallback,
    /// the SQL proofs the statement.
    /// </summary>
    public static async Task<int> ClaimAsync(
        AzureBankDbContext context,
        string runner,
        DateTime now,
        DateTime leaseEnd,
        int batch,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(batch);
        if (batch == 0)
        {
            return 0;
        }

        var free = context.SubscriberNotices
            .Where(n => n.DeliveredAt == null && (n.LeasedUntil == null || n.LeasedUntil <= now));

        if (context.Database.IsRelational())
        {
            // The batch by subquery; the free predicate REPEATED on the outer statement, because that
            // is the one the engine re-evaluates under the row lock when two claims collide.
            // Oldest first, and the id — a UUIDv7, time-ordered — breaks a tie, so a batch is the
            // same batch whichever engine sorts it.
            var oldest = free.OrderBy(n => n.OccurredAt).ThenBy(n => n.Id).Take(batch).Select(n => n.Id);
            return await context.SubscriberNotices
                .Where(n => oldest.Contains(n.Id)
                            && n.DeliveredAt == null
                            && (n.LeasedUntil == null || n.LeasedUntil <= now))
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(n => n.LeasedUntil, leaseEnd)
                        .SetProperty(n => n.LeasedBy, runner),
                    cancellationToken);
        }

        var rows = await free.OrderBy(n => n.OccurredAt).ThenBy(n => n.Id).Take(batch).ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            row.LeasedUntil = leaseEnd;
            row.LeasedBy = runner;
        }

        await context.SaveChangesAsync(cancellationToken);
        return rows.Count;
    }

    /// <summary>
    /// Extends the lease on every owed row this runner still holds to <paramref name="leaseEnd"/>,
    /// and returns how many. A sweep calls it before it reads its holdings, so a row held from an
    /// earlier sweep — delivered to a transport that refused it — expires WITH this sweep's lease
    /// and not before it: otherwise the per-row check, which compares against this sweep's end,
    /// could let a delivery run past the row's own earlier end, and another runner claim the row
    /// while this one was still writing it. Only live leases are renewed; a lapsed one is somebody
    /// else's to claim.
    /// </summary>
    public static async Task<int> RenewAsync(
        AzureBankDbContext context, string runner, DateTime now, DateTime leaseEnd, CancellationToken cancellationToken)
    {
        var mine = context.SubscriberNotices
            .Where(n => n.DeliveredAt == null && n.LeasedBy == runner && n.LeasedUntil > now);

        if (context.Database.IsRelational())
        {
            return await mine.ExecuteUpdateAsync(
                set => set.SetProperty(n => n.LeasedUntil, leaseEnd), cancellationToken);
        }

        var rows = await mine.ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            row.LeasedUntil = leaseEnd;
        }

        await context.SaveChangesAsync(cancellationToken);
        return rows.Count;
    }

    /// <summary>
    /// The owed rows this runner holds under a live lease, oldest first — keyed on the NAME the
    /// claim wrote, never on the instant, so a store that rounded a timestamp could not make a
    /// runner claim N and read back none. Includes rows held from an earlier sweep whose delivery
    /// failed, so they are retried before the lease lapses rather than after.
    /// </summary>
    public static IQueryable<SubscriberNotice> HeldBy(AzureBankDbContext context, string runner, DateTime now) =>
        context.SubscriberNotices
            .Where(n => n.DeliveredAt == null && n.LeasedBy == runner && n.LeasedUntil > now)
            .OrderBy(n => n.OccurredAt)
            .ThenBy(n => n.Id);

    /// <summary>How many owed rows another runner holds under a live lease right now.</summary>
    /// <remarks>
    /// <c>LeasedBy != null</c> for the same reason its sibling below carries it, and because the two
    /// must AGREE: a row held until a time by nobody is not held by another runner, so counting it
    /// here while excluding it there made the verb print "N owed notice(s) are leased by a live
    /// runner" with no runner to name. The store forbids the state (CK_SubscriberNotices_Lease) and
    /// the InMemory provider does not, which is where the two answers could diverge.
    /// </remarks>
    public static Task<int> HeldByOthersAsync(
        AzureBankDbContext context, string runner, DateTime now, CancellationToken cancellationToken) =>
        context.SubscriberNotices.CountAsync(
            n => n.DeliveredAt == null
                 && n.LeasedUntil != null
                 && n.LeasedUntil > now
                 && n.LeasedBy != null
                 && n.LeasedBy != runner,
            cancellationToken);

    /// <summary>
    /// The distinct KINDS of runner holding owed rows under a live lease right now, other than this
    /// one — <see cref="ApiKind"/>, <see cref="VerbKind"/>, <see cref="FunctionKind"/> — in the order
    /// they are declared, so the answer reads the same twice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THIS IS WHAT THE KIND PREFIX IS FOR. <see cref="HeldByOthersAsync"/> answers "how many" and
    /// the verb used to guess the rest, telling an operator that "the API's relay is delivering
    /// them" whichever runner actually held the rows — true while the API was the only one, and a
    /// guess the moment the Function shipped (ADR-0051 D7). The name is already in the column; this
    /// reads it rather than assuming it.
    /// </para>
    /// <para>
    /// A name whose head is none of the three is DROPPED rather than reported raw: <c>LeasedBy</c>
    /// is written by this protocol, but a hand-edited row could carry anything, and an operator
    /// message is the wrong place to echo an unvalidated string back at somebody. Dropping it leaves
    /// the caller with an empty list, which its own wording must survive — and the count from
    /// <see cref="HeldByOthersAsync"/> is unaffected, so nothing goes missing from the tally.
    /// </para>
    /// </remarks>
    public static async Task<IReadOnlyList<string>> HolderKindsOtherThanAsync(
        AzureBankDbContext context, string runner, DateTime now, CancellationToken cancellationToken)
    {
        /*
          `LeasedBy != null` IS NOT REDUNDANT, THOUGH THE STORE MAKES IT UNREACHABLE.
          CK_SubscriberNotices_Lease pairs the two halves — "([LeasedUntil] IS NULL AND [LeasedBy] IS
          NULL) OR (both NOT NULL)" — so on SQL Server a row cannot be held until a time by nobody,
          and SubscriberNoticeSqlServerTests watches the store refuse one. But `null != runner` is
          TRUE, so without this term the predicate would admit such a row, and unlike its siblings
          this query DEREFERENCES what it selects. The InMemory provider enforces no check
          constraint, and NotifyCommand wraps everything in a catch that reports "the store could
          not be read or written" — measured: with the term removed the operator is told
          "CANNOT NOTIFY: the store could not be read or written (NullReferenceException)", an
          accusation against the database for a bug in this query. A query that dereferences must
          not lean on an invariant two layers away that it cannot see.
        */
        var names = await context.SubscriberNotices
            .Where(n => n.DeliveredAt == null
                        && n.LeasedUntil != null
                        && n.LeasedUntil > now
                        && n.LeasedBy != null
                        && n.LeasedBy != runner)
            .Select(n => n.LeasedBy!)
            .Distinct()
            .ToListAsync(cancellationToken);

        string[] known = [ApiKind, VerbKind, FunctionKind];
        return known
            .Where(kind => names.Any(name => name.StartsWith(kind + "/", StringComparison.Ordinal)))
            .ToList();
    }
}
