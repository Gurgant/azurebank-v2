namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Every header of an answer, by name, with its value but for three that change on every request,
/// for a test that holds two answers alike.
/// </summary>
/// <remarks>
/// The date, the correlation id (new on every request the caller sent none for) and the length,
/// which carries the trace id's, are written by name only, so a header that one answer carries and
/// the other does not still tells them apart. The text has no braces, so a failure of a comparison
/// can be read (<see cref="ComparableText"/>).
/// </remarks>
internal static class AnswerHeaders
{
    private static readonly HashSet<string> PerRequest =
        new(StringComparer.OrdinalIgnoreCase) { "Date", "X-Correlation-ID", "Content-Length" };

    /// <summary>One line a header, ordered by name, as <c>Name: value</c>.</summary>
    public static string Of(HttpResponseMessage response) =>
        string.Join("\n", response.Headers.Concat(response.Content.Headers)
            .OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
            .Select(h => PerRequest.Contains(h.Key)
                ? $"{h.Key}: (per request)"
                : $"{h.Key}: {string.Join(", ", h.Value)}"));
}
