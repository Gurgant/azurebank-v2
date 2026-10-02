using System.Diagnostics;
using AzureBank.Infrastructure.Data;
using AzureBank.Infrastructure.Extensions;
using AzureBank.Shared.Options;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The Seeder as the process a container runs: the real <c>azurebank-seeder.dll</c>, started as a
/// child with the environment a job would give it, read by its exit code and its output.
/// </summary>
/// <remarks>
/// <para>
/// A CHILD PROCESS, because the claims are about one: an exit code a job runner acts on, what a
/// tool prints when it has no secret at all, and which folder it reads its settings from. Before,
/// the PIN pepper was validated in <c>Program.cs</c> ahead of the command line, so with none set
/// every invocation, <c>--help</c> included, ended in an unhandled
/// <c>OptionsValidationException</c> (measured 2026-10-01: exit 127 under Git Bash, 139 in a Linux
/// container).
/// </para>
/// <para>
/// The dll is the one in THE SEEDER'S OWN OUTPUT FOLDER. The copy the test project's build puts
/// beside the test assembly shares that folder's <c>appsettings.json</c> with the API's, so it
/// would not be the tool as it ships.
/// </para>
/// <para>
/// The child gets <c>DOTNET_ENVIRONMENT=Production</c> and has the connection string and the pepper
/// REMOVED from its environment unless a test sets them, so a developer's shell or user-secrets
/// cannot make a test pass. It starts in the temporary folder, never in the Seeder's own.
/// </para>
/// </remarks>
public sealed class SeederProcessTests
{
    private const string AzureConnection =
        "Server=tcp:not_a_server.database.windows.net,1433;Database=x;User Id=u;Password=not-a-secret;Connect Timeout=1";

    [Fact]
    public async Task Help_WithNoSecretAtAll_ListsTheThreeCommands_AndExitsZero()
    {
        var (output, exitCode) = await SeederProcess.Run(["--help"]);

        using var all = new AssertionScope();
        exitCode.Should().Be(0, SeederProcess.Shown(output));
        output.Should().Contain("migrate").And.Contain("seed").And.Contain("reset");
        output.Should().NotContain("Unhandled exception");
    }

    [Fact]
    public async Task NoCommand_WithNoSecretAtAll_PrintsTheUsage_AndExitsOne()
    {
        var (output, exitCode) = await SeederProcess.Run([]);

        using var all = new AssertionScope();
        exitCode.Should().Be(1, SeederProcess.Shown(output));
        output.Should().Contain("Required command was not provided.");
        output.Should().NotContain("Unhandled exception");
    }

    [Theory]
    [InlineData("seed")]
    [InlineData("reset", "--confirm")]
    public async Task OnAnAzureSqlName_WithNoPepper_SeedAndResetExitTwo_WithTheirRefusal(params string[] arguments)
    {
        var (output, exitCode) = await SeederProcess.Run(
            arguments, ("ConnectionStrings__DefaultConnection", AzureConnection));

        using var all = new AssertionScope();
        exitCode.Should().Be(2, SeederProcess.Shown(output));
        output.Should().Contain($"{arguments[0]} refused: not_a_server.database.windows.net is an Azure SQL server");
        output.Should().Contain("Nothing was opened");
        output.Should().NotContain("Unhandled exception");
    }

    [Fact]
    public async Task Migrate_WithNoConnectionString_ExitsTwo_NamingTheVariable()
    {
        // The tool ships no default string: a job with no secret mapped says so, and does not go
        // looking for a server on localhost.
        var (output, exitCode) = await SeederProcess.Run(["migrate"]);

        using var all = new AssertionScope();
        exitCode.Should().Be(2, SeederProcess.Shown(output));
        output.Should().Contain("migrate refused").And.Contain("ConnectionStrings__DefaultConnection");
        output.Should().NotContain("Unhandled exception");
    }

    [Fact]
    public async Task Migrate_StartedFromAnotherFolder_OpensWithThePoolItsOwnSettingsAskFor()
    {
        // The content root is the binary's folder, not the folder the process starts in (here the
        // temporary one). The limits line is printed before anything is opened, so reading it
        // needs no server: nothing listens on port 1 of loopback, the wait is one attempt, exit 1.
        // The same claim on a real server is SeederProcessSqlServerTests below, which a job with no
        // SQL Server skips.
        var (output, exitCode) = await SeederProcess.Run(
            ["migrate", "--wait-seconds", "0"],
            ("ConnectionStrings__DefaultConnection", "Server=127.0.0.1,1;Database=x;User Id=u;Password=not-a-secret;Connect Timeout=1"));

        using var all = new AssertionScope();
        exitCode.Should().Be(1, SeederProcess.Shown(output));
        output.Should().Contain("pool 5, pool blocking", "the 5 is in the appsettings.json beside the dll, and nowhere else");
        output.Should().Contain("did not accept a connection within 0 s");
        output.Should().NotContain("Unhandled exception");
    }
}

