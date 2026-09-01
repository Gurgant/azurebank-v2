using AzureBank.Shared.Enums;

namespace AzureBank.Shared.Entities;

/// <summary>
/// One record of what the audit chain looked like at the instant somebody ran the verifier, chained
/// to the record before it and authenticated under a key the database does not hold.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS EXISTS, AND WHAT IT DOES NOT DO. The row chain proves no row was altered and none was
/// removed from the MIDDLE, against somebody holding the database but not <c>Audit:ChainKey</c>. It
/// cannot see a truncated END: the surviving prefix links and hashes perfectly. An anchor records
/// how far the chain reached and how many rows it held, so a later, shorter history stops matching a
/// number somebody already has.
/// </para>
/// <para>
/// ⚠️ AND "LOUD" MEANS INTERIOR, NOT ANY. Removing a SUFFIX of records is silent: the survivors
/// are 1..n with every link met, and nothing in the walk asks how tall the chain ought to be. That
/// is the same shape as the row chain's own limit and it is why the anchors alone cannot close
/// truncation — the attack is a suffix removal in both tables. Measured both ways by
/// <c>DeletingAnchorsIsLoudONLYINTHEINTERIOR_ANDASUFFIXISSILENT</c>. The unqualified sentence was
/// repeated in nine places before it was checked.
///
/// ⚠️ ON ITS OWN THIS DETECTS NOTHING, and saying so is not modesty. Truncate the audit rows above
/// some sequence, then delete every anchor covering past it, and BOTH chains verify perfectly —
/// each links backwards only. What the record buys is narrower and real: deleting an INTERIOR
/// anchor is LOUD, because the counter gaps and the links stop meeting, while MINTING one needs
/// <c>Audit:AnchorKey</c>. The evidence is the pair the operator wrote down somewhere else, and this
/// table is what that pair is compared against.
/// </para>
/// <para>
/// AND IT DOES NOT CONSTRAIN THE OPERATOR. On a single-machine deployment the person who can
/// truncate the table holds the anchor key too, and can write honest-looking anchors over the
/// result. Nothing here is proof; an external timestamp is what would make it evidence, and it is
/// not built yet.
/// </para>
/// <para>
/// INSERT-ONLY, STRICTLY. No column is ever updated after the row is written, which is what makes
/// "any UPDATE against this table is tampering" a rule with no exceptions left to argue about.
/// </para>
/// <para>
/// ⚠️ THE APPLICATION ENFORCES THAT RULE. THE DATABASE DOES NOT, and this comment used to say it
/// did. Nothing at the engine refuses an UPDATE against this table: three tests in
/// <c>AuditAnchorSqlServerTests</c> issue one straight past the change tracker and it SUCCEEDS every
/// time — which is the whole reason the authentication code is what catches it. Engine enforcement
/// is what SQL Server's ledger would buy, it is deferred rather than rejected, and ADR-0044 records
/// why. Until then the rule is a discipline this code keeps, not a property the store provides.
/// </para>
/// <para>
/// The timestamp token that will bind an anchor to an instant attaches from a SEPARATE table rather
/// than filling a reserved column here, precisely so that rule stays absolute.
/// </para>
/// </remarks>
public class AuditAnchor
{
    /// <summary>This record's position in the anchor chain: 1-based, gapless, assigned by the writer.</summary>
    /// <remarks>
    /// It is inside the MACed payload. Outside it, an old anchor covering very little could be
    /// renumbered into the newest slot with its MAC still valid — the same tail-renumbering hole the
    /// row chain closed by hashing <c>Sequence</c>.
    /// </remarks>
    public long AnchorSequence { get; set; }

    /// <summary>Which rendering of the anchor payload wrote this record.</summary>
    /// <remarks>
    /// A SEPARATE LADDER FROM THE ROW PAYLOAD'S. Rows read <c>v2</c>/<c>v3</c>; anchors read
    /// <c>a1</c>. They version independently because they change for independent reasons, and a
    /// shared ladder would force a bump on one to invalidate the other.
    /// </remarks>
    public string PayloadVersion { get; set; } = string.Empty;

    /// <summary>Non-secret identity of the <c>Audit:AnchorKey</c> that authenticated this record.</summary>
    /// <remarks>
    /// WITHOUT THIS, A FAILED MAC MEANS NOTHING IN PARTICULAR. A verifier could not tell "MACed
    /// under a key I no longer hold" from "minted by somebody who never held one", so the single
    /// signal the sixth secret exists to produce would be uninterpretable — and the design would
    /// have quietly frozen the anchor key forever, which is the option this project rejected for
    /// audit rows one change earlier. Derived from the key rather than configured, and under its own
    /// domain string: sharing the row's would weld two constants that must be free to move apart.
    /// </remarks>
    public string AnchorKeyId { get; set; } = string.Empty;

    /// <summary>Whether this record anchors a chain state, or marks a run that produced none.</summary>
    /// <remarks>
    /// INSIDE THE MACED PAYLOAD, IN A FIXED POSITION, FOR BOTH KINDS. It decides what the record
    /// MEANS, so a MAC that did not cover it would let a database-only attacker flip a real anchor
    /// into a marker for free — collapsing the operator's provable bound to the previous anchor
    /// while every other check still passed.
    /// </remarks>
    public AuditAnchorKind Kind { get; set; }

