using System.Reflection;
using System.Text.RegularExpressions;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Every SQL block every runbook prints must be valid SQL.
/// </summary>
/// <remarks>
/// <para>
/// A runbook is code, and the audit-chain one had three commands broken by an edit that never ran
/// them. The last was a rewrap for line length that split a trailing <c>--</c> comment across two
/// lines, leaving a bare string literal: SQL Server rejects the whole batch with
/// <c>Msg 102, Incorrect syntax near 'unreadable'</c> before it reaches the query the operator
/// wanted. Two sibling commands from the same rewrap were caught only because somebody happened to
/// execute them.
/// </para>
/// <para>
/// <b>PARSEONLY, in a batch of its own.</b> The point is to check the SQL is well-formed, not to
/// run it: with <c>SET PARSEONLY ON</c> SQL Server parses what follows and executes none of it.
/// Blocks carrying a placeholder an operator must fill in are skipped by name, and the count of what
/// was checked is asserted so a regex that silently matched nothing cannot pass as agreement.
/// </para>
/// <para>
/// ⚠️ <b>UNTIL 2026-09-11 THIS EXECUTED THE SQL IT CLAIMED ONLY TO PARSE.</b> It sent
/// <c>SET PARSEONLY ON; &lt;block&gt;; SET PARSEONLY OFF;</c> as ONE batch, and SQL Server applies
/// both settings while PARSING the batch, so the option was off again by the time it ran. Measured
/// with sqlcmd that day: in such a batch a PRINT printed, a THROW threw and a SELECT returned its
/// rows; with the ON in a batch of its own, a block holding <c>SELECT 1/0</c>, a THROW and a SELECT
/// from a table that does not exist passed here, and a misspelt UPDATE still failed. So the audit
/// runbook's diagnostics had been running against the test database. They are read-only, and its
/// two blocks that are not, KILL and EXECUTE AS, were exempt by placeholder; but adding the PIN
/// runbook to that wrapper would have run its two UPDATEs.
/// </para>
/// <para>
/// <b>EVERY RUNBOOK, from a table.</b> Until 2026-09-11 this read only the audit-chain runbook, and
/// the PIN runbook's six blocks — two of them hand-run UPDATEs — were indented rather than fenced,
/// so no regex over fences could have found them either. <see cref="RunbookSqlIsFencedTests"/>
/// fails, with no SQL Server, when a runbook is missing from <see cref="Runbooks"/> or holds SQL
/// outside a fence.
/// </para>
/// <para>
/// <b>AGAINST <c>master</c>, not the configured database.</b> PARSEONLY needs no table, but
/// opening a connection needs its database to exist, and on a fresh server nothing creates it
/// until a proof that migrates runs first. Measured 2026-09-11 with the variable naming a database
/// that did not exist: <c>Cannot open database "AzureBankNoSuchDb_item10" requested by the
/// login</c>. CI's SQL job starts from an empty container, so this passed there only when the
/// order happened to suit it.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class RunbookSqlParsesSqlServerTests
{
    /// <summary>
    /// Every runbook, the fewest SQL blocks it can hold without a regex having gone blind, and the
    /// placeholder tokens that exempt a block because an operator substitutes them before running.
    /// </summary>
    public static TheoryData<string, int, string[]> Runbooks => new()
    {
        { "docs/runbooks/audit-chain-unavailable.md", 4, ["<session_id>"] },
        // Its placeholders sit inside string literals ('<UserId>'): every block parses as written.
        { "docs/runbooks/pin-enrolment-repudiated.md", 6, [] },
    };

    private readonly ITestOutputHelper _output;

    public RunbookSqlParsesSqlServerTests(ITestOutputHelper output) => _output = output;

    [SqlServerTheory]
    [MemberData(nameof(Runbooks))]
    public async Task EverySqlBlockInTheRunbookIsValidSql(
        string runbook, int minimumBlocks, string[] placeholders)
    {
        var path = Path.Combine(RepositoryRoot().FullName, runbook);
        File.Exists(path).Should().BeTrue($"the guard needs {runbook}; one that cannot read it must fail loudly");

        var blocks = Regex.Matches(await File.ReadAllTextAsync(path), "```sql\r?\n(.*?)```", RegexOptions.Singleline)
            .Select(match => match.Groups[1].Value)
            .ToList();

        blocks.Should().HaveCountGreaterThanOrEqualTo(
            minimumBlocks,
            $"{runbook} holds that many; a regex that matched nothing would otherwise pass");

        // Unpooled, because the session is left in PARSEONLY: a pooled connection would carry the
        // option into whichever test drew it next, and that test's statements would do nothing.
        var master = new SqlConnectionStringBuilder(SqlServerFactAttribute.ConnectionString!)
        {
            InitialCatalog = "master",
            Pooling = false,
        };
        await using var connection = new SqlConnection(master.ConnectionString);
        await connection.OpenAsync();

        await using (var parseOnly = connection.CreateCommand())
        {
            parseOnly.CommandText = "SET PARSEONLY ON;";
            await parseOnly.ExecuteNonQueryAsync();
        }

        /*
          PLACEHOLDERS ARE LISTED, NOT PATTERN-MATCHED, so a block cannot excuse itself from this
          guard by accident -- `<` is also a comparison operator, and a regex over angle brackets
          would quietly skip real SQL. Every token here is asserted to still be in use below, so a
          block that loses its placeholder starts being parsed again instead of staying exempt.

          <database_user> USED TO BE LISTED, for a reason that was the same mistake as the batch
          above: "SET PARSEONLY ON does NOT stop EXECUTE AS USER from running", measured as Msg
          15517. It was the whole batch that ran, not EXECUTE AS alone. Parsed for real, the
          impersonation recipe in step 3b passes with its placeholder quoted, so it is checked now.
        */
        var skipped = new List<string>();

        var checkedBlocks = 0;
        foreach (var block in blocks)
        {
            // An operator substitutes these before running; parsing them as written is not the test.
            var placeholder = placeholders.FirstOrDefault(
                token => block.Contains(token, StringComparison.Ordinal));
            if (placeholder is not null)
            {
                skipped.Add(placeholder);
                continue;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = block;

            var parse = async () => await command.ExecuteNonQueryAsync();

            await parse.Should().NotThrowAsync(
                $"this block is printed for an operator to paste:\n{block.Trim()}");

            checkedBlocks++;
        }

        _output.WriteLine(
            $"{runbook}: {checkedBlocks} of {blocks.Count} SQL blocks parsed; "
            + "the rest carry placeholders");
        checkedBlocks.Should().BeGreaterThanOrEqualTo(
            blocks.Count - placeholders.Length,
            "at most one block may be exempt per placeholder token; more than that means blocks are "
            + "excusing themselves from the guard, which is how an unparseable command reaches an "
            + "operator");
        placeholders.Should().OnlyContain(
            token => skipped.Contains(token),
            "a token nobody uses any more is a hole left open -- take it out of the list when the "
            + "block that needed it goes");
    }

    /// <summary>Walks up from the test assembly to the repository root.</summary>
    internal static DirectoryInfo RepositoryRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs", "runbooks")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull(because: "a guard that cannot find the repository must fail loudly");
        return dir!;
    }
}
