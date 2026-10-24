using System.Diagnostics;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Options;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The audit verifier as the process an operator or a scheduled job runs: the real
/// <c>azurebank-audit-verifier.dll</c>, started as a child, read by its exit code and its output.
/// </summary>
/// <remarks>
/// <para>
/// A CHILD PROCESS, because the claim is about one. Every other test of the verifier calls a
/// command's <c>RunAsync</c> and reads the number it returns. What a job reads is the number the
/// PROCESS ends with, and between the two there are three steps no test ran: the handler that puts
/// the number in <c>Environment.ExitCode</c> before it prints, <c>Program.Main</c>, and the
/// translation of the command-line framework's own code. That framework answers every mistake on
/// the command line with 1, and 1 is this tool's word for CHAIN BROKEN.
/// </para>
/// <para>
/// The dll is the one in THE VERIFIER'S OWN OUTPUT FOLDER, as <c>SeederProcessTests</c> takes the
/// Seeder's: the copy beside the test assembly shares that folder's <c>appsettings.json</c> with
/// the API's. The child gets <c>DOTNET_ENVIRONMENT=Production</c> and has the connection string
/// and every <c>Audit__</c> setting REMOVED from its environment unless a test sets them, so a
/// developer's shell cannot make a test pass. It starts in the temporary folder.
/// </para>
/// <para>
/// The collector of a coverage run does not see a child process: these tests add no line to that
/// figure, and hold what the figure cannot.
/// </para>
/// </remarks>
public sealed class AuditVerifierProcessTests : IDisposable
{
    // Port 1 on loopback: nothing listens there, and nothing leaves the machine.
    private const string AbsentServer =
        "Server=127.0.0.1,1;Database=x;User Id=u;Password=not-a-secret;Connect Timeout=1;TrustServerCertificate=True";

    private static readonly (string Name, string Value)[] UsableKeys =
    [
        ("ConnectionStrings__DefaultConnection", AbsentServer),
        ("Audit__ChainKey", VerifierProcess.ChainKey),
        ("Audit__AnchorKey", VerifierProcess.AnchorKey),
    ];

