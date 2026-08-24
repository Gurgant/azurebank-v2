using System.Reflection;
using System.Text.RegularExpressions;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Every SQL block the audit-chain runbook prints must be valid SQL.
/// </summary>
/// <remarks>
/// <para>
/// A runbook is code, and this one has now had three commands broken by an edit that never ran
/// them. The last was a rewrap for line length that split a trailing <c>--</c> comment across two
/// lines, leaving a bare string literal: SQL Server rejects the whole batch with
/// <c>Msg 102, Incorrect syntax near 'unreadable'</c> before it reaches the query the operator
/// wanted. Two sibling commands from the same rewrap were caught only because somebody happened to
/// execute them.
/// </para>
/// <para>
/// <b>PARSEONLY, not execution.</b> The point is to check the SQL is well-formed, not to run
/// diagnostics against a database: <c>SET PARSEONLY ON</c> makes SQL Server parse the batch and
/// return without executing anything, which is exact rather than heuristic and cannot touch data.
/// Blocks carrying a placeholder an operator must fill in are skipped by name, and the count of what
/// was checked is asserted so a regex that silently matched nothing cannot pass as agreement.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class RunbookSqlParsesSqlServerTests
{
    private const string Runbook = "docs/runbooks/audit-chain-unavailable.md";

    private readonly ITestOutputHelper _output;

    public RunbookSqlParsesSqlServerTests(ITestOutputHelper output) => _output = output;

    [SqlServerFact]
    public async Task EverySqlBlockInTheRunbookIsValidSql()
    {
        var path = Path.Combine(RepositoryRoot().FullName, Runbook);
        File.Exists(path).Should().BeTrue($"the guard needs {Runbook}; one that cannot read it must fail loudly");

        var blocks = Regex.Matches(await File.ReadAllTextAsync(path), "```sql\r?\n(.*?)```", RegexOptions.Singleline)
            .Select(match => match.Groups[1].Value)
            .ToList();

        blocks.Should().HaveCountGreaterThan(
            3, "the runbook's triage is mostly SQL; a regex that matched nothing would otherwise pass");

        await using var connection = new SqlConnection(SqlServerFactAttribute.ConnectionString!);
        await connection.OpenAsync();

        var checkedBlocks = 0;
        foreach (var block in blocks)
        {
            // An operator substitutes these before running; parsing them as written is not the test.
            if (block.Contains("<session_id>", StringComparison.Ordinal))
            {
                continue;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = "SET PARSEONLY ON; " + block + "; SET PARSEONLY OFF;";

            var parse = async () => await command.ExecuteNonQueryAsync();

            await parse.Should().NotThrowAsync(
                $"this block is printed for an operator to paste:\n{block.Trim()}");

            checkedBlocks++;
        }

        _output.WriteLine($"{checkedBlocks} of {blocks.Count} SQL blocks parsed; the rest carry placeholders");
        checkedBlocks.Should().BeGreaterThan(0, "skipping every block would make this guard vacuous");
    }

    /// <summary>Walks up from the test assembly to the repository root.</summary>
    private static DirectoryInfo RepositoryRoot()
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
