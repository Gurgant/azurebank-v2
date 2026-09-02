using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AzureBank.Infrastructure.Data;

/// <summary>Renders, authenticates and chains <see cref="AuditAnchor"/> records.</summary>
public interface IAuditAnchorChain
{
    /// <summary>Reads the newest anchor, or null when none has ever been written.</summary>
    Task<AuditAnchor?> ReadTailAsync(DbContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks that a record was written by somebody holding <c>Audit:AnchorKey</c> and has not been
    /// altered since.
    /// </summary>
    AuditAnchorCheck Check(AuditAnchor anchor);

    /// <summary>
    /// Builds the next record in the chain from a completed walk, filling every derived field.
    /// </summary>
    AuditAnchor Build(AuditChainVerification verification, AuditAnchor? tail, DateTime createdAtUtc);

    /// <summary>
    /// Walks every record in order and reports the first one that is missing, mis-linked or not
    /// authentic.
    /// </summary>
    /// <remarks>
    /// THIS IS WHAT MAKES "DELETING AN INTERIOR RECORD IS LOUD" TRUE -- and only the interior: a
    /// SUFFIX removal leaves 1..n with every link met and nothing here asks how tall the chain
    /// should be, which DeletingAnchorsIsLoudONLYINTHEINTERIOR_ANDASUFFIXISSILENT measures both
    /// ways. Without this walk even the interior half was a claim
    /// with no mechanism under it. Authenticating only the newest record leaves an interior deletion
    /// invisible: the survivor still verifies, and a later run extends a chain with a hole in it. A
    /// gapping counter and links that fail to meet are only signals if something looks, and this is
    /// the thing that looks.
    /// </remarks>
    Task<AuditAnchorChainVerification> VerifyChainAsync(
        DbContext context, CancellationToken cancellationToken = default);
}

/// <summary>What walking the whole anchor chain found.</summary>
/// <param name="Verified">How many records verified before the walk stopped.</param>
/// <param name="FirstBrokenSequence">Where it stopped, or null when it reached the end.</param>
/// <param name="Reason">What went wrong there, in a sentence an operator can act on.</param>
/// <param name="Kind">Which kind of break it was.</param>
/// <param name="DeepestCovered">
/// The highest <c>CoveredThroughSequence</c> the walk saw, or null when nothing it read covers
/// anything. Gap markers contribute nothing by construction.
/// </param>
/// <param name="Records">How many records the walk read.</param>
public readonly record struct AuditAnchorChainVerification(
    long Verified,
    long? FirstBrokenSequence,
    string? Reason,
    AuditAnchorChainBreakKind Kind = AuditAnchorChainBreakKind.None,
    long? DeepestCovered = null,
    long Records = 0)
{
    /// <summary>True when every record read verified and none was missing.</summary>
    public bool IsIntact => FirstBrokenSequence is null;
}

/// <summary>How an anchor chain walk failed.</summary>
/// <remarks>
/// Four kinds rather than one, for the reason the row chain's equivalent gives: an operator acts
/// differently on each, and a caller that has to match on the wording of a sentence is a caller that
/// breaks when the sentence is improved.
/// </remarks>
public enum AuditAnchorChainBreakKind
{
    /// <summary>No break: the walk reached the end.</summary>
    None = 0,

    /// <summary>
    /// The counter skips, or does not begin at 1. A record was removed, or the chain was started
    /// twice.
    /// </summary>
    MissingRecord,

    /// <summary>A record names a predecessor that is not the record before it.</summary>
    LinkBroken,

    /// <summary>A record does not match what its own content produces. This is a write.</summary>
    Unauthentic,

    /// <summary>
    /// A record names a scheme or a key this run cannot apply, so it was NOT checked - which is never
    /// the same as checked and good.
    /// </summary>
    UnknownScheme,
}

/// <summary>What checking a single anchor record concluded.</summary>
/// <remarks>
/// THREE OUTCOMES AND NOT TWO, for the reason the row walk already learned: "I cannot check this"
/// and "this is wrong" are different sentences, and collapsing them hands an attacker a muzzle.
/// Overwrite a tampered record's key identity with something no key produces and a two-valued answer
/// would soften from tampering to housekeeping.
/// </remarks>
public enum AuditAnchorCheck
{
    /// <summary>Rendered under a scheme and key this build holds, and its MAC matches.</summary>
    Authentic,

    /// <summary>
    /// Names a payload version or a key identity this build cannot apply, so its MAC was never
    /// recomputed. Unchecked, never proved good — and still a refusal to build on.
    /// </summary>
    UnknownScheme,

