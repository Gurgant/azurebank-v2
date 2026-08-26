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

    /// <summary>Recomputed under the key it names, and the MAC does not match. This is a write.</summary>
    MacMismatch,
}

/// <inheritdoc cref="IAuditAnchorChain"/>
public class AuditAnchorChain : IAuditAnchorChain
{
    /// <summary>The anchor payload rendering new records are written with.</summary>
    internal const string CurrentPayloadVersion = "a1";

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
        if (anchor.PayloadVersion != CurrentPayloadVersion)
        {
            return AuditAnchorCheck.UnknownScheme;
        }

        if (anchor.AnchorKeyId != _anchorKeyId)
        {
            return AuditAnchorCheck.UnknownScheme;
        }

        var expected = ComputeMac(RenderPayload(anchor));
        return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(anchor.Mac), Encoding.ASCII.GetBytes(expected))
            ? AuditAnchorCheck.Authentic
            : AuditAnchorCheck.MacMismatch;
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
            VerifiedUnderChainKeyId = AuditChain.DeriveKeyId(_options.Value.ChainKey),
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
      element is caller-supplied -- every one is hex, a digit string, a fixed enum name, or "a1".
      That is the single respect in which this payload is simpler than the row's.
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
