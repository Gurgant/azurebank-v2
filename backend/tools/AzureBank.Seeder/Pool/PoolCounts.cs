using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Options;
using Microsoft.EntityFrameworkCore;

namespace AzureBank.Seeder.Pool;

/// <summary>
/// What the pool holds at one instant: the counts a run reads before it changes anything, and
/// again at its end.
/// </summary>
/// <param name="AllRows">Every pool row, the records of deleted copies included.</param>
/// <param name="Rows">Pool rows that are not the record of a deleted copy.</param>
/// <param name="Free">Free copies young enough to hand out.</param>
/// <param name="Claimed">Claimed copies whose users still exist.</param>
/// <param name="Claims24h">Claims in the 24 hours before the instant, deleted copies included.</param>
/// <param name="ClientsAtCap">Clients with at least the daily cap of claims in those 24 hours.</param>
/// <param name="Tombstones">Records of deleted copies.</param>
/// <param name="ForeignUsers">Users that belong to no copy.</param>
internal sealed record PoolCounts(
    int AllRows,
    int Rows,
    int Free,
    int Claimed,
    int Claims24h,
    int ClientsAtCap,
    int Tombstones,
    int ForeignUsers)
{
    /// <summary>
    /// Users, and not one pool row: the signature of a database that is not the demo's. A run that
    /// sees it writes nothing.
    /// </summary>
    /// <remarks>
    /// Not one row AT ALL, records included: a demo database whose every copy was deleted still
    /// holds their records, and is still the demo's.
    /// </remarks>
    public bool IsTheWrongDatabase => AllRows == 0 && ForeignUsers > 0;

    /// <summary>Reads the counts as of <paramref name="now"/>.</summary>
    /// <remarks>
    /// "Free" is always FRESH free: a free copy older than <c>Demo:Pool:MaxFreeAgeHours</c> opens
    /// on a history that ends days ago, so it is not handed out and does not count towards the
    /// target. It stays a pool row until <c>recycle</c> deletes it.
    /// </remarks>
    public static async Task<PoolCounts> ReadAsync(
        AzureBankDbContext context, DemoOptions options, DateTime now, CancellationToken cancellationToken)
    {
        var freshSince = now.AddHours(-options.Pool.MaxFreeAgeHours);
        DateTime? dayAgo = now.AddHours(-24);
        var cap = options.Claim.MaxPerClientPerDay;
        var copies = context.DemoCopies.AsNoTracking();

        return new PoolCounts(
            AllRows: await copies.CountAsync(cancellationToken),
            Rows: await copies.CountAsync(c => c.DeletedAt == null, cancellationToken),
            Free: await copies.CountAsync(c => c.ClaimedAt == null && c.CreatedAt >= freshSince, cancellationToken),
            Claimed: await copies.CountAsync(c => c.ClaimedAt != null && c.DeletedAt == null, cancellationToken),
            Claims24h: await copies.CountAsync(c => c.ClaimedAt > dayAgo, cancellationToken),
            ClientsAtCap: await copies
                .Where(c => c.ClaimedAt > dayAgo && c.ClientKey != null)
                .GroupBy(c => c.ClientKey)
                .CountAsync(group => group.Count() >= cap, cancellationToken),
            Tombstones: await copies.CountAsync(c => c.DeletedAt != null, cancellationToken),
            ForeignUsers: await context.Users.CountAsync(u => u.DemoCopyId == null, cancellationToken));
    }
}