/// <summary>
/// The same child process against a real SQL Server: <c>migrate</c> started from a folder that is
/// not the Seeder's still reads the Seeder's own settings.
/// </summary>
/// <remarks>
/// The tool used to take its content root from the current directory. Started from anywhere else
/// (CI runs it from <c>backend/</c>) it lost its pool of 5 and its log levels: 874 lines for one
/// <c>reset</c>, 169 of them "Executed DbCommand", against 17 and 0 from its own folder (measured
/// 2026-10-01).
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class SeederProcessSqlServerTests : IDisposable
{
    private readonly string _connectionString = new SqlConnectionStringBuilder(SqlServerFactAttribute.ConnectionString ?? "Server=unset")
    {
        InitialCatalog = $"AzureBankOneShot_{Guid.NewGuid():N}",
    }.ConnectionString;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"azurebank-seeder-cwd-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }

        if (string.IsNullOrWhiteSpace(SqlServerFactAttribute.ConnectionString))
        {
            return;
        }

        using var db = new AzureBankDbContext(
            new DbContextOptionsBuilder<AzureBankDbContext>().UseSqlServer(_connectionString).Options);
        db.Database.EnsureDeleted();
    }

    [SqlServerFact]
    public async Task Migrate_StartedFromAnotherFolder_StillReadsItsOwnSettings()
    {
        Directory.CreateDirectory(_folder);

        // A keyword the test's own string sets wins, as it would anywhere; otherwise the pool is
        // the 5 of the Seeder's appsettings.json.
        var expected = new SqlConnectionStringBuilder(
            SqlConnectionDefaults.Apply(_connectionString, new DatabaseOptions { MaxPoolSize = 5 }));

        var (output, exitCode) = await SeederProcess.Run(
            ["migrate"], _folder, ("ConnectionStrings__DefaultConnection", _connectionString));

        using var all = new AssertionScope();
        exitCode.Should().Be(0, SeederProcess.Shown(output));
        output.Should().Contain($"pool {expected.MaxPoolSize}, pool blocking");
        output.Should().Contain("Applying migration '", "EF's own line is the record of what this run applied");
        output.Should().Contain("The database is at ");
        output.Should().NotContain("Executed DbCommand", "the tool's settings hold EF's command log at Warning");
    }
}

/// <summary>Starts the Seeder's dll as a child process and returns what it printed and its exit code.</summary>
internal static class SeederProcess
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(90);

    public static Task<(string Output, int ExitCode)> Run(
        string[] arguments, params (string Name, string Value)[] environment) =>
        Run(arguments, Path.GetTempPath(), environment);

    public static async Task<(string Output, int ExitCode)> Run(
        string[] arguments, string workingDirectory, params (string Name, string Value)[] environment)
    {
        var dll = Dll();
        File.Exists(dll).Should().BeTrue($"the Seeder is built with the test project, into {dll}");

        var start = new ProcessStartInfo(DotnetHost())
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
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
        start.Environment.Remove("Security__PinPepper");
        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        using var process = Process.Start(start)!;

        // No terminal behind it, as in a container: a prompt reads end-of-input instead of waiting.
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        var exited = true;
        using (var deadline = new CancellationTokenSource(Deadline))
        {
            try
            {
                await process.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                exited = false;
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }

        var output = await stdout + await stderr;
        exited.Should().BeTrue($"a one-shot ends within {Deadline.TotalSeconds} s. {Shown(output)}");
        return (output, process.ExitCode);
    }

    /// <summary>The output as a reason for an assertion, with the braces its formatter would read escaped.</summary>
    public static string Shown(string output) =>
        "the process printed:" + Environment.NewLine + output.Replace("{", "{{").Replace("}", "}}");

    /// <summary>
    /// <c>azurebank-seeder.dll</c> in the Seeder's own output folder, for the configuration this
    /// test assembly was built in (its own folder is <c>…/bin/&lt;configuration&gt;/net10.0/</c>).
    /// </summary>
    private static string Dll()
    {
        var own = new DirectoryInfo(
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var framework = own.Name;
        var configuration = own.Parent!.Name;
        return Path.Combine(
            SeederHost.RepoRoot(), "backend", "tools", "AzureBank.Seeder", "bin", configuration, framework, "azurebank-seeder.dll");
    }

    /// <summary>The dotnet host the SDK exports as DOTNET_HOST_PATH, else the one on PATH.</summary>
    private static string DotnetHost() =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host
            ? host
            : "dotnet";
}