    private readonly string _directory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "azurebank-verifier-process-" + Guid.NewGuid().ToString("N")))
            .FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
        {
            // A leaked temp directory is not worth failing a green run over (ExportCommandTests).
        }
    }

    [Fact]
    public async Task Help_WithNoKeyAtAll_ListsTheFiveCommands_AndExitsZero()
    {
        var (output, exitCode) = await VerifierProcess.Run(["--help"]);

        using var all = new AssertionScope();
        exitCode.Should().Be(0, ChildProcess.Shown(output));
        output.Should().Contain("verify").And.Contain("anchor").And.Contain("export")
            .And.Contain("evidence").And.Contain("notify");
        output.Should().NotContain("CANNOT VERIFY", "a tool that is not configured can still say what it is");
        output.Should().NotContain("Unhandled exception");
    }

    [Theory]
    // Measured 2026-10-12 on the pinned System.CommandLine: the framework says 1 for each of these.
    [InlineData("no command at all", "Required command was not provided.")]
    [InlineData("a mistyped command", "Unrecognized command or argument 'verfy'.")]
    [InlineData("an option nobody defined", "Unrecognized command or argument '--fast'.")]
    [InlineData("export with no path", "Required argument missing for command: 'export'.")]
    [InlineData("notify with no contact", "Option '--contact' is required.")]
    public async Task AMistakeOnTheCommandLine_ExitsFour_AndNeverOne(string mistake, string said)
    {
        string[] arguments = mistake switch
        {
            "no command at all" => [],
            "a mistyped command" => ["verfy"],
            "an option nobody defined" => ["verify", "--fast"],
            "export with no path" => ["export"],
            "notify with no contact" => ["notify", _directory],
            _ => throw new ArgumentOutOfRangeException(nameof(mistake), mistake, "not a case of this test"),
        };

        var (output, exitCode) = await VerifierProcess.Run(arguments, UsableKeys);

        using var all = new AssertionScope();
        exitCode.Should().Be(4, "a job that reads 1 takes a typing mistake for a tampered trail. " + ChildProcess.Shown(output));
        output.Should().Contain(said);
        output.Should().NotContain("Unhandled exception");
    }

    [Theory]
    [InlineData("verify", "CANNOT VERIFY: this tool is not configured to read the chain.")]
    [InlineData("anchor", "NO VERDICT: this tool is not configured to record an anchor.")]
    public async Task WithNoKeyAtAll_ACommandThatReadsTheChain_ExitsThree_AndNamesBothKeys(string command, string said)
    {
        // The connection string is set and names a server that is not there: a command that went
        // on without its keys would say "could not be read" instead.
        var (output, exitCode) = await VerifierProcess.Run(
            [command], ("ConnectionStrings__DefaultConnection", AbsentServer));

        using var all = new AssertionScope();
        exitCode.Should().Be(3, ChildProcess.Shown(output));
        output.Should().Contain(said);
        output.Should().Contain("Audit:ChainKey must be configured").And.Contain("Audit:AnchorKey must be configured");
        output.Should().NotContain("could not be read");
        output.Should().NotContain("Unhandled exception");
    }

    [Fact]
    public async Task Verify_OnAServerThatIsNotThere_ExitsThree_AndPrintsNoStackTrace()
    {
        // The collision the tool exists to keep apart: an outage is 3, a broken chain is 1.
        var (output, exitCode) = await VerifierProcess.Run(["verify"], UsableKeys);

        using var all = new AssertionScope();
        exitCode.Should().Be(3, ChildProcess.Shown(output));
        output.Should().Contain("CANNOT VERIFY: the audit store could not be read, so there is no verdict.");
        output.Should().Contain("SqlException: ");
        output.Should().NotContain("   at ", "the operator gets a sentence, not a stack trace");
    }

    [Fact]
    public async Task Evidence_ForABlankNumber_ExitsFour_ByTheHandlersOwnCode()
    {
        // The parser accepts a blank argument, so this 4 is the handler's: it reaches the process
        // only through Environment.ExitCode.
        var (output, exitCode) = await VerifierProcess.Run(["evidence", "   "], UsableKeys);

        using var all = new AssertionScope();
        exitCode.Should().Be(4, ChildProcess.Shown(output));
        output.Should().Contain("NOT ASSEMBLED: that is not a transaction number.");
    }

    [Fact]
    public async Task Export_OverAFileThatExists_ExitsSix_AndLeavesTheFileAsItWas()
    {
        // Refused before anything is read: the server is not there and the answer is still 6.
        var path = Path.Combine(_directory, "already-here.jsonl");
        const string earlier = "{\"anchorSequence\":1}\n";
        await File.WriteAllTextAsync(path, earlier);

        var (output, exitCode) = await VerifierProcess.Run(["export", path], UsableKeys);

        using var all = new AssertionScope();
        exitCode.Should().Be(6, ChildProcess.Shown(output));
        output.Should().Contain("already exists, and this will not overwrite it.");
        ComparableText.Of(await File.ReadAllTextAsync(path)).Should().Be(ComparableText.Of(earlier));
    }

    [Fact]
    public async Task Notify_IntoADirectoryThatIsNotThere_ExitsFour_AndCreatesNothing()
    {
        var missing = Path.Combine(_directory, "nobody-made-this");

        var (output, exitCode) = await VerifierProcess.Run(
            ["notify", missing, "--contact", "security@your-bank.example"], UsableKeys);

        using var all = new AssertionScope();
        exitCode.Should().Be(4, ChildProcess.Shown(output));
        output.Should().Contain("is not an existing directory.");
        Directory.Exists(missing).Should().BeFalse("a spool of addresses lands only where somebody meant it to");
    }
}

