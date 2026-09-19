namespace AzureBank.Tests.Integration;

/// <summary>
/// The one reading of a runbook's Markdown both runbook guards share: where its fences are, which
/// of them are SQL, and which lines sit outside every fence.
/// </summary>
/// <remarks>
/// Until 2026-09-17 each guard read fences its own way. <see
/// cref="RunbookSqlParsesSqlServerTests"/> took <c>```sql</c> blocks by regex and nothing else;
/// <see cref="RunbookSqlIsFencedTests"/> let any <c>```</c> or <c>~~~</c> fence, of any language,
/// count as a place SQL may sit. Between them, invalid SQL in a <c>~~~sql</c> block, in a bare
/// fence, or in a <c>```text</c> block passed both guards — measured that day on the PIN runbook,
/// all three green. A fence is SQL here only when the first word of its info string is <c>sql</c>,
/// whichever marker opens it, and the parse check reads exactly those fences.
/// <para>
/// A marker counts only when it is indented three columns or fewer, as CommonMark reads it: four
/// or more (a tab is four) make it text in an indented code block. Until the same day this reader
/// trimmed every line first, and measured on the PIN runbook that let two things through. SQL
/// inside an indented <c>```sql</c> pseudo-fence, which renders as indented code, counted as
/// fenced, so the scan never asked for a real fence; and a four-space <c>```</c> inside a real SQL
/// fence closed it early, leaving the lines after it, up to the true close, parsed by nobody. Both
/// probes fail a guard now: the first the scan, which also reports the indented <c>```sql</c>
/// marker itself, the second the parse check, which reads the whole block to its real close.
/// </para>
/// <para>
/// And a backtick fence's info string holds no backtick, as CommonMark has it. Until 2026-09-19
/// <c>```sql `x`</c> opened a SQL fence here while Markdown renders it, and the statement under
/// it, as a paragraph: measured on the PIN runbook with a valid <c>UPDATE</c> below it, both
/// guards stayed green over SQL that no reader sees as code. Such a line opens nothing now, so
/// what follows it is outside every fence and the scan reports it, marker included. A tilde fence
/// may hold backticks in its info string, and still does.
/// </para>
/// </remarks>
internal static class RunbookMarkdown
{
    /// <summary>
    /// A fenced block: the line its opening marker is on, that marker, its info string, and its
    /// body lines with their numbers.
    /// </summary>
    internal sealed record Fence(
        int OpenLine, string Marker, string Info, IReadOnlyList<(int Line, string Text)> Body)
    {
        /// <summary>SQL is the language the parse check runs; no other fence is read as
        /// SQL.</summary>
        public bool IsSql =>
            Info.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)
                is [var language, ..]
            && language.Equals("sql", StringComparison.OrdinalIgnoreCase);

        /// <summary>The body as one string, the way it is sent to SQL Server.</summary>
        public string Text => string.Join("\n", Body.Select(line => line.Text));
    }

    /// <summary>
    /// Splits a document into its fences and the lines outside all of them. A fence opens on a run
    /// of three or more backticks or tildes, and closes only on a line holding nothing but a run of
    /// the same character at least as long; a fence never closed runs to the end of the document.
    /// </summary>
    internal static (IReadOnlyList<Fence> Fences, IReadOnlyList<(int Line, string Text)> Outside)
        Read(IEnumerable<string> lines)
    {
        var fences = new List<Fence>();
        var outside = new List<(int Line, string Text)>();
        (int Line, string Marker, string Info, List<(int Line, string Text)> Body)? open = null;
        var number = 0;

        foreach (var line in lines)
        {
            number++;
            var trimmed = line.Trim();
            var marker = LeadingColumns(line) <= 3 ? FenceMarker(trimmed) : string.Empty;

            if (open is not { } fence)
            {
                if (marker.Length > 0 && !(marker[0] == '`' && trimmed[marker.Length..].Contains('`')))
                {
                    open = (number, marker, trimmed[marker.Length..].Trim(), []);
                }
                else
                {
                    outside.Add((number, line));
                }

                continue;
            }

            if (marker.Length >= fence.Marker.Length
                && marker[0] == fence.Marker[0]
                && trimmed.Length == marker.Length)
            {
                fences.Add(new Fence(fence.Line, fence.Marker, fence.Info, fence.Body));
                open = null;
                continue;
            }

            fence.Body.Add((number, line));
        }

        if (open is { } unclosed)
        {
            fences.Add(new Fence(unclosed.Line, unclosed.Marker, unclosed.Info, unclosed.Body));
        }

        return (fences, outside);
    }

    /// <summary>
    /// How far a line is indented, in columns: a space is one, a tab moves to the next multiple of
    /// four.
    /// </summary>
    private static int LeadingColumns(string line)
    {
        var columns = 0;
        foreach (var character in line)
        {
            if (character == ' ')
            {
                columns++;
            }
            else if (character == '\t')
            {
                columns += 4 - (columns % 4);
            }
            else
            {
                break;
            }
        }

        return columns;
    }

    /// <summary>The backtick or tilde run a trimmed line opens with, at least three long.</summary>
    private static string FenceMarker(string trimmed)
    {
        if (trimmed.Length < 3 || (trimmed[0] != '`' && trimmed[0] != '~'))
        {
            return string.Empty;
        }

        var length = 0;
        while (length < trimmed.Length && trimmed[length] == trimmed[0])
        {
            length++;
        }

        return length >= 3 ? trimmed[..length] : string.Empty;
    }
}
