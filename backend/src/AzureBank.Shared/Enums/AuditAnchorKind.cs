namespace AzureBank.Shared.Enums;

/// <summary>What an <see cref="Entities.AuditAnchor"/> record is asserting.</summary>
/// <remarks>
/// Stored as a string and covered by the record's MAC, in a fixed position, for BOTH members. The
/// value set is frozen the moment the first record is written: it is inside the hashed payload, so
/// renaming a member re-renders every record that named it.
/// </remarks>
public enum AuditAnchorKind
{
    /// <summary>The walk reached the end of an intact chain, and this record says how far and how many.</summary>
    Anchor,

    /// <summary>
    /// A run happened and produced nothing anchorable — the chain was broken, or there was nothing
    /// to verify.
    /// </summary>
    /// <remarks>
    /// IT COVERS NOTHING, and that is what makes it safe: every coverage column is null, so flipping
    /// a marker into an anchor cannot mint a claim it never carried. Its MAC stops the reverse move,
    /// which is the cheap one — a database-only attacker turning a real anchor into a marker would
    /// otherwise collapse the operator's provable bound to the previous record for free.
    /// ⚠️ It does not constrain the operator, who holds the key and can write as many honest-looking
    /// markers as they like.
    /// </remarks>
    GapMarker,
}
