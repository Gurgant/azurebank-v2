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
/// So a retired key answers only for rows inside its own EPOCH — at or below
/// <see cref="LastSequence"/>, and no lower than one past the previous key's boundary. A row on
/// either side of it is refused even when its hash is correct, because a correct hash under a key
/// that had no business writing there is exactly what minting looks like.
/// </para>
/// <para>
/// WHAT IT DOES NOT BUY. Inside its own epoch the retired key is as powerful as it ever was: whoever
/// holds it can rewrite that stretch of history and recompute it, exactly as the current key's
/// holder can rewrite the present. What the boundaries buy is that the damage stays THERE — they
/// partition the sequence space, so a compromised retired key reaches neither the rows later keys
/// wrote nor the rows earlier ones did. Rotation confines a key to one epoch; it cannot make that
/// epoch unrewritable, which is the whole of what rotation can achieve without an external anchor.
/// </para>
/// <para>
/// ⚠️ THIS PARAGRAPH SAID "the boundary bounds the FUTURE of a retired key, not its past" UNTIL THE
/// EPOCH GAINED A START. That was true of the first version of the ring and false of every
/// configuration doing more than one rotation, while the member documentation twelve lines below
/// said the opposite — one file, two security models. It read CONSERVATIVE rather than dangerous,
/// which is why it survived: an incident responder told to treat more of the table as rewritable
/// than really is loses time, not evidence.
/// </para>
/// </remarks>
public sealed class RetiredChainKey
{
    /// <summary>The retired key material. Read-side only: writing always uses the current key.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// The highest <c>AuditEvent.Sequence</c> this key legitimately wrote. Rows above it that name
    /// this key are refused — and so are rows BELOW the epoch it opens.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ THIS ONE NUMBER DEFINES TWO EDGES. The epoch each retired key answers for runs from one
    /// past the PREVIOUS key's boundary up to this one, so the boundaries partition the sequence
    /// space between them and no start is configured anywhere. Recording this value therefore moves
    /// two edges: raising it extends this key's reach upward AND pushes the next key's epoch up with
    /// it. There is no way to state one without the other, which is deliberate — a separately
    /// configured start would be a second place for the same fact.
    /// </para>
    /// <para>
    /// Read it off the table at the moment of rotation — it is the tail sequence when the new key
    /// took over. Getting it wrong therefore costs on both sides, which the single-edge version of
    /// this advice did not say: too HIGH re-opens this key's minting window by the difference AND
    /// pushes the next epoch's start above rows the next key genuinely wrote; too LOW refuses rows
    /// this key really did write AND pulls the next epoch's start down over rows it did not.
    /// </para>
    /// <para>
    /// ⚠️ AND THERE IS NO SAFE DIRECTION TO ERR IN. This said "both directions are loud — a refused
    /// row is a verdict — so err low", which is the single-edge advice one paragraph after
    /// withdrawing the single-edge advice. Too LOW is loud. Too HIGH is loud ONLY once the newer key
    /// has written above the real boundary; while that range is still empty nothing names the newer
    /// key inside it, the walk returns intact, and nothing is reported — which is exactly the window
    /// a retired key needs to mint into, and exactly the state a deployment is in between the
    /// rotation and the next write. Measured, and the transcript is in the runbook's triage table.
    /// Take the number from the rotation record; do not lean either way. Expect the noise, when
    /// there is any, in TWO epochs rather than one.
    /// </para>
    /// </remarks>
    public long LastSequence { get; set; }
}
