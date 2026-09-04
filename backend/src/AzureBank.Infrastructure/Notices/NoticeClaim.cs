using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using Microsoft.EntityFrameworkCore;

namespace AzureBank.Infrastructure.Notices;

/// <summary>
/// The claim protocol every runner shares (ADR-0048): a name, one set-based lease over the free
/// owed rows, and the queries that read what a runner holds and what others hold.
/// </summary>
/// <remarks>
/// <para>
/// ONE STATEMENT, ONE CHANCE TO INTERLEAVE. The claim is a single UPDATE over every owed row whose
/// lease is null or lapsed. The database serialises two such statements: the second waits on the
/// first's row locks and re-evaluates the predicate on the committed row, so it finds the lease
/// the first one wrote and takes nothing. A runner that claimed N rows in N round trips would spend
/// N chances to interleave instead of one. Proved on SQL Server by a claim held open in a
/// transaction, which blocks a second runner's sweep until it commits; the second then claims zero.
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
    /// Stamps every free owed row — no lease, or a lapsed one — with this runner's name and lease
    /// end, and returns how many. Set-based on a relational store; the load-and-save fallback exists
    /// for the InMemory test hosts, which cannot translate ExecuteUpdate. The two paths are not
    /// equally atomic and are not covered by the same tests: the unit suite runs the fallback, the
    /// SQL proofs the statement.
    /// </summary>
    public static async Task<int> ClaimAsync(
        AzureBankDbContext context, string runner, DateTime now, DateTime leaseEnd, CancellationToken cancellationToken)
    {
        var free = context.SubscriberNotices
            .Where(n => n.DeliveredAt == null && (n.LeasedUntil == null || n.LeasedUntil <= now));

        if (context.Database.IsRelational())
        {
            return await free.ExecuteUpdateAsync(
                set => set
                    .SetProperty(n => n.LeasedUntil, leaseEnd)
                    .SetProperty(n => n.LeasedBy, runner),
                cancellationToken);
        }

        var rows = await free.ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            row.LeasedUntil = leaseEnd;
            row.LeasedBy = runner;
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
            .OrderBy(n => n.OccurredAt);

    /// <summary>How many owed rows another runner holds under a live lease right now.</summary>
    public static Task<int> HeldByOthersAsync(
        AzureBankDbContext context, string runner, DateTime now, CancellationToken cancellationToken) =>
        context.SubscriberNotices.CountAsync(
            n => n.DeliveredAt == null && n.LeasedUntil != null && n.LeasedUntil > now && n.LeasedBy != runner,
            cancellationToken);
}
