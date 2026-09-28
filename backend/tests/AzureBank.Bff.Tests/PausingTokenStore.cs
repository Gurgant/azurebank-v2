using AzureBank.Bff.Models;
using AzureBank.Bff.Services.Interfaces;

namespace AzureBank.Bff.Tests;

/// <summary>
/// The real session store with one seam: it can hold ONE write-back between a request's read of its
/// session and the write that puts the session back. Everything else goes straight through.
/// </summary>
/// <remarks>
/// <para>
/// Written for 06 §10 O0-2 item 7 (F2). Every session write in <c>SessionService</c> is a read
/// (<c>GetSession</c>), a change to the object, then <c>UpdateSessionAsync</c>. If "Esci" removes
/// the session between the two, a store that writes back unconditionally puts it back, and the
/// signed-out cookie works again. The gap is microseconds wide in production; this makes it as wide
/// as the test needs, without touching production code.
/// </para>
/// <para>
/// Register it in place of <c>ITokenStoreService</c>, wrapping the real <c>InMemoryTokenStore</c>,
/// then: <see cref="PauseNextWriteBack"/>, send the request, await <see cref="WriteBackPaused"/>, do
/// what must happen in the gap, <see cref="ReleaseWriteBack"/>. Release in a <c>finally</c>: a held
/// request never finishes on its own.
/// </para>
/// </remarks>
internal sealed class PausingTokenStore(ITokenStoreService inner) : ITokenStoreService
{
    private readonly TaskCompletionSource _paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _armed;

    /// <summary>Completes once a write-back is being held, its session already read.</summary>
    public Task WriteBackPaused => _paused.Task;

    /// <summary>Holds the next <c>UpdateSessionAsync</c>, and only that one.</summary>
    public void PauseNextWriteBack() => Volatile.Write(ref _armed, 1);

    /// <summary>Lets the held write-back reach the real store.</summary>
    public void ReleaseWriteBack() => _release.TrySetResult();

    public async Task UpdateSessionAsync(UserSession session)
    {
        if (Interlocked.Exchange(ref _armed, 0) == 1)
        {
            _paused.TrySetResult();
            await _release.Task;
        }

        await inner.UpdateSessionAsync(session);
    }

    public Task StoreSessionAsync(UserSession session) => inner.StoreSessionAsync(session);

    public Task<UserSession?> GetSessionAsync(string sessionId) => inner.GetSessionAsync(sessionId);

    public Task RemoveSessionAsync(string sessionId) => inner.RemoveSessionAsync(sessionId);

    public Task CleanupExpiredSessionsAsync() => inner.CleanupExpiredSessionsAsync();
}
