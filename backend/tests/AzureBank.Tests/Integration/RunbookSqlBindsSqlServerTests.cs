using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Every SQL block every runbook prints must BIND: name tables and columns the migrated schema
/// has, and columns no join makes ambiguous.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the parse check is not enough.</b> <see cref="RunbookSqlParsesSqlServerTests"/> sends a
/// block under <c>SET PARSEONLY ON</c>, which checks grammar and resolves no name. It would have
/// passed the diagnostic PR #127 shipped — <c>SELECT … transaction_id</c> over a join of two DMVs
/// that both expose it, <c>Msg 209, Ambiguous column name</c> the first time an operator ran it —
/// and it passes a block that reads a column a later migration renamed.
/// </para>
/// <para>
/// <b><c>SET SHOWPLAN_XML ON</c>, in a batch of its own, against a migrated database.</b> The
/// server compiles what follows and runs none of it. Measured on 2026-09-19 (SQL Server 2025,
/// LocalDB), each option set in its own batch, the same probes under each:
/// </para>
/// <code>
///                                   PARSEONLY   NOEXEC     SHOWPLAN_XML
/// #127's ambiguous join             accepted    Msg 209    Msg 209
/// a column that does not exist      accepted    Msg 207    Msg 207
/// a TABLE that does not exist       accepted    accepted   Msg 208
/// '&lt;placeholder&gt;' = a uniqueidentifier  accepted    accepted   accepted
/// </code>
/// <para>
/// <c>NOEXEC</c> leaves a missing table to deferred name resolution, which is the rename this
/// guard most needs to see, so it is <c>SHOWPLAN_XML</c>. That nothing runs under it was measured
/// with a batch whose run would show — an <c>UPDATE</c> of four rows, a <c>CREATE TABLE</c>, a
/// <c>PRINT</c>: with no option all three left their mark, under <c>SHOWPLAN_XML</c> none did —
/// and <see cref="ABlockIsCompiledAndNeverRun"/> holds that here. (The first version of that
/// control updated a table with no rows in it, and its silence proved nothing.)
/// </para>
/// <para>
/// A placeholder inside a string literal compiles, so such blocks are checked as written. A block
/// the parse check exempts by placeholder token (<c>KILL &lt;session_id&gt;</c> is not grammar)
/// is exempt here by the same list: <see cref="RunbookSqlParsesSqlServerTests.Runbooks"/>.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class RunbookSqlBindsSqlServerTests
{
    private readonly ITestOutputHelper _output;

    public RunbookSqlBindsSqlServerTests(ITestOutputHelper output) => _output = output;

    /// <summary>The configured database, migrated: starting the host is what applies the schema.</summary>
    private static string MigratedDatabase()
    {
        using var factory = new CustomWebApplicationFactory();
        factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        using var client = factory.CreateClient();

        // Unpooled, because the session is left in SHOWPLAN_XML: a pooled connection would carry
        // the option into whichever test drew it next, and that test's statements would do nothing.
        return new SqlConnectionStringBuilder(SqlServerFactAttribute.ConnectionString!) { Pooling = false }
            .ConnectionString;
    }

    /// <summary>
    /// Compiles one block and runs none of it; returns the server's refusal, or null. A connection
    /// each, for the reason <see cref="RunbookSqlParsesSqlServerTests.ParseOnlyAsync"/> gives.
    /// </summary>
    private static async Task<string?> CompileOnlyAsync(string database, string block)
    {
        await using var connection = new SqlConnection(database);
        await connection.OpenAsync();
        await using (var showPlan = connection.CreateCommand())
        {
            showPlan.CommandText = "SET SHOWPLAN_XML ON;";
            await showPlan.ExecuteNonQueryAsync();
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = block;

            // The plans come back as result sets and are drained, not inspected. The refusal does
            // not ride behind them: the server compiles the WHOLE batch before it returns anything
            // (measured — with only the first result read, a block whose second statement names a
            // missing table was still refused), so a bad statement anywhere in a block throws here.
            await using var reader = await command.ExecuteReaderAsync();
            do
            {
                while (await reader.ReadAsync())
                {
                }
            }
            while (await reader.NextResultAsync());

            return null;
        }
        catch (SqlException e)
        {
            return $"Msg {e.Number}: {e.Message}";
        }
    }

    [SqlServerTheory]
    [MemberData(nameof(RunbookSqlParsesSqlServerTests.Runbooks), MemberType = typeof(RunbookSqlParsesSqlServerTests))]
    public async Task EverySqlBlockInTheRunbookBindsToTheSchema(
        string runbook, int minimumBlocks, string[] placeholders)
    {
        var path = Path.Combine(RunbookSqlParsesSqlServerTests.RepositoryRoot().FullName, runbook);
        var blocks = RunbookMarkdown.Read(await File.ReadAllLinesAsync(path)).Fences
            .Where(fence => fence.IsSql)
            .Select(fence => fence.Text)
            .ToList();
        blocks.Should().HaveCountGreaterThanOrEqualTo(minimumBlocks, $"{runbook} holds that many");

        var database = MigratedDatabase();
        var refused = new List<string>();
        var compiled = 0;
        foreach (var block in blocks)
        {
            if (placeholders.Any(token => block.Contains(token, StringComparison.Ordinal))
                || RunbookSqlParsesSqlServerTests.TouchesParseOnly(block))
            {
                continue;
            }

            compiled++;
            if (await CompileOnlyAsync(database, block) is { } refusal)
            {
                refused.Add($"{refusal}\n{block.Trim()}");
            }
        }

        foreach (var refusal in refused)
        {
            _output.WriteLine(refusal);
        }

        _output.WriteLine($"{runbook}: {compiled} of {blocks.Count} SQL blocks compiled, {refused.Count} refused");
        compiled.Should().BeGreaterThanOrEqualTo(blocks.Count - placeholders.Length);

        // As one string, so a run names every refusal and not the first (BeEmpty on a collection
        // prints "at least one item").
        string.Join("\n\n", refused).Should().BeEmpty(
            "an operator pastes these under pressure, and a table or a column the schema no longer "
            + "has is an error message where the answer should be");
    }

    /// <summary>
    /// The controls: each refusal this guard exists for is refused, by its message number, and a
    /// block that binds is accepted — so an accepted runbook block means something.
    /// </summary>
    [SqlServerFact]
    public async Task TheGuardRefusesWhatTheParseCheckCannotSee()
    {
        var database = MigratedDatabase();

        (await CompileOnlyAsync(database, "SELECT n.Id, n.AuditEventId FROM SubscriberNotices n;"))
            .Should().BeNull("these are a table and two columns the schema has");

        // PR #127's defect, as shipped: both DMVs expose transaction_id.
        (await CompileOnlyAsync(
                database,
                "SELECT transaction_id FROM sys.dm_tran_database_transactions dt "
                + "JOIN sys.dm_tran_session_transactions st ON dt.transaction_id = st.transaction_id;"))
            .Should().StartWith("Msg 209:");

        (await CompileOnlyAsync(database, "SELECT n.AuditEventIdRenamed FROM SubscriberNotices n;"))
            .Should().StartWith("Msg 207:");

        (await CompileOnlyAsync(database, "SELECT n.Id FROM SubscriberNoticesRenamed n;"))
            .Should().StartWith("Msg 208:", "this is the one NOEXEC accepts");

        (await CompileOnlyAsync(database, "SELECT 1;\nSELECT n.Id FROM SubscriberNoticesRenamed n;"))
            .Should().StartWith("Msg 208:", "the whole block is compiled, not its first statement");

        (await CompileOnlyAsync(database, "SELECT e.Id FROM AuditEvents e WHERE e.Id = '<AuditEventId from above>';"))
            .Should().BeNull("a placeholder inside a literal compiles, so such a block is checked as written");
    }

    [SqlServerFact]
    public async Task ABlockIsCompiledAndNeverRun()
    {
        var database = MigratedDatabase();
        var table = "ZzBindProbe_" + Guid.NewGuid().ToString("N");

        async Task<bool> TableExistsAsync()
        {
            await using var connection = new SqlConnection(database);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE name = @name;";
            command.Parameters.AddWithValue("@name", table);
            return (int)(await command.ExecuteScalarAsync())! > 0;
        }

        try
        {
            (await CompileOnlyAsync(database, $"CREATE TABLE dbo.{table} (i int);")).Should().BeNull();
            (await TableExistsAsync()).Should().BeFalse("the block was compiled, not run");

            // The control on the control: the same statement with no option set DOES create it,
            // so "no table" above is the option's doing and not a check that cannot see one.
            await using (var connection = new SqlConnection(database))
            {
                await connection.OpenAsync();
                await using var create = connection.CreateCommand();
                create.CommandText = $"CREATE TABLE dbo.{table} (i int);";
                await create.ExecuteNonQueryAsync();
            }

            (await TableExistsAsync()).Should().BeTrue();
        }
        finally
        {
            await using var connection = new SqlConnection(database);
            await connection.OpenAsync();
            await using var drop = connection.CreateCommand();
            drop.CommandText = $"DROP TABLE IF EXISTS dbo.{table};";
            await drop.ExecuteNonQueryAsync();
        }
    }
}
