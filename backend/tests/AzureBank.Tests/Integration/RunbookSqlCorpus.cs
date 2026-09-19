using System.Text.Json;
using FluentAssertions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// <c>RunbookSqlCorpus.json</c>: the statements the out-of-fence scan reports, the ones it does
/// not, and the prose it must leave alone, measured rather than imagined.
/// </summary>
/// <remarks>
/// Every statement was accepted by SQL Server's parser when it was collected, and
/// <see cref="RunbookSqlCorpusSqlServerTests"/> asks the server under test again, so the file cannot
/// drift into holding text that is not T-SQL. <see cref="Statements2025"/> are the ones CI's SQL
/// Server 2022 (16.0.4295) refused and SQL Server 2025 (17.0.4025) parses — <c>CREATE EXTERNAL
/// MODEL</c>, <c>CREATE JSON INDEX</c>, <c>SET OPTIMIZED_LOCKING</c>, two permissions 2025 added —
/// and a server older than 2025 is not asked about them. A statement CI refused for its PLATFORM
/// (<c>CODEPAGE</c> "is not supported on the 'Linux' platform") is not in the corpus at all: whether
/// it parses depends on where the test runs. <see cref="NotReported"/> is the published list of valid statements the
/// grammar does not reach, and <see cref="ReportedProse"/> the sentences it reports because they
/// are also the opening of valid T-SQL; both are pinned, so the numbers
/// <see cref="RunbookSqlGrammar"/>'s remarks quote cannot go stale quietly. A finding that the scan
/// misses a statement starts here: if it is in <see cref="NotReported"/> it is known; if it is not,
/// add it to <see cref="Statements"/>, watch <see cref="RunbookSqlIsFencedTests"/> fail, then
/// change the grammar.
/// </remarks>
internal sealed record RunbookSqlCorpus(
    string[] Statements,
    string[] Statements2025,
    string[] NotReported,
    string[] Prose,
    string[] ReportedProse,
    RunbookSqlCorpus.KnownMiss[] KnownMisses)
{
    /// <summary>A statement the READING hides from the grammar, and the reason
    /// <see cref="RunbookSqlGrammar"/>'s remarks give for it.</summary>
    internal sealed record KnownMiss(string Text, string Why);

    /// <summary>Every statement the scan reports, whichever server version it needs: the scan
    /// needs no server.</summary>
    internal IEnumerable<string> AllStatements => Statements.Concat(Statements2025);

    internal static RunbookSqlCorpus Load()
    {
        var path = Path.Combine(
            RunbookSqlParsesSqlServerTests.RepositoryRoot().FullName,
            "backend", "tests", "AzureBank.Tests", "Integration", "RunbookSqlCorpus.json");
        File.Exists(path).Should().BeTrue("a corpus that cannot be read must fail loudly, not pass empty");

        var corpus = JsonSerializer.Deserialize<RunbookSqlCorpus>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        // A file that lost its statements would pass every "none of these is missed" below it, and
        // a list that failed to bind is null, not empty.
        corpus.Should().NotBeNull();
        corpus!.Statements.Should().HaveCountGreaterThan(2500);
        corpus.Statements2025.Should().NotBeNull();
        corpus.NotReported.Should().NotBeNullOrEmpty();
        corpus.Prose.Should().HaveCountGreaterThan(300);
        corpus.ReportedProse.Should().NotBeNullOrEmpty();
        corpus.KnownMisses.Should().NotBeNullOrEmpty();
        return corpus;
    }
}
