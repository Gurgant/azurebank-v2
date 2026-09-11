using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Keeps runbook SQL where the parse check can see it: every runbook is in that check's table, and
/// no runbook holds SQL outside a fence. Neither needs SQL Server, so both run on every build.
/// </summary>
/// <remarks>
/// The PIN runbook's six SQL blocks, two of them hand-run UPDATEs, were Markdown INDENTED code until
/// 2026-09-11. They render exactly like fenced code, and a check that looks for <c>```sql</c> fences
/// never sees them — which is how they went unparsed while
/// <see cref="RunbookSqlParsesSqlServerTests"/> stayed green.
/// </remarks>
public sealed class RunbookSqlIsFencedTests
{
    /// <summary>A line indented as Markdown code that opens like a SQL statement.</summary>
    private static readonly Regex IndentedSql = new(
        @"^(?: {4,}|\t)\s*(?:SELECT|UPDATE|INSERT|DELETE|DECLARE|WITH|MERGE|EXEC|EXECUTE|ALTER"
        + @"|CREATE|DROP|TRUNCATE|BEGIN|SET|USE|IF)\b");

    private static string Root => RunbookSqlParsesSqlServerTests.RepositoryRoot().FullName;

    private static List<string> RunbooksOnDisk() =>
        Directory.EnumerateFiles(Path.Combine(Root, "docs", "runbooks"), "*.md")
            .Select(path => "docs/runbooks/" + Path.GetFileName(path))
            .ToList();

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
            var inFence = false;
            var number = 0;
            foreach (var line in File.ReadLines(Path.Combine(Root, runbook)))
            {
                number++;
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    inFence = !inFence;
                    continue;
                }

                if (!inFence && IndentedSql.IsMatch(line))
                {
                    offenders.Add($"{runbook}:{number}: {line.Trim()}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "SQL an operator is told to run belongs in a ```sql fence, where "
            + "RunbookSqlParsesSqlServerTests parses it; indented, it renders the same and nothing "
            + "checks it");
    }
}
