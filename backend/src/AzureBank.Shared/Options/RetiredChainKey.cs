namespace AzureBank.Shared.Options;

/// <summary>
/// A chain key this deployment has retired, together with the sequence it stopped writing at.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ THE BOUNDARY IS NOT BOOKKEEPING — WITHOUT IT THE RING IS A REGRESSION. Selecting a key by the
/// row's <c>KeyId</c> stops a row LYING about which key to check it with, because the id is inside
/// the hashed payload and relabelling breaks the hash. It does not stop a row being MINTED: somebody
/// holding the retired key computes an honest hash under it, labels it honestly, appends it by raw
/// SQL, and a ring that accepts any member key at any sequence verifies it.
/// </para>
/// <para>
/// Measured before this type existed: with an unbounded ring, a row written through the retired key
/// AFTER the rotation verified clean. Before the ring there was no such row — a retired key verified
/// nothing, so its holder could forge nothing. So an unbounded ring hands an old key a power it did
/// not have, which is the opposite of what rotating is for. The tail-anchor decision said this in
/// advance: *"a trial-keyring verifier lets a RETIRED key mint rows at any sequence forever — the
/// forgery surface grows with every rotation, inverting the reason to rotate"*, and asked for "the
/// key-epoch boundary" by name.
/// </para>
/// <para>
/// So a retired key answers only for rows at or below <see cref="LastSequence"/>. A row above it is
/// refused even when its hash is correct, because a correct hash under a key that had no business
/// writing by then is exactly what minting looks like.
/// </para>
/// <para>
/// WHAT IT DOES NOT BUY. Below the boundary the retired key is as powerful as it ever was: whoever
/// holds it can rewrite that stretch of history and recompute it, exactly as the current key's
/// holder can rewrite the present. The boundary bounds the FUTURE of a retired key, not its past —
/// which is the whole of what rotation can achieve without an external anchor.
/// </para>
/// </remarks>
public sealed class RetiredChainKey
{
    /// <summary>The retired key material. Read-side only: writing always uses the current key.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// The highest <c>AuditEvent.Sequence</c> this key legitimately wrote. Rows above it that name
    /// this key are refused.
    /// </summary>
    /// <remarks>
    /// Read it off the table at the moment of rotation — it is the tail sequence when the new key
    /// took over. Recording it too HIGH re-opens the minting window by exactly the difference;
    /// recording it too LOW refuses rows the key really did write, which is loud and correctable
    /// rather than silent. When in doubt, err low.
    /// </remarks>
    public long LastSequence { get; set; }
}