    /// <summary>The lowest audit <c>Sequence</c> the walk actually read. Null on a gap marker.</summary>
    /// <remarks>
    /// Anchored because an intact walk that BEGINS at 5,001 is the signature of a prefix deletion by
    /// somebody who held the key. Without it the record cannot tell that story later.
    /// </remarks>
    public long? LowestCoveredSequence { get; set; }

    /// <summary>The highest audit <c>Sequence</c> this record covers. Null on a gap marker.</summary>
    public long? CoveredThroughSequence { get; set; }

    /// <summary>How many rows the walk verified. Null on a gap marker.</summary>
    /// <remarks>
    /// NOT REDUNDANT WITH THE SEQUENCE BOUNDS, and it is worth saying because it looks it.
    /// <see cref="CoveredThroughSequence"/> and <see cref="TailRowHash"/> describe the same endpoint
    /// and both survive a prefix deletion untouched. The count is the only anchored quantity that
    /// constrains the interior.
    /// </remarks>
    public long? CoveredRowCount { get; set; }

    /// <summary>The <c>RowHash</c> of the row at <see cref="CoveredThroughSequence"/>. Null on a gap marker.</summary>
    /// <remarks>
    /// It transitively commits to every row beneath it, because each row's hash folds in its
    /// predecessor's — which is why one anchor covers a whole prefix and no tree is needed for this.
    /// </remarks>
    public string? TailRowHash { get; set; }

    /// <summary>Identity of the CURRENT <c>Audit:ChainKey</c> the run behind this record held.</summary>
    /// <remarks>
    /// The key a run HELD, recorded so that the record names something more than a hex string.
    /// Present on gap markers too: a run always holds a chain key, whatever the walk found.
    /// <para>
    /// ⚠️ IT DOES NOT IDENTIFY THE KEY BEHIND <see cref="TailRowHash"/>, and this paragraph used to
    /// imply that it did — "without this the record names a hex string with no way to know which key
    /// produced it" reads as a claim about the tail's key, which the paragraph below then denies.
    /// Two sentences, one remarks block, opposite claims.
    /// </para>
    /// <para>
    /// ⚠️ ONE ID FOR A WALK THAT MAY HAVE USED SEVERAL. This said "the key the walk verified under",
    /// which stopped being accurate when the key ring landed: a walk over a rotated table applies
    /// whichever key each row names, and this field is written from the current key unconditionally.
    /// It answers which key the RUN held, and nothing beyond that. It does NOT necessarily identify
    /// the key behind <see cref="TailRowHash"/>: an anchor taken after a rotation but before the
    /// first row is written under the new key finds a tail that a RETIRED key authenticated, while
    /// this field says the current one. The first version of this paragraph claimed the two were the
    /// same "because the tail is written under the current key" — true whenever anything has been
    /// written since the rotation, and false in exactly the window a rotation opens. See ADR-0044 D7,
    /// which records the anchor half of rotation as deferred rather than done.
    /// </para>
    /// </remarks>
    public string VerifiedUnderChainKeyId { get; set; } = string.Empty;

    /// <summary>The previous record's <see cref="PayloadHash"/>. Null only at <c>AnchorSequence = 1</c>.</summary>
    public string? PreviousAnchorPayloadHash { get; set; }

    /// <summary>Unkeyed digest of the chain state this record anchors. Null on a gap marker.</summary>
    /// <remarks>
    /// UNKEYED ON PURPOSE, and it is the one value here that is meant to leave the building: it is
    /// what a timestamp authority would sign, so it has to survive a key rotation and be checkable
    /// by somebody holding no secret of ours. It is also a literal element of the MACed payload
    /// rather than something a verifier is expected to recompute — otherwise one UPDATE swaps the
    /// value a third party will attest while the MAC still verifies.
    /// </remarks>
    public string? AnchoredValue { get; set; }

    /// <summary>Unkeyed SHA-256 over this record's payload, and what the NEXT record links to.</summary>
    /// <remarks>
    /// Unkeyed so that a party holding neither secret can still walk the anchor chain's shape. It
    /// authenticates nothing on its own — <see cref="Mac"/> does that — and a document that treats
    /// the link as evidence has misread it.
    /// </remarks>
    public string PayloadHash { get; set; } = string.Empty;

    /// <summary>HMAC-SHA256 over this record's payload under <c>Audit:AnchorKey</c>.</summary>
    public string Mac { get; set; } = string.Empty;

    /// <summary>When the run that produced this record walked the chain. UTC by convention.</summary>
    /// <remarks>
    /// OUR OWN CLOCK, and authenticated to whoever holds the anchor key rather than attested by
    /// anybody. It is hashed as ticks for the reason the audit row learned the hard way: a formatted
    /// timestamp renders differently once it has been through the database, so a payload built on
    /// one fails verification the moment it is read back.
    /// </remarks>
    public DateTime CreatedAt { get; set; }
}