    /// <summary>
    /// The record does not match what its own content produces, under the key it names. A write.
    /// </summary>
    /// <remarks>
    /// COVERS BOTH DERIVED VALUES, not only the authentication code the name mentions. The stored
    /// payload hash cannot live inside the payload it hashes, so the code does not cover it and it is
    /// recomputed separately - a record whose stored hash disagrees with its own content has been
    /// written to just as surely as one whose code does.
    /// </remarks>
    MacMismatch,
}

/// <inheritdoc cref="IAuditAnchorChain"/>
public class AuditAnchorChain : IAuditAnchorChain
{
    /// <summary>The anchor payload version new records are written under.</summary>
    /// <remarks>
    /// Said "rendering" while there was only one value, where it was vacuous rather than wrong. A
    /// second value made it a claim, and the claim is false: see <c>LegacyPayloadVersion</c> below,
    /// which is where this ladder's vocabulary is settled and why it differs from the row chain's.
    /// </remarks>
    internal const string CurrentPayloadVersion = "a2";

    /// <summary>
    /// The payload version records were written under while <c>VerifiedUnderChainKeyId</c> named
    /// the key the RUN held rather than the key that authenticated the tail.
    /// </summary>
    /// <remarks>
    /// ⚠️ NOTHING HERE CALLS <c>a1</c> AND <c>a2</c> TWO RENDERINGS, AND THAT IS DELIBERATE. This
    /// repository uses that word for a format that DIFFERS — the row chain's <c>v2</c> and <c>v3</c>
    /// earn it, since <c>v3</c> added the key identity to the payload — and borrowing it here would
    /// import exactly the difference the next sentence denies. Two versions, one rendering.
    /// <para>
    /// ⚠️ THE SHAPE DID NOT CHANGE, THE MEANING DID — which is exactly why the version had to move.
    /// Twelve elements in the same order render identically under both, and an <c>a1</c> record's
    /// bytes still authenticate: there is ONE renderer and it is right for both. What an <c>a1</c>
    /// record cannot do is say which of the two things its ninth element meant, and that element
    /// travels inside <c>AnchoredValue</c> — the one value here meant to be attested by somebody
    /// outside this system. Two imprints under one scheme string, meaning different things, is the
    /// ambiguity anchoring exists to remove.
    /// </para>
    /// </remarks>
    internal const string LegacyPayloadVersion = "a1";

    /*
      A SEPARATE DOMAIN STRING FROM THE ROW KEY'S, and the reason is not collision-avoidance. Each of
      these constants is frozen the moment the first record hashes one, and welding the anchor's to
      the row's would mean bumping either silently re-derives the other's identities. They are
      independent ladders that must be free to move apart.
    */
    private const string AnchorKeyIdDomain = "AzureBank.Audit.AnchorKeyId.v1";
    private const int KeyIdHexLength = 16;

    private readonly IOptions<AuditOptions> _options;
    private readonly string _anchorKeyId;

    public AuditAnchorChain(IOptions<AuditOptions> options)
    {
        _options = options;
        _anchorKeyId = DeriveAnchorKeyId(options.Value.AnchorKey);
    }

