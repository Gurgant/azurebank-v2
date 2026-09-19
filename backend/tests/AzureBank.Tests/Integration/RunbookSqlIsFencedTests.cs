using System.Text;
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
/// <b>What a statement looks like is <see cref="RunbookSqlGrammar"/>'s business; the reading is this
/// class's.</b> A bare verb is usually prose. Measured over the two runbooks on 2026-09-16: eleven
/// lines outside fences open with a T-SQL verb — <i>"Delete the pickup directory after the notices
/// in it have been dealt with"</i>, <i>"Use the notice's own `Event` in that WHERE clause"</i>,
/// <i>"If the dev or test database is simply behind on migrations"</i> — and none of them is SQL.
/// <see cref="TheScanReadsLowercaseAndFlushLeftSql_AndLeavesProseAlone"/> holds those lines beside
/// the statements they resemble, and <see cref="TheScanReportsEveryCorpusStatement_AndNoneOfItsProse"/>
/// holds the measured corpus, so a change to the grammar that blinds it, or that starts reporting
/// prose, fails here rather than in a runbook nobody parsed.
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
/// ⚠️ <b>AND UNTIL 2026-09-18 THE PATTERN WAS A LIST OF VERBS THAT GREW ONE REVIEW AT A TIME.</b>
/// Review found <c>DBCC CHECKDB;</c> unseen on 2026-09-17, and appended one at a time to the PIN
/// runbook outside any fence so were <c>WAITFOR DELAY</c>, <c>RAISERROR(…)</c>, <c>THROW</c>,
/// <c>PRINT</c>, <c>DENY</c>, <c>CHECKPOINT</c> and <c>RECONFIGURE</c>: each left both guards green.
/// They got a branch each, and the next review found an unterminated <c>DENY TAKE OWNERSHIP</c>.
/// Rather than add a tenth verb the pattern was measured, against 3,017 statements SQL Server's
/// parser accepts and 377 sentences a runbook could hold: it missed 822 of the statements and
/// reported 133 of the sentences. It is <see cref="RunbookSqlGrammar"/> now, which on the same
/// corpus reports 2,860 of the statements and 40 of the sentences, and the corpus publishes both
/// remainders (<c>notReported</c>, <c>reportedProse</c>) instead of leaving them to be found. The
/// reading learned three things in the same pass. A fence marker left outside every fence ends a
/// paragraph, because <c>reconfigure;</c> above a four-space <c>```</c> was joined to it and
/// stopped looking like a statement that had ended. T-SQL comments are taken out before the
/// sentence end is looked for, because <c>-- the columns we need.</c> ended the "sentence" before
/// the <c>FROM</c> under it. And a statement is followed for 40 lines, not 8, which is what an
/// SSMS-scripted column list needs.
/// </para>
/// <para>
/// ⚠️ <b>AND UNTIL 2026-09-19 THE READING DID NOT KNOW A STRING LITERAL.</b> Taking comments out by
/// pattern, one round old, cut <c>SELECT N'-- keep';</c> at the dashes, and the sentence end,
/// four rounds old, cut <c>SELECT N'stop. now';</c> at the full stop; appended to the PIN runbook
/// outside any fence, each left both guards green. <see cref="ReadAsSql"/> reads the paragraph
/// one character at a time now, and both fail the scan. Over the same 3,648 Markdown files the
/// lines it reports are the same ones as before, because an apostrophe after a letter opens
/// nothing. The same day a line opening with <c>#</c> stopped ending every paragraph:
/// <c>#Temp</c> is a temporary table, and a heading is one to six <c>#</c> followed by whitespace.
/// <see cref="RunbookSqlGrammar"/> had carried a branch for <c>SELECT * INTO</c> above a
/// <c>#pin_before</c> line, "cut short because the scan reads its next line as a heading"; with the
/// heading read properly that branch changed none of the corpus's entries, and it is gone.
/// </para>
/// </remarks>
public sealed class RunbookSqlIsFencedTests
{
    /// <summary>Blockquote marks, bullets and numbered steps a statement can sit behind.</summary>
    private static readonly Regex MarkdownLead = new(@"^\s*(?:(?:>\s?)+|[-*+]\s+|\d+[.)]\s+)*");

