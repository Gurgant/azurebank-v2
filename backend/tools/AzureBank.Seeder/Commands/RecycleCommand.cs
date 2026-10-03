using System.CommandLine;

namespace AzureBank.Seeder.Commands;

/// <summary>
/// STUB, so the tests that describe the command compile and fail on their own assertions: it
/// deletes nothing and exits 0.
/// </summary>
public static class RecycleCommand
{
    /// <summary>A command with the right name and nothing behind it.</summary>
    public static Command Create(IServiceProvider services, CancellationToken stopping = default) =>
        new("recycle", "Not written yet");

    /// <summary>Does nothing and exits 0.</summary>
    public static Task<int> RunAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        Task.FromResult(ExitCodes.Done);
}