/// <summary>
/// The same child process against a real SQL Server, on a database of its own: the verdicts a job
/// acts on, as the numbers the process ends with.
/// </summary>
/// <remarks>
/// The rows are written here, through the application's own context and chain, under the keys the
/// child is then given. The tampering is an UPDATE on the connection, past the change tracker,
/// which is how it would be done.
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class AuditVerifierProcessSqlServerTests : IDisposable
{
    private readonly string _connectionString = new SqlConnectionStringBuilder(SqlServerFactAttribute.ConnectionString ?? "Server=unset")
    {
        InitialCatalog = $"AzureBankVerifier_{Guid.NewGuid():N}",
    }.ConnectionString;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"azurebank-verifier-sql-{Guid.NewGuid():N}");

    public void Dispose()
    {
        // The database goes whatever becomes of the directory: a file that cannot be deleted
        // still fails the test, and must not leave a database of this run on the server.
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(SqlServerFactAttribute.ConnectionString))
            {
                using var db = Context();
                db.Database.EnsureDeleted();
            }
        }
    }

    private AzureBankDbContext Context() =>
        new(
            new DbContextOptionsBuilder<AzureBankDbContext>().UseSqlServer(_connectionString).Options,
            timeProvider: null,
            auditChain: new AuditChain(
                Options.Create(new AuditOptions { ChainKey = VerifierProcess.ChainKey, AnchorKey = VerifierProcess.AnchorKey }),
                NullLogger<AuditChain>.Instance));

    [SqlServerFact]
    public async Task TheVerdictsOfARealChain_AreTheExitCodesOfTheProcess()
    {
        Directory.CreateDirectory(_directory);
        (string Name, string Value)[] configured =
        [
            ("ConnectionStrings__DefaultConnection", _connectionString),
            ("Audit__ChainKey", VerifierProcess.ChainKey),
            ("Audit__AnchorKey", VerifierProcess.AnchorKey),
        ];

        await using (var db = Context())
        {
            await db.Database.MigrateAsync();
        }

        // An empty table is not an intact one: 2, and the job that reads it is told so.
        var (empty, emptyExit) = await VerifierProcess.Run(["verify"], configured);
        emptyExit.Should().Be(2, ChildProcess.Shown(empty));

        await using (var db = Context())
        {
            for (var i = 0; i < 3; i++)
            {
                db.AuditEvents.Add(new AuditEvent
                {
                    Id = Guid.CreateVersion7(),
                    OccurredAt = DateTime.UtcNow,
                    Event = $"VerifierProcessEvent{i}",
                    Outcome = AuditOutcome.Succeeded,
                    ActorUserId = Guid.NewGuid(),
                    RowHash = string.Empty,
                });
                await db.SaveChangesAsync();
            }
        }

        var (intact, intactExit) = await VerifierProcess.Run(["verify"], configured);
        var (anchored, anchoredExit) = await VerifierProcess.Run(["anchor"], configured);
        var copy = Path.Combine(_directory, "anchors.jsonl");
        var (exported, exportedExit) = await VerifierProcess.Run(["export", copy], configured);

        using (new AssertionScope())
        {
            intactExit.Should().Be(0, ChildProcess.Shown(intact));
            intact.Should().Contain("3 rows");
            anchoredExit.Should().Be(0, ChildProcess.Shown(anchored));
            anchored.Should().Contain("ANCHOR 1 recorded.");
            exportedExit.Should().Be(0, ChildProcess.Shown(exported));
            File.Exists(copy).Should().BeTrue();
        }

        // One row rewritten on the connection, as whoever holds the database would do it.
        await using (var db = Context())
        {
            var changed = await db.Database.ExecuteSqlRawAsync(
                "UPDATE [AuditEvents] SET [Event] = N'SomethingElse' "
                + "WHERE [Sequence] = (SELECT MAX([Sequence]) FROM [AuditEvents])");
            changed.Should().Be(1, "ARRANGE: one row was rewritten");
        }

        var (broken, brokenExit) = await VerifierProcess.Run(["verify"], configured);
        var (marker, markerExit) = await VerifierProcess.Run(["anchor"], configured);

        using var all = new AssertionScope();
        brokenExit.Should().Be(1, "a rewritten row is the one answer a job must be able to tell from every other. " + ChildProcess.Shown(broken));
        broken.Should().Contain("BROKEN");
        markerExit.Should().Be(1, "anchor records that it ran, and the verdict about the chain is still the verdict. " + ChildProcess.Shown(marker));
        marker.Should().Contain("GAP MARKER 2 recorded");
    }
}

/// <summary>Starts the verifier's dll as a child process and returns what it printed and its exit code.</summary>
internal static class VerifierProcess
{
    // Not secrets: keys of the length the tool asks for, for a database a test creates and drops.
    public const string ChainKey = "verifier-process-tests-chain-key-0123456789ab";
    public const string AnchorKey = "verifier-process-tests-anchor-key-9876543210xy";

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(90);

    public static async Task<(string Output, int ExitCode)> Run(
        string[] arguments, params (string Name, string Value)[] environment)
    {
        var dll = ChildProcess.ToolDll("AzureBank.AuditVerifier", "azurebank-audit-verifier.dll");
        File.Exists(dll).Should().BeTrue($"the verifier is built with the test project, into {dll}");

        var start = new ProcessStartInfo(ChildProcess.DotnetHost())
        {
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,

            // No terminal behind it, as under a job runner: RunAsync closes a redirected input at once.
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(dll);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment.Remove("ConnectionStrings__DefaultConnection");

        // A key left in the shell cannot answer for the test.
        foreach (var name in start.Environment.Keys
            .Where(key => key.StartsWith("Audit__", StringComparison.OrdinalIgnoreCase))
            .ToList())
        {
            start.Environment.Remove(name);
        }

        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        var (output, exitCode, exited) = await ChildProcess.RunAsync(start, Deadline);
        exited.Should().BeTrue($"a one-shot ends within {Deadline.TotalSeconds} s. {ChildProcess.Shown(output)}");
        return (output, exitCode);
    }
}
