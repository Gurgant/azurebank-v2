namespace AzureBank.Shared.Entities;

/// <summary>
/// One prepared demo copy: a demo user, two contacts, their four accounts and their ledger. The row
/// is what a visitor claims, what the caps count on, and what stays as a record once the copy's
/// users are deleted.
/// </summary>
/// <remarks>
/// NOT IN THE MODEL YET. The class exists so the tests of the pool compile; the table, its
/// constraints and its indexes arrive with the migration that maps it.
/// </remarks>
public class DemoCopy
{
    /// <summary>The copy's id, minted by the Seeder.</summary>
    public Guid Id { get; set; }

    /// <summary>The demo user a visitor signs in as.</summary>
    public Guid OwnerUserId { get; set; }

    /// <summary>The instant the copy was seeded; its ledger's dates are offsets from it.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When a visitor claimed the copy. Null while it is free.</summary>
    public DateTime? ClaimedAt { get; set; }

    /// <summary>The id minted by the request that claimed the copy.</summary>
    public Guid? ClaimId { get; set; }

    /// <summary>A keyed hash of the claiming client's address.</summary>
    public byte[]? ClientKey { get; set; }

    /// <summary>Authenticated unsafe requests made by the copy's users.</summary>
    public int Writes { get; set; }

    /// <summary>When a claimed copy's users were deleted: the row is then a record only.</summary>
    public DateTime? DeletedAt { get; set; }
}
