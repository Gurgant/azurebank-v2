using AzureBank.Shared.Constants;
using AzureBank.Shared.Options;
using Microsoft.Extensions.Options;

namespace AzureBank.Api.Services;

/// <summary>
/// ADR-0057 §6, anomaly 2: counts the renewals of each grant that the grant check accepted, over
/// one access-token lifetime, and raises the security event <c>RefreshRenewalRateHigh</c> when one
/// grant is renewed more than <see cref="Limit"/> times in it. It never refuses a renewal and never
/// writes to the database: the count lives in this process's memory and nowhere else.
/// </summary>
/// <remarks>
/// <para>
/// WHY IT EXISTS. A grant does not rotate (ADR-0057), so a second holder of a LIVE grant renews
/// unseen until its session ends. ADR-0057 §6 reasons, and nothing has measured, that the
/// legitimate BFF renews at most twice per token lifetime: single flight, the half-life threshold,
/// and the stop near the cap (ADR-0057 §4.5). Reasoned too: the token's <c>exp</c> is whole
/// seconds, so two of its renewals can come a little under half a lifetime apart, and one window
/// can then hold three. A limit of 3 raises nothing for three. So this catches a copy that renews
/// often, not a patient one.
/// </para>
/// <para>
/// WHY IT WRITES NOTHING. A renewal that writes is what ADR-0057 §1 removed: a lost answer, an EF
/// retry of a commit that did land, a hung database. The event is a Warning in the logs only, so on
/// Azure, where application logs are off, nobody sees it; locally and in CI they do. Putting it in
/// the audit trail would need a write.
/// </para>
/// <para>
/// WHAT IS COUNTED. The request's <c>ReceivedAt</c> stamp, the process's monotonic clock
/// (ADR-0057 F10), over the window (newest − <c>Jwt:ExpirationMinutes</c>, newest]. Only the newest
/// <see cref="Limit"/> + 1 stamps of a grant are kept, which is all "more than
/// <see cref="Limit"/>" needs. A request stamped earlier can reach here later than one stamped
/// after it, so a stamp is inserted in order rather than appended.
/// </para>
/// <para>
/// ONE EVENT PER GRANT PER WINDOW, at most. The event fires when the window first holds more than
/// <see cref="Limit"/> renewals, and not again for that grant until one token lifetime has passed:
/// a copy that renews every second writes one line per lifetime, not one per request.
/// </para>
/// <para>
/// BOUNDED MEMORY. An entry whose newest stamp is at least one window old is removed by a sweep. A
/// sweep runs on a renewal, at most once per window, and again whenever a new grant finds the map
/// full, so an entry can be held for up to about two windows after its last renewal. Removing an
/// entry loses nothing: its count in any later window is zero, and its event gate is open again. The
/// entries are also capped at <see cref="DefaultCapacity"/>. A grant that arrives while the map is
/// full and holds nothing expired is not counted, and a Warning says so, at most once per window.
/// </para>
/// <para>
/// A FALSE ALARM, read in the code and not measured. During a database hang, a renewal the BFF gave
/// up on after its 30 s timeout still reaches this count once the database answers (the API's
/// refresh takes no cancellation), and the BFF sends another after its 15 s cooldown. Over a long
/// enough hang one session can pass the limit. It costs one log line, and nobody is refused.
/// </para>
/// </remarks>
public sealed class RefreshRenewalRateDetector
{
    /// <summary>Renewals of one grant in one window that raise no event (ADR-0057 §6).</summary>
    public const int Limit = 3;

    /// <summary>The most grants tracked at once.</summary>
    public const int DefaultCapacity = 10_000;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly TimeSpan _window;
    private readonly int _capacity;
    private readonly ILogger<RefreshRenewalRateDetector> _logger;
    private DateTime _nextSweepAt = DateTime.MinValue;
    private DateTime? _lastFullWarningAt;

    public RefreshRenewalRateDetector(IOptions<JwtOptions> jwtOptions, ILogger<RefreshRenewalRateDetector> logger)
        : this(jwtOptions, logger, DefaultCapacity)
    {
    }

