using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Keeps runbook SQL where the parse check can see it: every runbook is in that check's table, and
/// no runbook holds SQL the check cannot read — outside a fence, or in a fence whose language is
/// not <c>sql</c>. None of these needs SQL Server, so all of them run on every build.
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
/// <para>
/// ⚠️ <b>AND UNTIL 2026-09-17 IT READ ONE LINE AT A TIME, AND TRUSTED EVERY FENCE.</b> Measured
/// that day by appending one probe at a time to the PIN runbook, with both guards run after each:
/// <c>UPDATE Users</c> / <c>SET PinHash = NULL</c> / <c>WHERE Email = …</c> and a lowercase
/// <c>select top 10 *</c> / <c>from AuditEvents</c> / <c>order by …</c> passed, because no single
/// line carried its verb and the clause the verb needs; invalid SQL in a <c>~~~sql</c> fence, a
/// bare fence and a <c>```text</c> fence passed too, because this scan counted any fence as safe
/// while the parse check read only <c>```sql</c>. A statement is now followed across the lines of
/// its paragraph, up to its first sentence end, and SQL counts as parsed only inside a fence <see
/// cref="RunbookMarkdown"/> reads as SQL — the same reader the parse check uses. The same probes
/// after the change: the three wrapped statements and a recognisable statement in a bare fence fail
/// this scan, the <c>```text</c> one fails it too, and the <c>~~~sql</c> one fails the parse check.
/// One residual, stated rather than left to be found: a statement whose verb is itself misspelt
/// (<c>UPDAT Users …</c>) is not recognisable as SQL, so in a fence of another language it still
/// passes; in a fence marked <c>sql</c> the parse check refuses it.
/// </para>
/// <para>
/// ⚠️ <b>AND UNTIL LATER THAT DAY NINE STATEMENTS A DATABASE RUNBOOK MIGHT HOLD WENT UNSEEN.</b>
/// <c>DBCC CHECKDB;</c> matched no branch, and neither did <c>DBCC CHECKIDENT (…)</c>,
/// <c>WAITFOR DELAY</c>, <c>RAISERROR(…)</c>, <c>THROW</c>, <c>PRINT</c>, <c>DENY</c>,
/// <c>CHECKPOINT</c> or <c>RECONFIGURE</c>: appended one at a time to the PIN runbook outside any
/// fence, each of the nine left both guards green. Each verb now has a shape of its own and is in
/// the semicolon branch, and each of the same nine probes fails this scan. The shape is needed even
/// with the semicolon branch: a statement is joined to the lines after it, so one followed by
/// another statement no longer ends in its own semicolon. The control's four-space case showed
/// that, reporting <c>DENY</c>, <c>CHECKPOINT</c> and <c>RECONFIGURE</c> only once they had a shape.
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
            | DBCC\s+\w+
            | WAITFOR\s+(?:DELAY|TIME)\b
            | RAISERROR\s*\(
            | THROW\s+\d
            | PRINT\s+(?:N?'|@)
            | DENY\s+(?:SELECT|INSERT|UPDATE|DELETE|EXEC|EXECUTE|ALTER|CONTROL|REFERENCES|VIEW
               |CONNECT|IMPERSONATE)\b
            | CHECKPOINT\s*(?:;|\d|$)
            | RECONFIGURE\s*(?:;|WITH\s+OVERRIDE\b|$)
            | (?:SELECT|UPDATE|INSERT|DELETE|DECLARE|EXEC|EXECUTE|MERGE|ALTER|CREATE|DROP|TRUNCATE
               |WITH|BEGIN|COMMIT|ROLLBACK|SET|USE|KILL|GRANT|REVOKE|DENY|DBCC|WAITFOR|RAISERROR
               |THROW|PRINT|CHECKPOINT|RECONFIGURE)\b.*;\s*$
          )",
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace
            | RegexOptions.CultureInvariant);

    /// <summary>Blockquote marks, bullets and numbered steps a statement can sit behind.</summary>
    private static readonly Regex MarkdownLead = new(@"^\s*(?:(?:>\s?)+|[-*+]\s+|\d+[.)]\s+)*");

    /// <summary>A line that ends a paragraph for the scan: blank, heading, list item, table
    /// row.</summary>
    private static readonly Regex ParagraphBreak = new(@"^\s*$|^\s*(?:\#|[-*+]\s|\d+[.)]\s|\|)");

    /// <summary>
    /// A <c>```sql</c> or <c>~~~sql</c> marker. Found among the lines outside every fence, it
    /// opened nothing: <see cref="RunbookMarkdown"/> only reads a marker indented three columns or
    /// fewer.
    /// </summary>
    private static readonly Regex SqlFenceMarker = new(
        @"^\s*(?:`{3,}|~{3,})\s*sql\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The blockquote marks on a continuation line, not part of its text.</summary>
    private static readonly Regex QuoteLead = new(@"^\s*(?:>\s?)+");

    /// <summary>A full stop before whitespace or the end: a sentence ends there, not a
    /// statement.</summary>
    private static readonly Regex SentenceEnd = new(@"\.(?:\s|$)");

    /// <summary>How many lines a statement is followed across before the scan stops.</summary>
    private const int StatementLines = 8;

    private static string Root => RunbookSqlParsesSqlServerTests.RepositoryRoot().FullName;

    private static List<string> RunbooksOnDisk() =>
        Directory.EnumerateFiles(Path.Combine(Root, "docs", "runbooks"), "*.md")
            .Select(path => "docs/runbooks/" + Path.GetFileName(path))
            .ToList();

    /// <summary>
    /// The SQL statements in a Markdown document that the parse check never reads — outside every
    /// fence, or inside a fence that is not SQL — as "<c>line number: text</c>", the line being
    /// where the statement opens.
    /// </summary>
    private static List<string> SqlNobodyParses(IEnumerable<string> lines)
    {
        var (fences, outside) = RunbookMarkdown.Read(lines);
        var found = Statements(outside);

        // Reported whatever follows it: a statement too misspelt to recognise would otherwise sit
        // under an indented ```sql that looks like a fence and is parsed by nobody.
        found.AddRange(outside
            .Where(line => SqlFenceMarker.IsMatch(line.Text))
            .Select(line => $"{line.Line}: {line.Text.Trim()} "
                + "(indented four columns or more, it opens no fence)"));
        foreach (var fence in fences.Where(fence => !fence.IsSql))
        {
            found.AddRange(Statements(fence.Body));
        }

        return found;
    }

    /// <summary>
    /// Every line a statement opens on, read together with the following lines of its paragraph, so
    /// <c>UPDATE Users</c> is joined to the <c>SET</c> on the line after it. The joined text stops
    /// at the first sentence end: prose that opens with a verb must not borrow a <c>FROM</c> from a
    /// later sentence.
    /// </summary>
    private static List<string> Statements(IReadOnlyList<(int Line, string Text)> lines)
    {
        var found = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            var (number, text) = lines[i];
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var parts = new List<string> { MarkdownLead.Replace(text, string.Empty, 1).Trim() };
            for (var next = i + 1;
                 next < lines.Count
                 && parts.Count < StatementLines
                 && lines[next].Line == lines[next - 1].Line + 1;
                 next++)
            {
                // A quoted statement goes on quoted: "> UPDATE Users" then "> SET …". The marks are
                // taken off before the line is judged, so "> " on its own still ends the paragraph.
                var continued = QuoteLead.Replace(lines[next].Text, string.Empty, 1);
                if (ParagraphBreak.IsMatch(continued))
                {
                    break;
                }

                parts.Add(continued.Trim());
            }

            var joined = string.Join(" ", parts);
            var end = SentenceEnd.Match(joined);
            if (SqlStatement.IsMatch(end.Success ? joined[..(end.Index + 1)] : joined))
            {
                found.Add($"{number}: {text.Trim()}");
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
    public void NoRunbookHoldsSqlOutsideASqlFence()
    {
        var offenders = new List<string>();
        foreach (var runbook in RunbooksOnDisk())
        {
            offenders.AddRange(
                SqlNobodyParses(File.ReadLines(Path.Combine(Root, runbook)))
                    .Select(line => $"{runbook}:{line}"));
        }

        offenders.Should().BeEmpty(
            "SQL an operator is told to run belongs in a fence marked sql, where "
            + "RunbookSqlParsesSqlServerTests parses it; outside one, or in a fence of another "
            + "language, nothing checks it");
    }

    /// <summary>
    /// The control for the scan itself. Measured 2026-09-16 with the pattern this file replaced
    /// swapped back in: it found none of the first twenty statements below — lowercase, flush left,
    /// behind a bullet or a quote mark — and a flush-left <c>update Users set PinHash = null where
    /// ...;</c> appended to the PIN runbook left <see cref="NoRunbookHoldsSqlOutsideASqlFence"/>
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
            "dbcc checkdb;",
            "DBCC CHECKIDENT ('Users', NORESEED)",
            "waitfor delay '00:00:05';",
            "raiserror('stop here', 16, 1)",
            "throw 50000, 'stop here', 1;",
            "print 'done'",
            "deny select on Users to public;",
            "checkpoint;",
            "reconfigure;",
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

            // And six that open with the verbs added on 2026-09-17, the way prose would.
            "Print the verification output and attach it to the incident.",
            "Deny the request if the user cannot be reached by a second channel.",
            "Deny access to the export share until the investigation is closed.",
            "Checkpoint the investigation notes before handing over the shift.",
            "Throw away any export taken before the boundary was raised.",
            "Wait for the second approver before running the UPDATE below.",
        ];

        SqlNobodyParses(sql).Should().HaveCount(
            sql.Length,
            "each of these is a statement an operator would paste, and the scan is what stands "
            + "between one of them and a runbook nobody parses");

        SqlNobodyParses(prose).Should().BeEmpty(
            "these are sentences out of the runbooks themselves; a scan that reports prose is one "
            + "somebody switches off");

        SqlNobodyParses(["```sql", .. sql, "```"]).Should().BeEmpty(
            "fenced as sql is where this SQL belongs, and the parse check reads it there");

        SqlNobodyParses(["~~~sql", .. sql, "~~~"]).Should().BeEmpty(
            "a tilde fence marked sql is read by the parse check too");

        SqlNobodyParses(["```", .. sql, "```"]).Should().HaveCount(
            sql.Length, "a fence with no language is not one the parse check reads");

        SqlNobodyParses(["```text", .. sql, "```"]).Should().HaveCount(
            sql.Length, "nor is a fence marked with another language");

        SqlNobodyParses(["   ```sql", .. sql, "   ```"]).Should().BeEmpty(
            "a marker indented three spaces still opens and closes a fence");

        SqlNobodyParses(["    ```sql", .. sql, "    ```"]).Should().HaveCount(
            sql.Length + 1,
            "four spaces make the marker text in an indented code block, not a fence, so the SQL "
            + "after it is not in a sql fence and the parse check never reads it — and the marker "
            + "itself is reported, so a statement too misspelt to recognise cannot hide under it");

        SqlNobodyParses(["    ```sql", "    UPDAT Users SET PinHash = NULL;", "    ```"])
            .Should().ContainSingle(
                "the indented marker is reported even when nothing after it reads as SQL");

        SqlNobodyParses(["```sql", "SELECT 1;", "    ```", .. sql, "```"]).Should().BeEmpty(
            "a four-space ``` inside a sql fence is part of its body, not its close: everything up "
            + "to the real close is sql, and is parsed");

        SqlNobodyParses(["~~~text", "```sql", .. sql, "```", "~~~"]).Should().HaveCount(
            sql.Length,
            "a fence ends only on its own marker, so a ```sql line inside a ~~~text block opens "
            + "nothing, and the SQL after it is still not parsed");
    }

    /// <summary>
    /// A statement wrapped across lines, with the clause its verb needs on a later one, as the
    /// review on 2026-09-16 put it — and the prose next to it, which must not be joined into SQL.
    /// </summary>
    [Fact]
    public void TheScanFollowsAStatementAcrossLines_ButNotAcrossSentences()
    {
        SqlNobodyParses(
                ["UPDATE Users", "SET PinHash = NULL", "WHERE Email = N'someone@example.com'"])
            .Should().ContainSingle(
                "the UPDATE's SET is on the next line, and the statement starts on the first");

        SqlNobodyParses(["select top 10 *", "from AuditEvents", "order by Sequence desc"])
            .Should().ContainSingle("the SELECT's FROM is on the next line");

        SqlNobodyParses(
                [
                    "Select the alert in the portal and note its identifier. Then pick one",
                    "from the list.",
                ])
            .Should().BeEmpty(
                "the FROM belongs to a later sentence, not to the verb that opens the first");

        SqlNobodyParses(["UPDATE Users", "", "SET PinHash = NULL"])
            .Should().BeEmpty(
                "a blank line ends the paragraph, and neither half is a statement on its own");

        SqlNobodyParses(
                [
                    "> UPDATE Users",
                    "> SET PinHash = NULL",
                    "> WHERE Email = N'someone@example.com'",
                ])
            .Should().ContainSingle(
                "a quoted statement continues on quoted lines; the marks are not part of the SQL");

        SqlNobodyParses(["> UPDATE Users", ">", "> SET PinHash = NULL"])
            .Should().BeEmpty("a quote line with nothing after its mark ends the paragraph");

        SqlNobodyParses(
                [
                    "> Select the alert in the portal and note its identifier. Then pick one",
                    "> from the list.",
                ])
            .Should().BeEmpty("a quoted sentence is still a sentence, and ends where it ends");
    }

    /// <summary>
    /// The control for the parse check's PARSEONLY refusal, which needs no SQL Server to test.
    /// Every spelling here turns the option off on a real server or names it where it could; the
    /// first version of the refusal, a pattern for SET, whitespace, PARSEONLY, missed the comment
    /// forms.
    /// </summary>
    [Fact]
    public void TheParseCheckRefusesEverySpellingOfParseOnly()
    {
        string[] refused =
        [
            "SET PARSEONLY OFF;",
            "set parseonly off;",
            "SET/* the comment a keyword pattern did not expect */PARSEONLY OFF;",
            "SET -- a line comment\nPARSEONLY OFF;",
            "SET /* a /* nested */ comment */ PARSEONLY OFF;",
            "EXEC (N'SET PARSEONLY OFF');",
        ];

        refused.Should().OnlyContain(
            block => RunbookSqlParsesSqlServerTests.TouchesParseOnly(block),
            "any of these would leave the parse check executing what it claims only to parse");

        RunbookSqlParsesSqlServerTests.TouchesParseOnly("SELECT TOP 1 Sequence FROM AuditEvents;")
            .Should().BeFalse("a block that does not name the option is sent to be parsed");
    }
}
