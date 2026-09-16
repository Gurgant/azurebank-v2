using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Keeps runbook SQL where the parse check can see it: every runbook is in that check's table, and
/// no runbook holds SQL outside a fence. Neither needs SQL Server, so both run on every build.
/// </summary>
/// <remarks>
/// <para>
/// The PIN runbook's six SQL blocks, two of them hand-run UPDATEs, were Markdown INDENTED code until
/// 2026-09-11. They render exactly like fenced code, and a check that looks for <c>```sql</c> fences
/// never sees them — which is how they went unparsed while
/// <see cref="RunbookSqlParsesSqlServerTests"/> stayed green.
/// </para>
/// <para>
/// ⚠️ <b>AND UNTIL 2026-09-16 THIS SAW ONLY INDENTED, UPPERCASE SQL.</b> The pattern asked for four
/// spaces or a tab followed by a capitalised keyword, so
/// <c>update Users set PinHash = null where Id = 1;</c> written flush left slipped past it — and
/// the parse check next door reads <c>```sql</c> fences only, so nothing else would have looked at
/// it either. It now matches in any case, at any indentation, and behind the Markdown furniture a
/// statement can sit behind: blockquote marks, bullets, numbered steps.
/// </para>
/// <para>
/// <b>Each verb carries the rest of its own grammar, because a bare verb is usually prose.</b>
/// Measured over the two runbooks on 2026-09-16: eleven lines outside fences open with one of these
/// words — <i>"Delete the pickup directory after the notices in it have been dealt with"</i>,
/// <i>"Use the notice's own `Event` in that WHERE clause"</i>, <i>"If the dev or test database is
/// simply behind on migrations"</i> — and none of them is SQL. So <c>DELETE</c> must reach its
/// <c>FROM</c>, <c>UPDATE</c> its <c>SET</c>, <c>USE</c> its semicolon; a statement short enough to
/// carry no grammar at all, <c>select 1;</c>, is caught by that semicolon.
/// <see cref="TheScanReadsLowercaseAndFlushLeftSql_AndLeavesProseAlone"/> is the control: it holds
/// the lines this pattern must catch and the prose it must not, so a later tightening that blinds
/// it fails here rather than in a runbook nobody parsed.
/// </para>
/// </remarks>
public sealed class RunbookSqlIsFencedTests
{
    /// <summary>
    /// A line that OPENS a SQL statement, in any case, at any indentation. Every branch but the
    /// last names the statement's own shape; the last accepts any of these verbs on a line that
    /// ends in a semicolon, which is what a one-line statement looks like when it has no shape.
    /// </summary>
    private static readonly Regex SqlStatement = new(
        @"^(?:
              SELECT\s+.*\bFROM\b
            | UPDATE\s+[\w.\[\]\#@]+\s+SET\b
            | INSERT\s+INTO\b
            | DELETE\s+FROM\b
            | DECLARE\s+@
            | EXEC(?:UTE)?\s+(?:AS\b|[\w.\[\]\#@])
            | MERGE\s+[\w.\[\]\#]+\s+USING\b
            | (?:ALTER|CREATE|DROP|TRUNCATE)\s+(?:TABLE|INDEX|VIEW|PROC|PROCEDURE|FUNCTION
               |DATABASE|LOGIN|USER|ROLE|SCHEMA|TRIGGER|SEQUENCE|TYPE)\b
            | WITH\s+[\w\[\]]+\s+AS\s*\(
            | BEGIN\s+(?:TRAN|TRANSACTION|TRY|CATCH)\b
            | SET\s+(?:@\w+\s*=|PARSEONLY|NOEXEC|NOCOUNT|XACT_ABORT|ANSI_NULLS|QUOTED_IDENTIFIER
               |TRANSACTION\s+ISOLATION)\b
            | USE\s+\[?\w+\]?\s*;
            | KILL\s+\d
            | (?:BACKUP|RESTORE)\s+(?:DATABASE|LOG|HEADERONLY|FILELISTONLY)\b
            | GRANT\s+.*\bTO\b
            | REVOKE\s+.*\b(?:FROM|TO)\b
            | IF\s+(?:EXISTS\s*\(|NOT\s+EXISTS\s*\(|OBJECT_ID\s*\(|@)
            | (?:SELECT|UPDATE|INSERT|DELETE|DECLARE|EXEC|EXECUTE|MERGE|ALTER|CREATE|DROP|TRUNCATE
               |WITH|BEGIN|COMMIT|ROLLBACK|SET|USE|KILL|GRANT|REVOKE)\b.*;\s*$
          )",
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace
            | RegexOptions.CultureInvariant);

    /// <summary>Blockquote marks, bullets and numbered steps a statement can sit behind.</summary>
    private static readonly Regex MarkdownLead = new(@"^\s*(?:(?:>\s?)+|[-*+]\s+|\d+[.)]\s+)*");

    /// <summary>A fence opening or closing: three or more backticks or tildes.</summary>
    private static readonly Regex FenceMarker = new(@"^(?:`{3,}|~{3,})");

    private static string Root => RunbookSqlParsesSqlServerTests.RepositoryRoot().FullName;

    private static List<string> RunbooksOnDisk() =>
        Directory.EnumerateFiles(Path.Combine(Root, "docs", "runbooks"), "*.md")
            .Select(path => "docs/runbooks/" + Path.GetFileName(path))
            .ToList();

    /// <summary>
    /// The lines of a Markdown document that open a SQL statement outside every fence, as
    /// "<c>line number: text</c>". A fence closes only on its own marker, so a ``` inside a ~~~
    /// block does not end it.
    /// </summary>
    private static List<string> SqlOutsideFences(IEnumerable<string> lines)
    {
        var found = new List<string>();
        string? fence = null;
        var number = 0;

        foreach (var line in lines)
        {
            number++;
            var trimmed = line.TrimStart();
            var marker = FenceMarker.Match(trimmed);
            if (marker.Success)
            {
                if (fence is null)
                {
                    fence = marker.Value;
                }
                else if (trimmed.StartsWith(fence, StringComparison.Ordinal)
                    && trimmed[fence.Length..].Trim().Length == 0)
                {
                    fence = null;
                }

                continue;
            }

            if (fence is null && SqlStatement.IsMatch(MarkdownLead.Replace(line, string.Empty, 1)))
            {
                found.Add($"{number}: {line.Trim()}");
            }
        }

        return found;
    }

    [Fact]
    public void EveryRunbookIsInTheParseChecksTable()
    {
        var listed = RunbookSqlParsesSqlServerTests.Runbooks.Select(row => (string)row[0]).ToList();

        RunbooksOnDisk().Should().BeEquivalentTo(
            listed, "a runbook the table does not name is one whose SQL nobody parses");
    }

    [Fact]
    public void NoRunbookHoldsSqlOutsideAFence()
    {
        var offenders = new List<string>();
        foreach (var runbook in RunbooksOnDisk())
        {
            offenders.AddRange(
                SqlOutsideFences(File.ReadLines(Path.Combine(Root, runbook)))
                    .Select(line => $"{runbook}:{line}"));
        }

        offenders.Should().BeEmpty(
            "SQL an operator is told to run belongs in a ```sql fence, where "
            + "RunbookSqlParsesSqlServerTests parses it; outside one, indented or flush left, "
            + "nothing checks it");
    }

    /// <summary>
    /// The control for the scan itself. Measured 2026-09-16 with the pattern this file replaced
    /// swapped back in: it found none of the twenty statements below — lowercase, flush left,
    /// behind a bullet or a quote mark — and a flush-left <c>update Users set PinHash = null where
    /// ...;</c> appended to the PIN runbook left <see cref="NoRunbookHoldsSqlOutsideAFence"/>
    /// green. With the pattern above, that same line fails it by line number. The prose is the
    /// runbooks' own: a looser pattern starts reporting those sentences as SQL.
    /// </summary>
    [Fact]
    public void TheScanReadsLowercaseAndFlushLeftSql_AndLeavesProseAlone()
    {
        string[] sql =
        [
            "update Users set PinHash = null where Id = 1;",
            "select top 10 * from AuditEvents order by Sequence desc",
            "delete from AuditEvents where Id = '00000000-0000-0000-0000-000000000001';",
            "insert into AuditEvents (Id) values (newid());",
            "declare @id uniqueidentifier = newid();",
            "exec sp_who2;",
            "EXECUTE AS USER = 'auditor';",
            "alter table AuditEvents add Sequence bigint;",
            "truncate table Notices;",
            "with recent as (select top 5 * from AuditEvents)",
            "begin transaction;",
            "use master;",
            "kill 53;",
            "set parseonly on;",
            "select 1;",
            "> select 1;",
            "- update Users set PinHash = null where Id = 1;",
            "    update Users set PinHash = null where Id = 1;",
            "merge Accounts using Staging on Accounts.Id = Staging.Id",
            "if exists (select 1 from AuditEvents) print 'yes';",
        ];

        string[] prose =
        [
            // The eleven lines outside fences in the two runbooks that open with one of the verbs,
            // cut at a word to fit, as measured on 2026-09-16.
            "restore sequence**, so no further log backup can ever be applied to it. If this is",
            "DELETE there is a larger problem than the outage you are ending.",
            "If the dev or test database is simply behind on migrations, see the note in",
            "restore of a partial backup. Both are measured below and neither involves an",
            "with the reason on the next line. Where the refusal is about a particular entry it",
            "begin at sequence 1, and that is a second finding rather than a fix.** Re-point the",
            "If the row was minted, raising the boundary completes the attack and the trail then",
            "use on a trail that begins at sequence 1. Boundaries are at least 1 and strictly",
            "with its trigger. Delete",
            "Use the notice's own `Event` in that WHERE clause. Filtering on `PinEnrolled` for a",
            "Delete the pickup directory after the notices in it have been dealt with. It holds",

            // And three that open with verbs the runbooks do not yet start a sentence with.
            "Select the alert in the portal and note its identifier.",
            "Set the environment variable before running the tool.",
            "Create a throwaway user for this, never the seeded one.",
        ];

        SqlOutsideFences(sql).Should().HaveCount(
            sql.Length,
            "each of these is a statement an operator would paste, and the scan is what stands "
            + "between one of them and a runbook nobody parses");

        SqlOutsideFences(prose).Should().BeEmpty(
            "these are sentences out of the runbooks themselves; a scan that reports prose is one "
            + "somebody switches off");

        SqlOutsideFences(["```sql", .. sql, "```"]).Should().BeEmpty(
            "fenced is where this SQL belongs, and the parse check reads it there");

        SqlOutsideFences(["~~~", "```", .. sql, "```", "~~~"]).Should().BeEmpty(
            "a fence ends on its own marker; a ``` inside a ~~~ block does not open a gap");
    }
}
