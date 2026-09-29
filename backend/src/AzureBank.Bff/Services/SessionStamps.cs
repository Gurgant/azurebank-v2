using System.Collections.Concurrent;

namespace AzureBank.Bff.Services;

/// <summary>
/// The latest session stamp the BFF knows for each user who holds a session (ADR-0057 §5.3): the
/// map the store's validity check reads and <see cref="SessionStampWatcher"/> fills.
/// </summary>
/// <remarks>
/// <para>
/// <b>It only ever rises.</b> A stamp is a counter the API raises on every sign-out of all a user's
/// sessions, so a lower value is an older read. Keeping the highest one means a late answer, or a
/// sign-in whose stamp was read just before a sign-out committed, can never bring an ended session
/// back.
/// </para>
/// <para>
/// <b>Fed from two places.</b> The watcher's poll, every 15 s while anyone holds a session, and
/// every new session: a sign-in answers the user's current stamp, which can already be above what
/// the last poll read. No sign-out of all a user's sessions starts inside the BFF today ("Esci" ends
/// one session, ADR-0057 §4.6), so the poll and the sign-ins are the only sources.
/// </para>
/// <para>
/// <b>A failed poll changes nothing here</b>: the map keeps its last values. The lever that raised a
/// stamp also revoked the user's grants, so a session this map has not heard about still ends at its
/// next renewal.
/// </para>
/// </remarks>
public sealed class SessionStamps
{
    private readonly ConcurrentDictionary<Guid, int> _latest = new();

    /// <summary>
    /// Whether a session given <paramref name="stamp"/> at sign-in is still current: true unless a
    /// higher stamp is known for <paramref name="userId"/>.
    /// </summary>
    public bool IsCurrent(Guid userId, int stamp) =>
        !_latest.TryGetValue(userId, out var latest) || stamp >= latest;

    /// <summary>The latest stamp known for <paramref name="userId"/>, or null when none is.</summary>
    public int? Latest(Guid userId) => _latest.TryGetValue(userId, out var latest) ? latest : null;

    /// <summary>Records <paramref name="stamp"/> for <paramref name="userId"/> unless a higher one is known.</summary>
    public void Observe(Guid userId, int stamp) =>
        _latest.AddOrUpdate(userId, stamp, (_, known) => Math.Max(known, stamp));

    /// <summary>
    /// Forgets every user not in <paramref name="signedIn"/>, so the map holds no more users than the
    /// store does. Safe to race a sign-in: a user forgotten here is read again at the next poll.
    /// </summary>
    public void RetainOnly(IReadOnlySet<Guid> signedIn)
    {
        foreach (var userId in _latest.Keys)
        {
            if (!signedIn.Contains(userId))
            {
                _latest.TryRemove(userId, out _);
            }
        }
    }
}
