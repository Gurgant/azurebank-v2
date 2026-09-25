using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;

namespace AzureBank.Bff.Tests;

/// <summary>
/// A BFF that refuses to start ends its process with 1, never says it started, and in Production
/// writes all of it as JSON lines: the API's <c>FailedStartupExitCodeTests</c>, for this host, and
/// what its console looks like.
/// </summary>
/// <remarks>
/// Measured on the container running as Production (2026-09-25), with a
/// <c>ServiceCredential:BffKey</c> shorter than 32 characters: the log read "AzureBank BFF Gateway
/// started successfully" and then "Hosting failed to start", every line was text, and the
/// exception's stack trace ran over many lines. The real <c>AzureBank.Bff.dll</c> runs as a child
/// process because the console and the exit code belong to the process; a sink registered in a
/// test host sees events, not the text a collector reads. <c>--urls http://127.0.0.1:0</c> keeps a
/// host that does start off port 5000.
/// </remarks>
public sealed class FailedStartupTests
{
    private const string Refusal = "ServiceCredential:BffKey must be configured with at least 32 characters";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task ARefusalToStart_ExitsOne_AndNeverSaysItStarted()
    {
        var (output, exitCode) = await RunRefusingBff("Testing");

        output.Should().Contain(Refusal, "the output is only evidence if the refusal is the one this test set up");
        output.Should().NotContain("started successfully",
            "the success line waits for the host to have started, and this one never did");
        exitCode.Should().Be(1);
    }

    [Fact]
    public async Task InProduction_EveryLineIsJson_AndTheRefusalIsOneEvent()
    {
        var (output, exitCode) = await RunRefusingBff("Production");

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lines.Should().NotBeEmpty();
        var events = lines.Select(line =>
        {
            var parse = () => JsonDocument.Parse(line);
            parse.Should().NotThrow($"every console line is a JSON object in Production, and this one is not: {line}");
            return parse().RootElement;
        }).ToList();

        // The bootstrap logger's lines too: "Starting" is written before the host exists.
        events.Select(e => e.GetProperty("@m").GetString()).Should().Contain("Starting AzureBank BFF Gateway...");
        var fatal = events
            .Where(e => e.TryGetProperty("@l", out var level) && level.GetString() == "Fatal")
            .ToList();
        fatal.Should().ContainSingle();
        fatal[0].GetProperty("@x").GetString().Should().Contain(Refusal, "the stack trace rides inside the one event");
        output.Should().NotContain("started successfully");
        exitCode.Should().Be(1);
    }

    [Fact]
    public async Task AConsoleNamedInConfiguration_IsTheOnlyConsole()
    {
        // As a Serilog:WriteTo in a local appsettings file would name one (the API's example does).
        var (output, _) = await RunRefusingBff("Testing", ("Serilog__WriteTo__0__Name", "Console"));

        output.Split("Hosting failed to start").Length.Should().Be(2,
            "one console writes the host's refusal once; a second would print every line twice");
    }

    private static async Task<(string Output, int ExitCode)> RunRefusingBff(
        string environment, params (string Name, string Value)[] extra)
    {
        var start = new ProcessStartInfo(DotnetHost())
        {
            WorkingDirectory = Path.Combine(BackendRoot().FullName, "src", "AzureBank.Bff"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add("http://127.0.0.1:0");

        // Both variables: the host reads DOTNET_ENVIRONMENT over ASPNETCORE_ENVIRONMENT (measured
        // on the API, recorded in its FailedStartupExitCodeTests), so an inherited one would decide.
        start.Environment["ASPNETCORE_ENVIRONMENT"] = environment;
        start.Environment["DOTNET_ENVIRONMENT"] = environment;
        // The module initializer put a valid key in this process's environment; the child gets a
        // short one instead, which is the refusal under test.
        start.Environment["ServiceCredential__BffKey"] = "too-short";
        foreach (var (name, value) in extra)
        {
            start.Environment[name] = value;
        }

        using var process = Process.Start(start)!;
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
        exited.Should().BeTrue(
            $"a host that refuses to start ends within {Deadline.TotalSeconds}s. Output:{Environment.NewLine}{output}");
        return (output, process.ExitCode);
    }

    /// <summary>The dotnet host the SDK exports as DOTNET_HOST_PATH, else the one on PATH.</summary>
    private static string DotnetHost() =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host
            ? host
            : "dotnet";

    /// <summary>Walks up from the test assembly to the directory holding the solution.</summary>
    private static DirectoryInfo BackendRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AzureBank.slnx")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("the BFF runs from its own content root, so the sources must exist");
        return dir!;
    }
}