    /// <summary>The non-secret identity of an anchor key.</summary>
    internal static string DeriveAnchorKeyId(string anchorKey)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(anchorKey));
        return Convert.ToHexStringLower(
            hmac.ComputeHash(Encoding.UTF8.GetBytes(AnchorKeyIdDomain)))[..KeyIdHexLength];
    }

    public Task<AuditAnchor?> ReadTailAsync(DbContext context, CancellationToken cancellationToken = default)
        /*
          A PLAIN READ. No UPDLOCK, no HOLDLOCK, no transaction -- and the contrast with the audit
          chain's tail read is deliberate rather than an oversight. That one is serialised because a
          money movement is refused if its audit row cannot be written, so writers must QUEUE rather
          than fail; measured, that lock costs seconds of cross-account stall. An operator tool wants
          the opposite: fail fast and be re-run for free. Two concurrent runs collide on the primary
          key, loudly, which is the correct answer to a double launch.
        */
        => context.Set<AuditAnchor>()
            .AsNoTracking()
            .OrderByDescending(a => a.AnchorSequence)
            .FirstOrDefaultAsync(cancellationToken);

    public AuditAnchorCheck Check(AuditAnchor anchor)
    {
        /*
          THE GATE WIDENED WHEN THE VERSION MOVED, AND THE TWO ARE ONE CHANGE. A bare bump refuses
          every record already written -- at record ONE, which is where the anchor walk starts -- and
          `anchor` then refuses to append forever with no --force to get past it. So the ladder is a
          ONE-WAY DOOR: the legacy arm ships in the same commit as the bump or the bump does not
          ship. Pinned by the sample-ladder test, which checks four genuine 'a1' records written
          under the key this repository publishes.

          There is no renderer fork below this line and there must not be one. Both versions render
          the same twelve elements in the same order; only what element nine MEANS differs, and the
          version string is the record of that.
        */
        if (anchor.PayloadVersion is not (CurrentPayloadVersion or LegacyPayloadVersion))
        {
            return AuditAnchorCheck.UnknownScheme;
        }

        if (anchor.AnchorKeyId != _anchorKeyId)
        {
            return AuditAnchorCheck.UnknownScheme;
        }

        var payload = RenderPayload(anchor);

        /*
          THE STORED PAYLOAD HASH IS CHECKED FIRST, and its absence here was a real hole rather than
          untidiness. PayloadHash cannot be INSIDE the payload -- it is a hash of it, so that would be
          circular -- which means the authentication code does not cover it, and somebody holding the
          database can rewrite it freely with that code still verifying.

          What that buys them is laundering. The NEXT record links to tail.PayloadHash, so a run that
          accepted this one would genuinely authenticate a link to a value of their choosing. The same
          reasoning already put AnchoredValue INSIDE the payload as a literal element; this is the one
          derived value that cannot go there, so it is recomputed here instead.
        */
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(anchor.PayloadHash), Encoding.ASCII.GetBytes(Sha256(payload))))
        {
            return AuditAnchorCheck.MacMismatch;
        }

        var expected = ComputeMac(payload);
        return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(anchor.Mac), Encoding.ASCII.GetBytes(expected))
            ? AuditAnchorCheck.Authentic
            : AuditAnchorCheck.MacMismatch;
    }

    public async Task<AuditAnchorChainVerification> VerifyChainAsync(
        DbContext context, CancellationToken cancellationToken = default)
    {
        /*
          ASCENDING, ALL OF IT, AND STREAMED. The table holds one record per operator run, so it is
          small by construction -- but "small" is a fact about today rather than a guarantee, and the
          row walk this mirrors reads its chain the same way for the same reason.

          THE ORDER OF THE THREE CHECKS IS DELIBERATE. The counter first, because a missing record is
          the move this chain exists to make loud and it must never be reported as something subtler.
          Then authenticity, because a record that does not match itself cannot be trusted to say what
          it follows. The link last, since it is only meaningful between two records that are both
          what they claim to be.
        */
        long verified = 0;
        string? previousPayloadHash = null;
        var expectedSequence = 1L;

        /*
          THE COVERAGE COMES FROM THIS WALK, NOT FROM A QUERY AFTERWARDS.

          A caller that verified the chain here and then asked the table for MAX(CoveredThroughSequence)
          separately would be describing two different instants: a record added between the two reads
          is counted in the maximum and was never verified, and the coverage then reads DEEPER than
          anything this walk vouched for -- which makes an uncovered window come out SMALLER than the
          truth, the one direction that must not be wrong.

          This repository has already solved the same problem one layer down and written the fix
          into VerifyCommand: the sequence range used to be two extra queries and printed 101 rows
          verified over a range ending at 100, and it was fixed by taking the range FROM the walk.
          Same shape, same fix.

          Gap markers contribute nothing, which is not a special case: their coverage column is null
          by construction, so a marker simply never raises the maximum.
        */
        long? deepestCovered = null;

        await foreach (var record in context.Set<AuditAnchor>()
            .AsNoTracking()
            .OrderBy(a => a.AnchorSequence)
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken))
        {
            if (record.AnchorSequence != expectedSequence)
            {
                return new AuditAnchorChainVerification(
                    verified,
                    expectedSequence,
                    $"Anchor {expectedSequence} is missing: the next record read is "
                    + $"{record.AnchorSequence}. The writer assigns this counter gaplessly, so a skip "
                    + "means a record was removed -- and removing one is the single move this chain "
                    + "is built to make loud.",
                    AuditAnchorChainBreakKind.MissingRecord);
            }

            var check = Check(record);
            if (check is AuditAnchorCheck.UnknownScheme)
            {
                return new AuditAnchorChainVerification(
                    verified,
                    record.AnchorSequence,
                    $"Anchor {record.AnchorSequence} names a scheme or a key this run cannot apply, "
                    + "so it was NOT checked -- which is never the same as checked and good. Either "
                    + "you hold a different Audit:AnchorKey than the run that wrote it, this build "
                    + "cannot render the version it declares, or the record was overwritten.",
                    AuditAnchorChainBreakKind.UnknownScheme);
            }

            if (check is AuditAnchorCheck.MacMismatch)
            {
                return new AuditAnchorChainVerification(
                    verified,
                    record.AnchorSequence,
                    $"Anchor {record.AnchorSequence} does not match what its own content produces, and "
                    + "it names the key this run holds -- so the key is not in question. This is a "
                    + "write. Preserve the table and escalate.",
                    AuditAnchorChainBreakKind.Unauthentic);
            }

            if (record.PreviousAnchorPayloadHash != previousPayloadHash)
            {
                return new AuditAnchorChainVerification(
                    verified,
                    record.AnchorSequence,
                    $"Anchor {record.AnchorSequence} expected to follow "
                    + $"'{previousPayloadHash ?? "(start of chain)"}' but records "
                    + $"'{record.PreviousAnchorPayloadHash ?? "(start of chain)"}'. The links do not "
                    + "meet, which is what a substituted record looks like.",
                    AuditAnchorChainBreakKind.LinkBroken);
            }

            previousPayloadHash = record.PayloadHash;
            if (record.CoveredThroughSequence is { } through
                && (deepestCovered is null || through > deepestCovered))
            {
                deepestCovered = through;
            }

            expectedSequence++;
            verified++;
        }

        return new AuditAnchorChainVerification(
            verified, null, null, DeepestCovered: deepestCovered, Records: verified);
    }

    public AuditAnchor Build(AuditChainVerification verification, AuditAnchor? tail, DateTime createdAtUtc)
    {
        /*
          A RECORD IS AN ANCHOR ONLY OVER AN INTACT WALK THAT ACTUALLY READ SOMETHING. Everything
          else is a gap marker, including an empty table -- because an empty audit table is not
          "anchoring is not in use yet", it is a table that reports exactly what a truncated one
          reports, and recording that it was empty on this date is itself evidence.
        */
        var anchorable = verification.IsIntact && verification.Verified > 0;

        /*
          AN ANCHORABLE WALK MUST HAND OVER BOTH TAIL VALUES, AND THIS REFUSES RATHER THAN GUESSES.
          The hash and the key that authenticated it leave the walk on adjacent lines precisely so
          they cannot disagree -- but `Build` is public, takes a public record struct, and
          `TailChainKeyId` is a trailing OPTIONAL parameter, so any caller can hand over a tail hash
          with no key by simply not passing one. That is not hypothetical carelessness: it is the
          shape every construction of this type had before the field existed.

          What it would produce is the exact defect this design was written to remove, restored
          silently. The anchor would take the hash and fall back to the key the RUN holds, and after
          a rotation that is a different key -- a record naming one key beside a hash only another
          can check, carrying a VALID MAC over the lie, published inside AnchoredValue for a third
          party to attest. Nothing downstream can detect it, because everything downstream trusts
          this record.

          ArgumentException rather than an operator sentence: no code in this tree can reach it --
          `VerifyAsync` is the only producer and it sets both or neither -- so this is a programming
          error at a public boundary, not a configuration an operator can fix.
        */
        if (anchorable && (verification.TailRowHash is null || verification.TailChainKeyId is null))
        {
            throw new ArgumentException(
                "An intact walk over a non-empty table must carry both TailRowHash and "
                + "TailChainKeyId. Anchoring a tail hash without the identity of the key that "
                + "authenticated it would record the key this run happens to hold, which after a "
                + "rotation is not the key behind the hash.",
                nameof(verification));
        }

        var anchor = new AuditAnchor
        {
            AnchorSequence = (tail?.AnchorSequence ?? 0) + 1,
            PayloadVersion = CurrentPayloadVersion,
            AnchorKeyId = _anchorKeyId,
            Kind = anchorable ? AuditAnchorKind.Anchor : AuditAnchorKind.GapMarker,
            LowestCoveredSequence = anchorable ? verification.LowestSequence : null,
            CoveredThroughSequence = anchorable ? verification.HighestSequence : null,
            CoveredRowCount = anchorable ? verification.Verified : null,
            TailRowHash = anchorable ? verification.TailRowHash : null,
            /*
              THE KEY THAT AUTHENTICATED THE TAIL, NOT THE KEY THIS RUN HOLDS. Those are the same
              string on every deployment that has never rotated, which is why this read as correct
              for so long: it is wrong only in the window a rotation opens, between the rotation and
              the first row written under the new key. In that window the walk certifies a tail a
              RETIRED key authenticated, and the old code filed it under the current key's identity
              -- an anchor naming one key beside a hash only another key can check.

              ⚠️ GATED ON `anchorable`, THE SAME CONDITION AS THE HASH ONE LINE ABOVE, and the first
              version of this was not. It read `TailChainKeyId ?? DeriveKeyId(current)` and argued
              that gating it would be "the second place the rule lives" -- which is refuted by the
              line directly above, where that identical condition already decides the very value
              this one is supposed to travel with. One place, applied to one of the two fields, is
              not one place; it is a seam.

              What the seam allowed was narrow and bad: a verification carrying a tail key but NOT
              intact got its hash discarded by the gate and its key taken by the fallback, so a GAP
              MARKER came out naming the tail's key. This field means "the key the run held" on a
              gap marker, and the record would have said otherwise while authenticating perfectly.

              Now both come from `anchorable` or neither does. Above it, the guard refuses the other
              half -- anchorable with no key -- rather than falling back, because falling back there
              is the original defect wearing this change's clothes.
            */
            VerifiedUnderChainKeyId = anchorable
                ? verification.TailChainKeyId!
                : AuditChain.DeriveKeyId(_options.Value.ChainKey),
            PreviousAnchorPayloadHash = tail?.PayloadHash,
            CreatedAt = createdAtUtc,
        };

        // Order matters: the anchored value is an element of the payload, so it is computed first.
        anchor.AnchoredValue = anchorable ? ComputeAnchoredValue(anchor) : null;

        var payload = RenderPayload(anchor);
        anchor.PayloadHash = Sha256(payload);
        anchor.Mac = ComputeMac(payload);
        return anchor;
    }

    /*
      TWELVE ELEMENTS, AND THE ORDER IS FROZEN the moment one record exists.

      Element one is the STORED version rather than a literal, so the column and the prefix are the
      same string: overwrite the column and the MAC stops matching, which means the declaration is
      protected by the very thing it selects.

      '|' is safe here without the row payload's "put the free-text field last" rule, because no
      element is caller-supplied -- every one is hex, a digit string, a fixed enum name, or a
      version string this class owns. That is the single respect in which this payload is simpler
      than the row's. (It ended on the literal "a1" until a second version existed, which is the
      whole argument for naming the kind of thing rather than the value of the day.)
    */
    private static string RenderPayload(AuditAnchor a) => string.Join('|',
        a.PayloadVersion,
        a.AnchorKeyId,
        a.AnchorSequence.ToString(CultureInfo.InvariantCulture),
        a.Kind.ToString(),
        a.LowestCoveredSequence?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        a.CoveredThroughSequence?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        a.CoveredRowCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        a.TailRowHash ?? string.Empty,
        a.VerifiedUnderChainKeyId,
        a.PreviousAnchorPayloadHash ?? string.Empty,
        a.AnchoredValue ?? string.Empty,
        a.CreatedAt.Ticks.ToString(CultureInfo.InvariantCulture));

    /*
      THE IMPRINT: EIGHT ELEMENTS, UNKEYED, and four of the twelve above are deliberately absent.

      AnchorKeyId, because this value is meant to be attested by a third party and must survive a
      rotation -- attesting which key authenticated US is provenance about us, not about the chain.
      CreatedAt, because our own clock is not something anybody should be invited to treat as
      attested. Kind, because a gap marker has no imprint at all, so the label has nothing to qualify
      inside a digest that only exists for anchors. And the MAC, which cannot be here: it is computed
      over a payload containing this value, so the reverse would be circular -- with the consequence,
      worth stating plainly, that no third party ever attests which key authenticated an anchor.

      AnchorSequence and PreviousAnchorPayloadHash ARE here, which makes every imprint unique even
      over an unchanged chain. Without that, two runs produce the same imprint and a timestamp token
      is relocatable between them -- and a timestamp authority is required not to examine what it
      signs, so nothing downstream would object.
    */
    private static string ComputeAnchoredValue(AuditAnchor a) => Sha256(string.Join('|',
        a.PayloadVersion,
        a.AnchorSequence.ToString(CultureInfo.InvariantCulture),
        a.LowestCoveredSequence?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        a.CoveredThroughSequence?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        a.CoveredRowCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        a.TailRowHash ?? string.Empty,
        a.VerifiedUnderChainKeyId,
        a.PreviousAnchorPayloadHash ?? string.Empty));

    private string ComputeMac(string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.Value.AnchorKey));
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }

    private static string Sha256(string payload)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
}
