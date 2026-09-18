using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Asks SQL Server itself about the two things <see cref="RunbookSqlGrammar"/> and
/// <see cref="RunbookSqlCorpus"/> take on trust everywhere else: that every corpus statement is
/// T-SQL, and that the permission vocabulary is the server's.
/// </summary>
/// <remarks>
/// Parsed, never run, the way <see cref="RunbookSqlParsesSqlServerTests"/> parses runbook blocks:
/// <c>SET PARSEONLY ON</c> in a batch of its own, on an unpooled connection to <c>master</c>.
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class RunbookSqlCorpusSqlServerTests
{
    /// <summary>SQL Server 2025's major version: the one the corpus was collected on.</summary>
    private const int SqlServer2025 = 17;

    private readonly ITestOutputHelper _output;

    public RunbookSqlCorpusSqlServerTests(ITestOutputHelper output) => _output = output;

    private static async Task<SqlConnection> OpenMasterAsync()
    {
        var master = new SqlConnectionStringBuilder(SqlServerFactAttribute.ConnectionString!)
        {
            InitialCatalog = "master",
            Pooling = false,
        };
        var connection = new SqlConnection(master.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    [SqlServerFact]
    public async Task EveryCorpusStatementIsValidSql()
    {
        var corpus = RunbookSqlCorpus.Load();
        string serverVersion;
        await using (var probe = await OpenMasterAsync())
        {
            serverVersion = probe.ServerVersion;
        }

        var major = int.Parse(serverVersion.Split('.')[0]);

        // A statement that names PARSEONLY could switch parsing off, and what followed it would RUN.
        // RunbookSqlParsesSqlServerTests refuses such a block; so does this.
        // notReported is asked too: "valid T-SQL the scan does not reach" is a claim about T-SQL.
        var statements = (major >= SqlServer2025 ? corpus.AllStatements : corpus.Statements)
            .Concat(corpus.NotReported)
            .ToList();
        var unsafeToSend = statements.Where(RunbookSqlParsesSqlServerTests.TouchesParseOnly).ToList();
        var toParse = statements.Except(unsafeToSend).ToList();

        // Every failure is collected before anything is asserted, so one run names them all.
        var rejected = new List<string>();
        foreach (var statement in toParse)
        {
            /*
              A CONNECTION EACH, which is how the corpus was measured. Several SET options take
              effect while a batch is PARSED (QUOTED_IDENTIFIER is one), so on a shared connection
              one corpus statement would change how the ones after it parse.
            */
            await using var connection = await OpenMasterAsync();
            await using (var parseOnly = connection.CreateCommand())
            {
                parseOnly.CommandText = "SET PARSEONLY ON;";
                await parseOnly.ExecuteNonQueryAsync();
            }

            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = statement;
                await command.ExecuteNonQueryAsync();
            }
            catch (SqlException e)
            {
                rejected.Add($"Msg {e.Number}: {e.Message} <- {statement.ReplaceLineEndings(" / ")}");
            }
        }

        foreach (var refusal in rejected)
        {
            _output.WriteLine(refusal);
        }

        _output.WriteLine(
            $"SQL Server {serverVersion}: {toParse.Count - rejected.Count} of {toParse.Count} "
            + $"corpus statements parsed; {unsafeToSend.Count} name PARSEONLY and were not sent; "
            + $"{corpus.AllStatements.Count() + corpus.NotReported.Length - statements.Count} need "
            + "SQL Server 2025 and were not asked");

        // As ONE string: BeEmpty on a collection names "at least one item", the first, and the point
        // of collecting was that a server three versions older names all of its refusals in one run.
        // Measured on CI's SQL Server 2022: 13 refused, and the collection form showed one.
        string.Join(Environment.NewLine, rejected).Should().BeEmpty(
            "the corpus is what the scan is measured against, and a line in it that is not T-SQL "
            + "measures nothing; one that only a newer server parses belongs in statements2025");
    }

    [SqlServerFact]
    public async Task ThePermissionVocabularyHoldsEveryNameTheServerReports()
    {
        await using var connection = await OpenMasterAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT DISTINCT permission_name FROM sys.fn_builtin_permissions(DEFAULT) ORDER BY permission_name;";

        var reported = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                reported.Add(reader.GetString(0).Trim());
            }
        }

        // The control: a query that returned nothing would make "none is missing" true of any list.
        reported.Should().Contain("TAKE OWNERSHIP").And.HaveCountGreaterThan(100);
        _output.WriteLine(
            $"SQL Server {connection.ServerVersion} reports {reported.Count} permission names; "
            + $"the vocabulary holds {RunbookSqlGrammar.Permissions.Length}");
        reported.Except(RunbookSqlGrammar.Permissions, StringComparer.OrdinalIgnoreCase).Should().BeEmpty(
            "GRANT, DENY and REVOKE are recognised by the permission they name, so a permission the "
            + "server accepts and the vocabulary lacks is a statement the scan cannot see");
    }
}
