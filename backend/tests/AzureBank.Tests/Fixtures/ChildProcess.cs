using System.Diagnostics;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Runs a tool or a host as a child process, for the tests whose claim is about a process: the
/// exit code it ends with and what it printed.
/// </summary>
/// <remarks>
/// Each caller keeps its own arguments, working folder, environment and deadline, and says in its
/// own words what a child still running at the deadline means.
/// </remarks>
internal static class ChildProcess
{
    /// <summary>The dotnet host the SDK exports as DOTNET_HOST_PATH, else the one on PATH.</summary>
    public static string DotnetHost() =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host
            ? host
            : "dotnet";

    /// <summary>
    /// Starts <paramref name="start"/>, which redirects both output streams, and waits up to
    /// <paramref name="deadline"/> for it to end. Returns its standard output followed by its
    /// standard error, its exit code, and whether it ended by itself: a child still running at the
    /// deadline is killed with every process it started, and <c>Exited</c> is false.
    /// </summary>
    /// <remarks>
    /// An input the caller redirected is closed at once, so the child has no terminal behind it and
    /// a prompt reads end-of-input instead of waiting. Both streams are read while the child runs:
    /// a child whose pipe is full and unread stops, and would meet the deadline instead of its end.
    /// </remarks>
    public static async Task<(string Output, int ExitCode, bool Exited)> RunAsync(
        ProcessStartInfo start, TimeSpan deadline)
    {
        using var process = Process.Start(start)!;

        if (start.RedirectStandardInput)
        {
            process.StandardInput.Close();
        }

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        var exited = true;
        using (var timeout = new CancellationTokenSource(deadline))
        {
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                exited = false;
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }

        var output = await stdout + await stderr;
        return (output, process.ExitCode, exited);
    }

    /// <summary>The output as a reason for an assertion, with the braces its formatter would read escaped.</summary>
    public static string Shown(string output) =>
        "the process printed:" + Environment.NewLine + output.Replace("{", "{{").Replace("}", "}}");

    /// <summary>
    /// <paramref name="fileName"/> in the output folder of the tool <paramref name="project"/> under
    /// <c>backend/tools</c>, for the configuration this test assembly was built in (its own folder
    /// is <c>…/bin/&lt;configuration&gt;/net10.0/</c>).
    /// </summary>
    public static string ToolDll(string project, string fileName)
    {
        var own = new DirectoryInfo(
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var framework = own.Name;
        var configuration = own.Parent!.Name;
        return Path.Combine(
            RepositoryFiles.Root().FullName, "backend", "tools", project, "bin", configuration, framework, fileName);
    }
}
