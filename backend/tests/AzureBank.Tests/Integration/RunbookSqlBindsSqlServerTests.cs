using System.Text.RegularExpressions;
using System.Xml.Linq;
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
    private static async Task<string?> CompileOnlyAsync(string database, string block) =>
        (await CompileAsync(database, block)).Refusal;

    /// <summary>
    /// Compiles one block and runs none of it: the server's refusal, or null, and the kind of
    /// every statement it compiled, in order, as the plan names it (<c>StatementType</c>:
    /// <c>SELECT</c>, <c>UPDATE</c>, <c>SELECT INTO</c>, <c>EXECUTE STRING</c>, …).
    /// </summary>
    private static async Task<(string? Refusal, IReadOnlyList<string> Kinds)> CompileAsync(string database, string block)
    {
        await using var connection = new SqlConnection(database);
        await connection.OpenAsync();
        await using (var showPlan = connection.CreateCommand())
        {
            showPlan.CommandText = "SET SHOWPLAN_XML ON;";
            await showPlan.ExecuteNonQueryAsync();
        }

        var kinds = new List<string>();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = block;

            // The plans come back as result sets, and one thing is read from them: what kind each
            // statement is. The refusal does not ride behind them: the server compiles the WHOLE
            // batch before it returns anything (measured — with only the first result read, a
            // block whose second statement names a missing table was still refused), so a bad
            // statement anywhere in a block throws here.
            await using var reader = await command.ExecuteReaderAsync();
            do
            {
                while (await reader.ReadAsync())
                {
                    // A statement inside another (the UPDATE under an IF) is an element of its
                    // own in the plan, so every level is read.
                    kinds.AddRange(XDocument.Parse(reader.GetString(0)).Descendants()
                        .Select(element => element.Attribute("StatementType")?.Value)
                        .OfType<string>());
                }
            }
            while (await reader.NextResultAsync());

            return (null, kinds);
        }
        catch (SqlException e)
        {
            return ($"Msg {e.Number}: {e.Message}", kinds);
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

    /// <summary>
    /// The sentence a runbook promises it with. A runbook that carries it is held to it by
    /// <see cref="ARunbookThatSaysItsSqlOnlyReads_HoldsNothingButSelects"/>.
    /// </summary>
    private const string OnlyReads = "Every statement here reads and none writes.";

    /// <summary>The runbook that makes the promise today.</summary>
    private const string DemoPoolRunbook = "docs/runbooks/demo-pool.md";

    /// <summary>The one kind of statement a plan may name in such a runbook.</summary>
    private const string Select = "SELECT";

    /// <summary>
    /// A runbook that tells its reader "every statement here reads and none writes" holds SQL an
    /// operator pastes into the demo's database on that word. Until 2026-10-04 nothing held the
    /// word: with one block of <c>docs/runbooks/demo-pool.md</c> replaced by
    /// <c>UPDATE DemoCopies SET Writes = 0 WHERE DeletedAt IS NULL;</c> the four runbook classes
    /// passed 20 of 20, since an <c>UPDATE</c> parses and binds as well as a <c>SELECT</c> does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The server says what each statement is.</b> Under <c>SHOWPLAN_XML</c> the plan names
    /// every statement's kind, and only <c>SELECT</c> is let through: the twelve blocks of the
    /// pool's runbook are twelve of them (measured on LocalDB 17, 2026-10-04). No list of verbs is
    /// kept here, so a way of writing nobody thought of is refused for not being a
    /// <c>SELECT</c>; <see cref="ThePlanNamesAWriteWhereverItStands"/> holds what the plan names
    /// the ones somebody did think of. A block that only reads in another way (<c>DECLARE</c> and
    /// a <c>SELECT</c> of the variable: <c>ASSIGN</c>, <c>SELECT WITHOUT QUERY</c>) fails here
    /// too, until its kinds are added on purpose.
    /// </para>
    /// <para>
    /// <b>What it does not see.</b> A <c>SELECT</c> that takes locks: with <c>WITH (UPDLOCK,
    /// HOLDLOCK)</c> the plan still says <c>SELECT</c> (measured the same day). And a
    /// <c>SELECT</c> that writes through something it calls, which this schema gives no way to
    /// do: it has no sequence for <c>NEXT VALUE FOR</c> to draw from (<c>sys.sequences</c> is
    /// empty), and <c>OPENROWSET</c> was refused where it was tried (Msg 15281 on LocalDB, whose
    /// "Ad Hoc Distributed Queries" option is off).
    /// </para>
    /// </remarks>
    [SqlServerFact]
    public async Task ARunbookThatSaysItsSqlOnlyReads_HoldsNothingButSelects()
    {
        var root = RunbookSqlParsesSqlServerTests.RepositoryRoot().FullName;

        // Whichever runbooks carry the sentence, read with its line breaks folded: a promise
        // written into a second runbook is held from the day it is written.
        var promising = RunbookSqlParsesSqlServerTests.Runbooks
            .Select(row => (Runbook: (string)row[0], MinimumBlocks: (int)row[1], Placeholders: (string[])row[2]))
            .Where(row => Regex.Replace(File.ReadAllText(Path.Combine(root, row.Runbook)), @"\s+", " ")
                .Contains(OnlyReads, StringComparison.Ordinal))
            .ToList();
        promising.Select(row => row.Runbook).Should().Contain(
            DemoPoolRunbook,
            "CONTROL: the pool's runbook makes the promise in these words. If the sentence was "
            + "reworded, give this test the new words; if it was taken out, take this line out with it");

        var database = MigratedDatabase();
        var offenders = new List<string>();
        foreach (var (runbook, minimumBlocks, placeholders) in promising)
        {
            var blocks = RunbookMarkdown.Read(await File.ReadAllLinesAsync(Path.Combine(root, runbook))).Fences
                .Where(fence => fence.IsSql)
                .ToList();
            blocks.Should().HaveCountGreaterThanOrEqualTo(minimumBlocks, $"{runbook} holds that many");

            var statements = 0;
            foreach (var block in blocks)
            {
                var where = $"{runbook}:{block.OpenLine}";
                if (placeholders.Any(token => block.Text.Contains(token, StringComparison.Ordinal))
                    || RunbookSqlParsesSqlServerTests.TouchesParseOnly(block.Text))
                {
                    offenders.Add($"{where}: the block is not compiled (a placeholder, or PARSEONLY), so nothing says what it does");
                    continue;
                }

                var (refusal, kinds) = await CompileAsync(database, block.Text);
                statements += kinds.Count;
                if (refusal is not null)
                {
                    offenders.Add($"{where}: {refusal}");
                }
                else if (kinds.Count == 0 || kinds.Any(kind => kind != Select))
                {
                    offenders.Add($"{where}: the server reads this block as [{string.Join(", ", kinds)}]");
                }
            }

            _output.WriteLine($"{runbook}: {blocks.Count} SQL blocks, {statements} statements");
        }

        foreach (var offender in offenders)
        {
            _output.WriteLine(offender);
        }

        // As one string, so a run names every offender and not the first.
        string.Join("\n", offenders).Should().BeEmpty(
            "the runbook tells its reader that every statement in it reads and none writes, and an "
            + "operator runs them on that word. A block the server reads as anything but SELECTs "
            + "either writes, or reads in a way this check has not been taught");
    }

    /// <summary>
    /// The controls for the reading of a plan: a statement that writes is named as something other
    /// than a <c>SELECT</c> wherever it stands in a block, and the shapes the pool's runbook is
    /// written in are named <c>SELECT</c> and nothing else. Each block compiles against the
    /// migrated schema and none is run (<see cref="ABlockIsCompiledAndNeverRun"/>).
    /// </summary>
    [SqlServerFact]
    public async Task ThePlanNamesAWriteWhereverItStands()
    {
        var database = MigratedDatabase();

        async Task<IReadOnlyList<string>> KindsOfAsync(string block)
        {
            var (refusal, kinds) = await CompileAsync(database, block);
            refusal.Should().BeNull("ARRANGE: the block binds to the schema, so the plan is there to read");
            return kinds;
        }

        // What only reads, in the shapes the runbook uses: a join with an aggregate, a UNION ALL,
        // a CROSS APPLY, a common table expression.
        string[] reads =
        [
            "SELECT TOP (20) c.Id, c.ClaimedAt, c.Writes FROM DemoCopies c WHERE c.DeletedAt IS NULL ORDER BY c.Writes DESC, c.ClaimedAt;",
            "SELECT c.Id, COUNT(u.Id) AS Users FROM DemoCopies c LEFT JOIN AspNetUsers u ON u.DemoCopyId = c.Id GROUP BY c.Id HAVING COUNT(u.Id) <> 3;",
            "SELECT 'AspNetUsers' AS TableName, COUNT(*) AS N FROM AspNetUsers UNION ALL SELECT 'DemoCopies', COUNT(*) FROM DemoCopies;",
            "SELECT k.Free FROM DemoCopies c CROSS APPLY (SELECT CASE WHEN c.ClaimedAt IS NULL THEN 1 ELSE 0 END AS Free) k;",
            "WITH free AS (SELECT c.Id FROM DemoCopies c WHERE c.ClaimedAt IS NULL) SELECT COUNT(*) AS Free FROM free;",
        ];
        foreach (var block in reads)
        {
            (await KindsOfAsync(block)).Should().Equal([Select], block);
        }

        // What writes, and where in a block it can stand: alone, after a SELECT, under an IF,
        // behind a common table expression.
        (string Block, string Named)[] writes =
        [
            ("UPDATE DemoCopies SET Writes = 0 WHERE DeletedAt IS NULL;", "UPDATE"),
            ("SELECT COUNT(*) FROM DemoCopies;\nUPDATE DemoCopies SET Writes = 0 WHERE DeletedAt IS NULL;", "UPDATE"),
            ("IF EXISTS (SELECT 1 FROM DemoCopies) UPDATE DemoCopies SET Writes = 0;", "UPDATE"),
            ("DELETE FROM DemoCopies WHERE 1 = 0;", "DELETE"),
            ("WITH old AS (SELECT c.Id FROM DemoCopies c) DELETE FROM old;", "DELETE"),
            ("INSERT INTO DemoCopies (Id, OwnerUserId, CreatedAt) SELECT NEWID(), NEWID(), SYSUTCDATETIME() WHERE 1 = 0;", "INSERT"),
            ("MERGE DemoCopies AS t USING (SELECT NEWID() AS Id) AS s ON t.Id = s.Id WHEN MATCHED THEN UPDATE SET Writes = 0;", "MERGE"),
            ("SELECT c.Id INTO #claimed FROM DemoCopies c;", "SELECT INTO"),
        ];
        foreach (var (block, named) in writes)
        {
            (await KindsOfAsync(block)).Should().Contain(named, block);
        }

        // And what a plan names in some other word, which is all the check needs of it: not SELECT.
        string[] namedSomethingElse =
        [
            "EXEC (N'UPDATE DemoCopies SET Writes = 0');",
            "EXEC sp_executesql N'UPDATE DemoCopies SET Writes = 0';",
            "TRUNCATE TABLE DemoCopies;",
            "CREATE TABLE dbo.ZzOnlyReadsProbe (i int);",
            "DROP TABLE IF EXISTS dbo.ZzOnlyReadsProbe;",
            "DECLARE @n int = 1; SELECT @n;",
        ];
        foreach (var block in namedSomethingElse)
        {
            (await KindsOfAsync(block)).Should().Contain(kind => kind != Select, block);
        }
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
