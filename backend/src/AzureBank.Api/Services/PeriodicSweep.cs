namespace AzureBank.Api.Services;

/// <summary>
/// The loop that the API's three periodic sweeps run through:
/// <see cref="IdempotencyCleanupService"/>, <see cref="RefreshTokenCleanupService"/> and
/// <see cref="NoticeRelayService"/>. A <see cref="PeriodicTimer"/>, so the first sweep starts one
/// full period after the call and a fast test host is never interrupted; a catch-all per sweep, so
/// a sweep that fails with anything but a cancellation never ends the loop while the host runs;
/// every cancellation rethrown past it and absorbed at the outer level as the host's shutdown,
/// even one that did not come from <c>stoppingToken</c>, which ends the loop without a line.
/// </summary>
/// <remarks>
/// <para>
/// THE TIMER IS BUILT BEFORE THE FIRST AWAIT, so it exists by the time this call hands its task
/// back: a test that moves a fake clock straight after starting the loop moves a timer that is
/// already counting.
/// </para>
/// <para>
/// THE LINES STAY THE CALLER'S. Each service passes two callbacks rather than a logger and two
/// strings, so that every template stays a literal at its call site, where
/// <c>LogPlaceholderClassTests</c> reads it, and every line keeps its service's logger category.
/// </para>
/// </remarks>
internal static class PeriodicSweep
{
    /// <summary>
    /// Runs <paramref name="sweep"/> once per <paramref name="period"/> on <paramref name="clock"/>
    /// until <paramref name="stoppingToken"/> is cancelled. A sweep that fails once the stop was
    /// asked goes to <paramref name="interruptedByShutdown"/> and ends the loop; any other failure
    /// goes to <paramref name="failed"/>, and the loop waits for the next period.
    /// </summary>
    public static async Task RunAsync(
        TimeSpan period,
        TimeProvider clock,
        Func<CancellationToken, Task> sweep,
        Action<Exception> interruptedByShutdown,
        Action<Exception> failed,
        CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(period, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await sweep(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (stoppingToken.IsCancellationRequested)
                {
                    // A statement cancelled once it is on the server comes back as the provider's
                    // own exception, not as an OperationCanceledException, and nothing in it tells
                    // it from a statement that failed while the host was stopping. Neither is
                    // logged as a failed sweep: this process retries nothing after the stop, and a
                    // fault that outlives it meets the first sweep of the next start, which says so.
                    interruptedByShutdown(ex);
                    return;
                }
                catch (Exception ex)
                {
                    failed(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown.
        }
    }
}
