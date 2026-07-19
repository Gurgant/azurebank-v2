using Microsoft.Extensions.Compliance.Redaction;

namespace AzureBank.Api.Observability;

/// <summary>
/// Masks an email address to <c>j***@example.com</c> form: the first character of the
/// local part survives (enough for an operator to eyeball-correlate adjacent log lines),
/// the rest of the local part is dropped, and the domain is kept (useful for spotting
/// e.g. disposable-domain abuse) — the value can no longer identify a person.
/// Malformed input (no <c>'@'</c>, empty, whitespace) collapses to <c>***</c>: when we
/// can't parse it we assume ALL of it is sensitive rather than leak it verbatim.
/// </summary>
public sealed class EmailMaskingRedactor : Redactor
{
    private const string Mask = "***";

    /// <inheritdoc />
    public override int GetRedactedLength(ReadOnlySpan<char> input)
    {
        ComputeShape(input, out var keepFirst, out var tailLength);
        return (keepFirst ? 1 : 0) + Mask.Length + tailLength;
    }

    /// <inheritdoc />
    public override int Redact(ReadOnlySpan<char> source, Span<char> destination)
    {
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
        // Split on the FIRST '@' — any further '@' belongs to the (kept) tail, which is
        // harmless: extra masking is safer than misparsing.
        var at = source.IndexOf('@');
        if (at < 0)
        {
            // Not an email we can safely take apart (covers empty/whitespace too):
            // redact everything.
            keepFirst = false;
            tailLength = 0;
            return;
        }

        keepFirst = at > 0; // an empty local part ("@example.com") has no first char to keep
        tailLength = source.Length - at;
    }
}
