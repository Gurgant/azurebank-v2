namespace AzureBank.Bff.Models;

/// <summary>
/// Complete session data stored server-side in the BFF.
/// JWT tokens are NEVER exposed to the browser.
/// </summary>
public class UserSession
{
    /// <summary>
    /// Cryptographically secure session identifier (32 bytes, URL-safe Base64).
    /// This is what gets stored in the browser cookie.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// User's unique identifier from the API.
    /// </summary>
    public required Guid UserId { get; init; }

    /// <summary>
    /// JWT access token - stored server-side, never sent to browser.
    /// </summary>
    public required string AccessToken { get; set; }

    /// <summary>
    /// When the access token expires.
    /// </summary>
    public required DateTime TokenExpiry { get; set; }

    /// <summary>
    /// When the access token was issued: its own <c>iat</c>, or the moment the BFF received it when
    /// the token carries none. <see cref="TokenExpiry"/> minus this is the token's lifetime L, from
    /// which the renewal thresholds are taken (06 §4.5, F6): a 15-minute token renews in the
    /// background from 7.5 minutes left, a 2-minute one from 60 seconds.
    /// </summary>
    public required DateTime TokenIssuedAt { get; set; }

    /// <summary>
    /// The session's grant: the refresh token presented for every new access token. It does not
    /// rotate (06 §4.3), so it is set once, at sign-in, and never overwritten — init-only says so.
    /// Never reaches the browser. Null when registration's best-effort issuance failed: such a
    /// session cannot renew and ends with its access token (see InMemoryTokenStore.IsSessionValid).
    /// </summary>
    public string? RefreshToken { get; init; }

    /// <summary>
    /// When the grant stops working, as the API reported it at sign-in (<c>refreshTokenExpiresAt</c>).
    /// Fixed at issue and never extended. Null when there is no grant.
    /// </summary>
    public DateTime? GrantExpiresAt { get; init; }

    /// <summary>
    /// When the session was created - for absolute timeout enforcement.
    /// </summary>
    public required DateTime SessionCreated { get; init; }

    /// <summary>
    /// The session's hard end: the earlier of <see cref="SessionCreated"/> plus the absolute timeout
    /// and <see cref="GrantExpiresAt"/> (06 §4.1, F5). One field, so the store's validity check,
    /// <c>/me</c> and <c>session-status</c> cannot disagree about it. The grant is minted before the
    /// session, so when it sets the cap the cap is earlier by at most the sign-in's own duration.
    /// </summary>
    public required DateTime AbsoluteExpiresAt { get; init; }

    /// <summary>
    /// The user's session stamp as the API answered it at sign-in (06 §5.3). Every sign-out of all
    /// the user's sessions raises the stamp at the API; once <see cref="Services.SessionStamps"/>
    /// knows a higher value, the store's validity check refuses this session and it ends by the one
    /// path every ending takes (06 §4.6). Fixed for the session's life.
    /// </summary>
    public int SessionStamp { get; init; }

    /// <summary>
    /// Last user activity - for inactivity timeout enforcement.
    /// Updated on every authenticated request.
    /// </summary>
    public DateTime LastActivity { get; set; }

    /// <summary>
    /// Current authentication level:
    /// 0 = None (not authenticated)
    /// 1 = Session (logged in with email/password)
    /// 2 = PIN (verified PIN for sensitive operations)
    /// </summary>
    public int AuthLevel { get; set; } = 1;

    /// <summary>
    /// When PIN was last verified - null if not verified.
    /// PIN verification expires after configured duration (default 5 min).
    /// </summary>
    public DateTime? PinVerifiedAt { get; set; }

    /// <summary>
    /// Last known user information, served by <c>/bff/auth/me</c> whenever its read of the API
    /// cannot produce a valid answer (ADR-0039) — not only when the API is unreachable. There are
    /// five such paths and they are all deliberate: the renewal gives no token, the request
    /// throws, the deadline expires, the response is not 2xx, or the body does not deserialise into
    /// a usable handle.
    ///
    /// <para>
    /// This used to say the cache existed "to avoid API calls on /bff/auth/me", which stopped being
    /// true the moment that endpoint started reading through — and while it was true it let a
    /// rename made through the proxy go unnoticed for a whole session.
    /// </para>
    /// </summary>
    public required UserSessionInfo UserInfo { get; init; }

