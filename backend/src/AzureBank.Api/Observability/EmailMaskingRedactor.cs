using System.Globalization;
using Microsoft.Extensions.Compliance.Redaction;

namespace AzureBank.Api.Observability;

/// <summary>
/// Masks an email address to <c>j***@example.com</c> form: the first character of the
/// local part survives (enough for an operator to eyeball-correlate adjacent log lines),
/// the rest of the local part is dropped, and the domain is kept (useful for spotting
/// e.g. disposable-domain abuse). This is PSEUDONYMISATION, not anonymisation: masked
/// lines remain personal data for GDPR retention/access purposes — the control reduces
/// exposure, it does not exempt the logs from data-protection duties.
///
/// The domain tail is kept VERBATIM, so it is only kept when the input is provably a
/// single well-formed address: exactly one <c>'@'</c>, non-empty on both sides, and no
/// control/format/separator/whitespace character anywhere. Everything else collapses to
/// <c>***</c> — a redactor is a trust boundary, and echoing a crafted "email" like
/// <c>a@b\r\n[WARN]</c> would hand log-forging a path straight through the PII defence.
/// </summary>
public sealed class EmailMaskingRedactor : Redactor
{
    private const string Mask = "***";

    /// <inheritdoc />
    public override int GetRedactedLength(ReadOnlySpan<char> input)
    {
        // Empty in -> empty out, matching the base class's string overload (which
        // short-circuits "" before these overrides run). Without this, a direct span-based
        // caller would get a misleading "***" for a field that was simply empty.
        if (input.IsEmpty)
        {
            return 0;
        }

        ComputeShape(input, out var keepFirst, out var tailLength);
        return (keepFirst ? 1 : 0) + Mask.Length + tailLength;
    }

    /// <inheritdoc />
    public override int Redact(ReadOnlySpan<char> source, Span<char> destination)
    {
        // Keep both overloads in agreement (see GetRedactedLength).
        if (source.IsEmpty)
        {
            return 0;
        }

        ComputeShape(source, out var keepFirst, out var tailLength);
        var length = (keepFirst ? 1 : 0) + Mask.Length + tailLength;
        if (destination.Length < length)
        {
            throw new ArgumentException(
                $"Destination is too small ({destination.Length} < {length} chars).", nameof(destination));
        }

        var written = 0;
        if (keepFirst)
        {
            destination[written++] = source[0];
        }
        Mask.CopyTo(destination[written..]);
        written += Mask.Length;
        if (tailLength > 0)
        {
            // The tail is the first '@' plus everything after it (the domain).
            source[^tailLength..].CopyTo(destination[written..]);
            written += tailLength;
        }
        return written;
    }

    /// <summary>
    /// Single source of truth for the output shape, shared by both overrides so
    /// <see cref="GetRedactedLength"/> can never disagree with <see cref="Redact"/>
    /// (the base class sizes buffers off the former before calling the latter).
    /// </summary>
    private static void ComputeShape(ReadOnlySpan<char> source, out bool keepFirst, out int tailLength)
    {
        // The tail (first '@' + domain) is echoed VERBATIM, so it may only survive when the
        // input is provably one well-formed address. Anything else — no '@', empty local part
        // or domain, a second '@' (could embed another address), or ANY control/format/
        // separator/whitespace character (could forge log lines through the kept tail, e.g.
        // "a@b\r\n[WARN]") — collapses to the bare mask.
        var at = source.IndexOf('@');
        var wellFormed =
            at > 0                                        // non-empty local part
            && at < source.Length - 1                     // non-empty domain
            && source[(at + 1)..].IndexOf('@') < 0        // exactly one '@'
            && !ContainsUnsafeChar(source);
        if (!wellFormed)
        {
            keepFirst = false;
            tailLength = 0;
            return;
        }

        keepFirst = true;
        tailLength = source.Length - at;
    }

    /// <summary>
    /// True when any character could alter log rendering or hide content: whitespace
    /// (incl. Unicode line/paragraph separators), C0/C1 controls, format characters
    /// (e.g. RTL override), surrogates, private-use, or unassigned code points.
    /// Mirrors the character classes LogSanitizer strips — but here we REFUSE rather
    /// than strip: a "cleaned" malformed email is still not provably an email.
    /// </summary>
    private static bool ContainsUnsafeChar(ReadOnlySpan<char> source)
    {
        foreach (var c in source)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                return true;
            }

            switch (CharUnicodeInfo.GetUnicodeCategory(c))
            {
                case UnicodeCategory.Format:
                case UnicodeCategory.Surrogate:
                case UnicodeCategory.PrivateUse:
                case UnicodeCategory.OtherNotAssigned:
                    return true;
            }
        }

        return false;
    }
}
