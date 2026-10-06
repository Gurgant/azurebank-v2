using System.Text;
using System.Text.Json;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// A problem body with its <c>traceId</c> cut out, for a test that searches the whole body for
/// something that must not be in it.
/// </summary>
/// <remarks>
/// The id is 32 hexadecimal characters, new on every request, so a search of the whole body for
/// a few digits searches the id too. Four given digits stand somewhere in about one id in 2,260
/// (29 places, one chance in 65,536 at each).
/// <c>Summary_WithAnotherUsersAccountId_ReturnsForbidden</c> holds that a refusal carries no
/// <c>4242</c>, and on such an id it fails with nothing leaked. It failed once on 2026-10-06
/// and passed when run again; the id is the only member of that body that differs between two
/// runs.
/// <para>
/// Only the member named <c>traceId</c> at the top of the body goes, name and value. Every
/// other character comes back as it was sent and in order, so every other member is still
/// searched: a number written after the id, another id of the same shape, a <c>traceId</c>
/// inside another member, and the path in <c>instance</c>. On a route with an id in its path
/// that id is therefore still searched, and it can hold the digits by chance as a trace id
/// can. The comma beside the member stays, so what comes back is a text to search, not one to
/// parse. A body that has no such member, or that cannot be read as JSON as far as that
/// member, comes back whole: the search is then the one it was before this helper
/// (<c>ProblemBodyTests</c> holds the helper to each of these).
/// </para>
/// </remarks>
internal static class ProblemBody
{
    /// <summary>The body's own text, without its top-level <c>traceId</c> member.</summary>
    public static string WithoutTraceId(string body)
    {
        var utf8 = Encoding.UTF8.GetBytes(body);
        try
        {
            var reader = new Utf8JsonReader(utf8);
            while (reader.Read())
            {
                if (reader.CurrentDepth == 1
                    && reader.TokenType == JsonTokenType.PropertyName
                    && reader.ValueTextEquals("traceId"))
                {
                    var nameStarts = (int)reader.TokenStartIndex;
                    reader.Skip();
                    var valueEnds = (int)reader.BytesConsumed;
                    return Encoding.UTF8.GetString(utf8.AsSpan(0, nameStarts))
                        + Encoding.UTF8.GetString(utf8.AsSpan(valueEnds));
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON as far as the member: there is nothing to cut, and all of it is searched.
        }

        return body;
    }
}
