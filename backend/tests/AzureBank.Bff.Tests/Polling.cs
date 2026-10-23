namespace AzureBank.Bff.Tests;

/// <summary>
/// Waits for what a host does on a thread of its own, after the request that set it going has
/// been answered: the revoke that ending a session queues, a renewal that runs on, a line the log
/// writes last.
/// </summary>
internal static class Polling
{
    /// <summary>
    /// Asks <paramref name="condition"/> every 25 ms until it holds, for ten seconds unless
    /// <paramref name="within"/> gives another wait, and answers whether it held. The answer is
    /// the caller's to assert, with a reason of its own.
    /// </summary>
    internal static async Task<bool> Eventually(Func<bool> condition, TimeSpan? within = null)
    {
        var deadline = DateTime.UtcNow + (within ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(25);
        }

        return true;
    }
}
