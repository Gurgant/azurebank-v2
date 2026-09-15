using System.Text.Json;

namespace AzureBank.Shared.Constants;

/// <summary>
/// What the <c>Detail</c> of a success row that consumed a step-up authorisation names: that
/// authorisation, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Until 2026-09-14 the money rows carried no <c>Detail</c> at all (ADR-0044, "What is wired, and
/// what is not"): amount, counterparty
/// and description live on the ledger row the subject reaches, and copying them here would tie
/// financial data to an actor in a table designed never to be purged (D5). An authorisation id is
/// none of those. It is an opaque identifier of the SAME actor's own act, it sits on no ledger row,
/// and without it the only link from a movement to the PIN that paid for it was
/// <c>StepUpAuthorizations.ConsumedByTransactionId</c> — a column in an unchained table, which the
/// evidence pack could only report as "NOT STRONGLY AUTHENTICATED" once the row was gone, with no
/// break anywhere to find. Named here, inside the row the chain hashes, the binding becomes
/// tamper-evident: the name survives the row's deletion, and a re-pointed row no longer matches.
/// </para>
/// <para>
/// The writer renders one key, one Guid, by hand so the shape is fixed:
/// <c>{"authorizationId":"&lt;D&gt;"}</c>.
/// Rows written before this change have a null <c>Detail</c>; <see cref="ConsumedAuthorisationOf"/>
/// answers null for them, and the evidence pack calls them pre-binding rows rather than guessing.
/// </para>
/// </remarks>
public static class AuditDetails
{
    /// <summary>The JSON member naming the consumed authorisation.</summary>
    public const string AuthorizationIdKey = "authorizationId";

    /// <summary>The <c>Detail</c> of a success row that consumed <paramref name="authorizationId"/>.</summary>
    public static string ConsumedAuthorisation(Guid authorizationId)
        => "{\"" + AuthorizationIdKey + "\":\"" + authorizationId.ToString("D") + "\"}";

    /// <summary>
    /// The authorisation a row's <c>Detail</c> names, or null for a pre-binding row, a null detail,
    /// or a detail that is not a JSON object with a D-format string <c>authorizationId</c> member.
    /// Other members are ignored: the reader is looser than the writer. Never throws: the pack
    /// reads rows an attacker may have written, and a malformed detail is a finding to print, not
    /// an exception to die on.
    /// </summary>
    public static Guid? ConsumedAuthorisationOf(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(detail);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(AuthorizationIdKey, out var value)
                && value.ValueKind == JsonValueKind.String
                && Guid.TryParseExact(value.GetString(), "D", out var id))
            {
                return id;
            }
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            // Not this shape -- or not even a UTF-16 string JsonDocument will read: a lone surrogate
            // raises ArgumentException ("Cannot transcode invalid UTF-16 string to UTF-8 JSON text"),
            // not JsonException, and an nvarchar written around the application can hold one. The
            // caller reports the row as it found it (EvidenceVerdictTests pins both).
        }

        return null;
    }
}