    /// <summary>A line that ends a paragraph for the scan: blank, heading, list item, table row,
    /// or a fence marker. A marker left outside every fence is one indented four columns or more,
    /// and it is no part of the statement above it: <c>reconfigure;</c> followed by such a
    /// <c>```</c> was joined to it and no longer ran to a statement boundary. A heading is one to
    /// six <c>#</c> and then whitespace or the end of the line, as CommonMark has it: until
    /// 2026-09-19 any line opening with <c>#</c> ended the paragraph, so <c>DELETE</c> /
    /// <c>#Temp</c> / <c>WHERE Id = 1;</c> was cut before its temporary table and no line held the
    /// whole statement.</summary>
    private static readonly Regex ParagraphBreak = new(
        @"^\s*$|^\s*(?:\#{1,6}(?:\s|$)|[-*+]\s|\d+[.)]\s|\||`{3,}|~{3,})");

    /// <summary>
    /// A <c>```sql</c> or <c>~~~sql</c> marker. Found among the lines outside every fence, it
    /// opened nothing: <see cref="RunbookMarkdown"/> only reads a marker indented three columns or
    /// fewer, and a backtick marker only when no backtick follows it.
    /// </summary>
    private static readonly Regex SqlFenceMarker = new(
        @"^\s*(?:`{3,}|~{3,})\s*sql\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The blockquote marks on a continuation line, not part of its text.</summary>
    private static readonly Regex QuoteLead = new(@"^\s*(?:>\s?)+");

    /// <summary>
    /// How many lines a statement is followed across before the scan stops. It was 8, and a
    /// <c>SELECT</c> scripted by SSMS with one column to a line and nine columns never reached its
    /// <c>FROM</c>. Measured at 8 and at 40 over 507,700 lines of prose in 3,648 Markdown files:
    /// the same lines reported, not one more, because a paragraph of prose stops at its first
    /// full stop long before it stops at a line count.
    /// </summary>
    private const int StatementLines = 40;

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
                + "(it opens no fence: indented four columns or more, or a backtick in its info string)"));
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
    /// later sentence. <see cref="ReadAsSql"/> does the joining, so a full stop inside a comment or
    /// a string literal ends nothing.
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

            var (joined, openingLength, sentenceEnd) = ReadAsSql(parts);
            if (openingLength == 0)
            {
                // A line that is only a comment opens nothing; the statement under it is judged
                // on its own line.
                continue;
            }

            var read = (sentenceEnd >= 0 ? joined[..(sentenceEnd + 1)] : joined).TrimEnd();
            if (RunbookSqlGrammar.Statement.IsMatch(read))
            {
                found.Add($"{number}: {text.Trim()}");
            }
        }

        return found;
    }

    /// <summary>
    /// A paragraph's lines joined the way T-SQL reads them: string literals kept whole, comments
    /// dropped, and the first sentence end that is OUTSIDE a literal — a full stop before
    /// whitespace or the end of a line.
    /// </summary>
    /// <remarks>
    /// Until 2026-09-19 this was three regular expressions, and review found what they could not
    /// know: <c>SELECT N'-- keep';</c> lost everything after the dashes, and <c>SELECT N'stop.
    /// now';</c> ended at the full stop, so both went unreported. Three statements already sat in
    /// the corpus's <c>notReported</c> list for the same reason (<c>SELECT 'Dr. Who' AS n …</c>).
    /// A quote opens a literal only where T-SQL could put one — not after a letter or a digit,
    /// unless that letter is the <c>N</c> of <c>N'…'</c> — so the apostrophe in <i>"the user's
    /// row"</i> opens nothing, and prose still ends at its full stop.
    /// </remarks>
    private static (string Text, int OpeningLength, int SentenceEnd) ReadAsSql(IReadOnlyList<string> lines)
    {
        var text = new StringBuilder();
        var (openingLength, sentenceEnd, inLiteral, commentDepth) = (0, -1, false, 0);

        for (var number = 0; number < lines.Count; number++)
        {
            var line = lines[number];
            if (number > 0 && text.Length > 0 && text[^1] != ' ')
            {
                text.Append(' ');
            }

            for (var i = 0; i < line.Length; i++)
            {
                var (here, next) = (line[i], i + 1 < line.Length ? line[i + 1] : '\0');
                if (commentDepth > 0)
                {
                    // T-SQL block comments nest.
                    if (here == '*' && next == '/')
                    {
                        i++;
                        if (--commentDepth == 0)
                        {
                            text.Append(' ');
                        }
                    }
                    else if (here == '/' && next == '*')
                    {
                        commentDepth++;
                        i++;
                    }

                    continue;
                }

                if (inLiteral)
                {
                    // '' inside a literal closes it and opens it again, which comes to the same.
                    text.Append(here);
                    inLiteral = here != '\'';
                    continue;
                }

                if (here == '-' && next == '-')
                {
                    break;
                }

                if (here == '/' && next == '*')
                {
                    commentDepth = 1;
                    i++;
                    continue;
                }

                if (here == '\'')
                {
                    inLiteral = OpensLiteral(text);
                }
                else if (here == '.' && sentenceEnd < 0 && (next == '\0' || char.IsWhiteSpace(next)))
                {
                    sentenceEnd = text.Length;
                }

                text.Append(here);
            }

            if (number == 0)
            {
                openingLength = text.ToString().Trim().Length;
            }
        }

        return (text.ToString(), openingLength, sentenceEnd);
    }

    /// <summary>True when a quote at the end of <paramref name="text"/> could open a T-SQL
    /// literal: at the start, after anything that is not part of a word, or after the <c>N</c> of
    /// a Unicode literal.</summary>
    private static bool OpensLiteral(StringBuilder text)
    {
        static bool InWord(char character) => char.IsLetterOrDigit(character) || character == '_';

        if (text.Length == 0 || !InWord(text[^1]))
        {
            return true;
        }

        return text[^1] is 'N' or 'n' && (text.Length == 1 || !InWord(text[^2]));
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
            + "language, nothing checks it. A reported line that is prose is prose that is also "
            + "valid T-SQL (\"Open SSMS\" is OPEN cursor): end the sentence with a full stop");
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
            "DENY TAKE OWNERSHIP ON dbo.Users TO auditor",
            "checkpoint;",
            "reconfigure;",

            // A comment marker and a full stop inside a string literal, as review put them on
            // 2026-09-18: read by pattern, the first was cut at its dashes and the second at its stop.
            "SELECT N'-- keep';",
            "SELECT N'stop. now';",
            "update Users set Note = N'see sec. 4 -- later' where Id = 1;",
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

            // An apostrophe after a letter opens no literal, so this still ends at its first full
            // stop. Read as one, it runs on to "from Staging where" and is reported.
            "Select users' rows. Then copy them from Staging where needed.",
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

        SqlNobodyParses(["```sql `pin`", .. sql, "```"]).Should().HaveCount(
            sql.Length + 1,
            "a backtick fence's info string holds no backtick, so this line opens nothing and "
            + "Markdown renders the SQL under it as a paragraph: it is outside every fence, and the "
            + "marker is reported with it");

        SqlNobodyParses(["```sql`", .. sql, "```"]).Should().HaveCount(
            sql.Length + 1, "the same with the backtick hard against the language");

        SqlNobodyParses(["~~~sql `pin`", .. sql, "~~~"]).Should().BeEmpty(
            "a tilde fence may hold backticks in its info string, and this one is sql");

        SqlNobodyParses(["```sql", "SELECT 1;", "    ```", .. sql, "```"]).Should().BeEmpty(
            "a four-space ``` inside a sql fence is part of its body, not its close: everything up "
            + "to the real close is sql, and is parsed");

        SqlNobodyParses(["~~~text", "```sql", .. sql, "```", "~~~"]).Should().HaveCount(
            sql.Length,
            "a fence ends only on its own marker, so a ```sql line inside a ~~~text block opens "
            + "nothing, and the SQL after it is still not parsed");
    }

    /// <summary>
    /// The measured corpus: every statement in it is reported when it stands alone and when another
    /// statement follows it unterminated, and none of its prose is. The statements are ones SQL
    /// Server's parser accepts (<see cref="RunbookSqlCorpusSqlServerTests"/> asks it again), so a
    /// miss here is T-SQL the scan cannot see, not a disagreement about what T-SQL is.
    /// </summary>
    [Fact]
    public void TheScanReportsEveryCorpusStatement_AndNoneOfItsProse()
    {
        var corpus = RunbookSqlCorpus.Load();
        var statements = corpus.AllStatements.ToList();

        statements.Where(statement => !ReportsItsFirstLine(statement.Split('\n')))
            .Should().BeEmpty(
                "each of these parses on SQL Server, and standing alone outside a fence it is "
                + "exactly what this scan exists to report");

        // The last statement of an unfenced paragraph always stands alone, so the case above is
        // what keeps a paragraph from passing. This one keeps the line number honest too: joined
        // to a follower, a statement no longer ends where its text does.
        statements.Where(statement => !ReportsItsFirstLine([.. statement.Split('\n'), "PRINT 'next'"]))
            .Should().BeEmpty("a statement is still one when another follows it with no semicolon");

        corpus.Prose.Where(sentence => SqlNobodyParses(sentence.Split('\n')).Count > 0)
            .Should().BeEmpty(
                "these are sentences a runbook could hold, many opening with a T-SQL verb; a scan "
                + "that reports prose is one somebody switches off");

        corpus.KnownMisses.Where(known => ReportsItsFirstLine(known.Text.Split('\n')))
            .Select(known => $"{known.Text.ReplaceLineEndings(" / ")} ({known.Why})")
            .Should().BeEmpty(
                "RunbookSqlGrammar's remarks say the scan does not see these; one it reports now "
                + "means the remark is out of date, so move it into the statements");
    }

    /// <summary>
    /// The two remainders <see cref="RunbookSqlGrammar"/>'s remarks count: valid statements the
    /// scan does not report, and prose it does. Pinned in this direction too, so the published
    /// lists stay the truth — a grammar change that reaches one of these fails here and the entry
    /// moves to the list it now belongs in.
    /// </summary>
    [Fact]
    public void TheCorpusPublishesWhatTheScanDoesNotReach_AndThePublishedListsAreTrue()
    {
        var corpus = RunbookSqlCorpus.Load();

        corpus.NotReported
            .Where(statement =>
                ReportsItsFirstLine(statement.Split('\n'))
                && ReportsItsFirstLine([.. statement.Split('\n'), "PRINT 'next'"]))
            .Should().BeEmpty(
                "the scan reports these now, alone and followed, so they belong in statements and "
                + "the count in RunbookSqlGrammar's remarks comes down");

        corpus.ReportedProse.Where(sentence => SqlNobodyParses(sentence.Split('\n')).Count == 0)
            .Should().BeEmpty(
                "the scan leaves these alone now, so they belong in prose and the count in "
                + "RunbookSqlGrammar's remarks comes down");

        // The remarks quote these four numbers. A corpus edit that moves one fails here, next to
        // the sentence that has to change with it.
        (corpus.AllStatements.Count() + corpus.NotReported.Length, corpus.NotReported.Length)
            .Should().Be((3017, 157), "RunbookSqlGrammar's remarks say so");
        (corpus.Prose.Length + corpus.ReportedProse.Length, corpus.ReportedProse.Length)
            .Should().Be((377, 40), "RunbookSqlGrammar's remarks say so");
    }

    private static bool ReportsItsFirstLine(IEnumerable<string> lines) =>
        SqlNobodyParses(lines).Any(found => found.StartsWith("1: ", StringComparison.Ordinal));

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

        SqlNobodyParses(["DELETE", "#Temp", "WHERE Id = 1;"]).Should().ContainSingle(
            "#Temp is a temporary table, not a heading: a heading's # is followed by whitespace");

        SqlNobodyParses(["UPDATE", "##pin_before", "SET PinHash = NULL", "WHERE Id = 1"])
            .Should().ContainSingle("nor is a global temporary table one");

        SqlNobodyParses(["UPDATE Users", "# Rollback", "SET PinHash = NULL"])
            .Should().BeEmpty("a heading still ends the paragraph");

        SqlNobodyParses(["UPDATE Users", "#", "SET PinHash = NULL"])
            .Should().BeEmpty("and so does a # with nothing after it, which is an empty heading");

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
