using System.Diagnostics;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Xunit;

namespace AzureBank.Tests.Integration;

/// <summary>
/// A host that refuses to start ends its PROCESS with exit code 1. Until 2026-09-11 the API's
/// <c>Program.cs</c> logged the refusal and returned 0, so a supervisor keyed on exit codes read a
/// host that never listened as a clean shutdown. Measured with <c>Audit:AnchorKey</c> emptied: 0
/// before the fix, 1 after.
/// </summary>
/// <remarks>
/// <para>
/// The real <c>AzureBank.Api.dll</c> runs as a child process, because the claim is about a process.
/// <c>WebApplicationFactory</c> runs the same <c>Program.cs</c> INSIDE the test host, where
/// <see cref="Environment.ExitCode"/> belongs to the test host and nothing asserts on it.
/// </para>
/// <para>
/// The environment is <c>Testing</c>, never <c>Development</c>: Development loads user-secrets, and
/// a developer whose secrets hold a valid anchor key would get a host that STARTS. The factory's
/// test keys are passed as environment variables, all but the anchor key, which is removed so an
/// inherited one cannot fill it. <c>--urls http://127.0.0.1:0</c> sends a host that does start to a
/// free port rather than the BFF's 5000, where a bind failure would exit 1 for the wrong reason —
/// which is also why the refusal is read back out of the output rather than inferred from the code.
/// </para>
/// </remarks>
public sealed class FailedStartupExitCodeTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task AHostThatRefusesToStart_EndsTheProcessWithOne()
    {
        var start = new ProcessStartInfo(DotnetHost())
        {
            WorkingDirectory = Path.Combine(BackendRoot().FullName, "src", "AzureBank.Api"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add("http://127.0.0.1:0");

        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";
        start.Environment["Jwt__Secret"] =
            "integration-tests-only-signing-key-0123456789abcdef0123456789abcdef";
        start.Environment["Idempotency__HashKey"] = CustomWebApplicationFactory.IdempotencyHashKey;
        start.Environment["StepUp__BindingKey"] = CustomWebApplicationFactory.StepUpBindingKey;
        start.Environment["Security__PinPepper"] = CustomWebApplicationFactory.PinPepper;
        start.Environment["Audit__ChainKey"] = CustomWebApplicationFactory.AuditChainKey;
        start.Environment.Remove("Audit__AnchorKey");

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
            $"a host that refuses to start ends within {Deadline.TotalSeconds}s, and one still " +
            "running STARTED, so the refusal under test never happened. Output:" +
            $"{Environment.NewLine}{output}");
        output.Should().Contain(
            "Audit:AnchorKey must be configured with at least 32 characters",
            "the exit code is only evidence if the refusal is the one this test set up");
        process.ExitCode.Should().Be(1,
            "a supervisor reads the exit code, and a refusal that exits 0 reads as a clean stop");
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

        dir.Should().NotBeNull("the API runs from its own content root, so the sources must exist");
        return dir!;
    }
}