    /// <summary>For tests: a smaller capacity, so a full map takes a handful of grants.</summary>
    internal RefreshRenewalRateDetector(
        IOptions<JwtOptions> jwtOptions, ILogger<RefreshRenewalRateDetector> logger, int capacity)
    {
        // At least a minute: a lifetime of zero or less mints tokens that are already dead, and a
        // window that empty would drop the stamp it had just added.
        _window = TimeSpan.FromMinutes(Math.Max(1, jwtOptions.Value.ExpirationMinutes));
        _capacity = capacity;
        _logger = logger;
    }

    /// <summary>How many grants the detector holds now. For tests: the memory bound is this count.</summary>
    internal int TrackedGrants
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Counts one accepted renewal of <paramref name="grantId"/>, received at <paramref name="receivedAt"/>.</summary>
    public void Record(Guid grantId, Guid userId, DateTime receivedAt)
    {
        var raise = false;
        int? fullAt = null;

        lock (_gate)
        {
            if (receivedAt >= _nextSweepAt)
            {
                Sweep(receivedAt);
            }

            if (_entries.TryGetValue(grantId, out var entry))
            {
                raise = entry.Add(receivedAt, _window);
            }
            else if (HasRoomFor(receivedAt))
            {
                entry = new Entry();
                _entries.Add(grantId, entry);
                raise = entry.Add(receivedAt, _window);
            }
            else if (_lastFullWarningAt is not { } warned || receivedAt - warned >= _window)
            {
                _lastFullWarningAt = receivedAt;
                fullAt = _entries.Count;
            }
        }

        // Logged outside the lock: a slow sink must not queue every other renewal behind it.
        if (fullAt is { } count)
        {
            _logger.LogWarning(
                "Renewal-rate detector holds {Count} grants, its capacity; refresh token {TokenId} is not counted",
                count, grantId);
        }

        if (raise)
        {
            _logger.LogWarning(
                "SecurityEvent {SecurityEvent}: refresh token {TokenId} (user {UserId}) was renewed more than {Max} times within {Interval}; answered, nothing written",
                SecurityEvents.RefreshRenewalRateHigh, grantId, userId, Limit, _window);
        }
    }

    /// <summary>True when a new grant fits, after sweeping once if the map is full.</summary>
    private bool HasRoomFor(DateTime now)
    {
        if (_entries.Count < _capacity)
        {
            return true;
        }

        Sweep(now);
        return _entries.Count < _capacity;
    }

    /// <summary>Removes every entry whose newest stamp has left the window ending at <paramref name="now"/>.</summary>
    private void Sweep(DateTime now)
    {
        var cutoff = now - _window;
        foreach (var (grantId, entry) in _entries)
        {
            if (entry.Newest <= cutoff)
            {
                // Removing during enumeration is allowed for Dictionary since .NET Core 3.0.
                _entries.Remove(grantId);
            }
        }

        _nextSweepAt = now + _window;
    }

    /// <summary>One grant: its newest stamps, oldest first, and when it last raised the event.</summary>
    private sealed class Entry
    {
        private readonly List<DateTime> _stamps = new(Limit + 1);
        private DateTime? _raisedAt;

        public DateTime Newest => _stamps[^1];

        /// <summary>Adds one stamp; true when this renewal raises the event.</summary>
        public bool Add(DateTime receivedAt, TimeSpan window)
        {
            var at = _stamps.Count;
            while (at > 0 && _stamps[at - 1] > receivedAt)
            {
                at--;
            }

            _stamps.Insert(at, receivedAt);
            if (_stamps.Count > Limit + 1)
            {
                _stamps.RemoveAt(0);
            }

            var newest = _stamps[^1];
            _stamps.RemoveAll(s => s <= newest - window);

            if (_stamps.Count <= Limit)
            {
                return false;
            }

            if (_raisedAt is { } raised && newest - raised < window)
            {
                return false;
            }

            _raisedAt = newest;
            return true;
        }
    }
}
