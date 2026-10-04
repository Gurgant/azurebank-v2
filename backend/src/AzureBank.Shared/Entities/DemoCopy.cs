namespace AzureBank.Shared.Entities;

/// <summary>
/// One prepared demo copy: a demo user, two contacts, their four accounts and their ledger. The row
/// is what a visitor claims, what the caps count on, and what stays as a record once the copy's
/// users are deleted.
/// </summary>
/// <remarks>
/// <para>
/// THE KEY GOES FROM THE USER TO THE COPY (<see cref="ApplicationUser.DemoCopyId"/>), never the other
/// way: all three users of a copy carry its id, and the row outlives them. So
/// <see cref="OwnerUserId"/> is a plain column that may name a user who is no longer there.
/// </para>
/// <para>
/// No expiry column: a copy's end is <see cref="ClaimedAt"/> plus the configured lifetime
/// (<c>Demo:CopyLifetimeHours</c>).
/// </para>
/// <para>
/// Outside the demo the table holds no row.
/// </para>
/// </remarks>
public class DemoCopy
{
    /// <summary>The copy's id, minted by the Seeder and never by the database.</summary>
    /// <remarks>
    /// Not an order: SQL Server sorts a <c>uniqueidentifier</c> by its last bytes first, so "newest"
    /// is always read from <see cref="CreatedAt"/>.
    /// </remarks>
    public Guid Id { get; set; }

    /// <summary>The demo user a visitor signs in as. One copy per owner; no foreign key.</summary>
    public Guid OwnerUserId { get; set; }

    /// <summary>The instant the copy was seeded; its ledger's dates are offsets from it.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When a visitor claimed the copy. Null while it is free.</summary>
    /// <remarks>Set together with <see cref="ClaimId"/> or not at all: the database refuses one without the other.</remarks>
    public DateTime? ClaimedAt { get; set; }

    /// <summary>The id minted by the request that claimed the copy. No two copies carry the same one.</summary>
    public Guid? ClaimId { get; set; }

    /// <summary>
    /// A keyed hash of the claiming client's address, 32 bytes. Null on a free copy and on the
    /// record of a deleted one.
    /// </summary>
    public byte[]? ClientKey { get; set; }

    /// <summary>
    /// Requests of the copy's signed-in users that the demo's budget counted: any method but GET,
    /// HEAD, OPTIONS and TRACE, and each reveal of an account number; never a token endpoint.
    /// Starts at 0. (Until 2026-10-04 this said "authenticated unsafe requests".)
    /// </summary>
    public int Writes { get; set; }

    /// <summary>
    /// When a claimed copy's users were deleted: the row is then a record only. The database
    /// refuses it on a copy nobody claimed.
    /// </summary>
    public DateTime? DeletedAt { get; set; }
}