    /*
      RENEWAL AND ENDING, under ONE lock on the session (06 §4.5-4.6, F2).

      Before PR-1 the single flight lived in a map of semaphores beside the store, and the map dropped
      a session's semaphore whenever it saw the session gone, so a caller still holding the old one
      and a caller creating a new one could both renew. Here the flag that says the session is over
      and the renewal in flight sit on the session itself, and every renewal checks the one and
      registers the other under this lock — as does every ending, which sets Ended and captures the
      renewal in flight in the same step, so the revoke that follows can wait for it.
    */

    /// <summary>The lock that guards <see cref="Ended"/>, <see cref="InFlightRenewal"/> and the renewal state below.</summary>
    public Lock SyncRoot { get; } = new();

    /// <summary>
    /// True once the session has ended ("Esci", idle expiry, the cap, re-authentication, a new
    /// sign-in over its cookie, a dead grant, a raised stamp, a graceful stop). Never goes back to
    /// false. Set only under <see cref="SyncRoot"/>; nothing renews, and no renewal result is stored,
    /// after it.
    /// </summary>
    public bool Ended { get; set; }

    /// <summary>The renewal running for this session, or null. Registered and cleared under <see cref="SyncRoot"/>.</summary>
    public Task? InFlightRenewal { get; set; }

    /// <summary>
    /// No renewal starts before this instant: set 15 seconds after a renewal failed for a transient
    /// reason (06 §4.5), so a database outage costs the API one renewal per session every 15 seconds
    /// rather than one per request.
    /// </summary>
    public DateTime? RenewalRetryAfter { get; set; }

    /// <summary>
    /// True once a renewal came back with an expiry no later than the one already held — the API
    /// clamps access tokens to the grant's expiry, so there is nothing more to gain — or the grant
    /// was refused. The session stops renewing for good (06 §4.5).
    /// </summary>
    public bool RenewalStopped { get; set; }

    // Computed properties
    public bool IsTokenExpired => DateTime.UtcNow >= TokenExpiry;

    /// <summary>
    /// A renewal starts only if it can gain at least this much: the grant must outlive the access
    /// token already held by one second or more (06 §4.5, F6).
    /// </summary>
    public static readonly TimeSpan MinimumRenewalGain = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Whether a renewal could ever give this session a later token: it holds a grant, has not
    /// stopped renewing, and the grant outlives its access token by <see cref="MinimumRenewalGain"/>.
    /// A session for which this is false ends with its access token.
    /// </summary>
    public bool CanStillRenew =>
        RefreshToken is not null
        && !RenewalStopped
        && (GrantExpiresAt is not { } grantEnd || grantEnd - TokenExpiry >= MinimumRenewalGain);

    public bool IsPinVerificationValid(int validityMinutes) =>
        PinVerifiedAt.HasValue &&
        DateTime.UtcNow < PinVerifiedAt.Value.AddMinutes(validityMinutes);
}

/// <summary>
/// Cached user info in session (mirrors UserLoginInfo from Shared).
/// </summary>
public class UserSessionInfo
{
    public required Guid Id { get; init; }
    public required string Email { get; init; }
    public required string FirstName { get; init; }
    public required string LastName { get; init; }
    /// <summary>
    /// Settable, like <see cref="HasPin"/> and for the same reason: it is the only other cached
    /// field a signed-in user can change mid-session (ADR-0015 rename), so the BFF has to be able
    /// to write it back. Everything else here — Id, Email, FirstName, LastName — is immutable for
    /// the life of the session, and stays init-only to say so.
    /// </summary>
    public required string AzureTag { get; set; }
    public bool HasPin { get; set; }
}
