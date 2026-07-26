using AzureBank.Bff.Models;

namespace AzureBank.Bff.DTOs;

/// <summary>
/// Response for /bff/auth/login and /bff/auth/register.
/// Excludes JWT token - only returns user info.
/// </summary>
public class BffLoginResponse
{
    public required UserSessionInfo User { get; set; }
    public required DateTime ExpiresAt { get; set; }
}

/// <summary>
/// Response for /bff/auth/me.
/// Includes user info and session metadata.
/// </summary>
public class BffMeResponse
{
    public required UserSessionInfo User { get; set; }
    public required BffSessionInfo Session { get; set; }
}

/// <summary>
/// Session metadata returned to frontend.
/// </summary>
public class BffSessionInfo
{
    /// <summary>
    /// Current authentication level (1=Session, 2=PIN).
    /// </summary>
    public int AuthLevel { get; set; }

    /// <summary>
    /// When the session was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Last activity timestamp.
    /// </summary>
    public DateTime LastActivity { get; set; }

    /// <summary>
    /// When the session will expire (based on absolute timeout).
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// When the session would expire from INACTIVITY: <see cref="LastActivity"/> plus the
    /// configured inactivity window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>On this endpoint it reports the WINDOW, never the time remaining.</b> Fetching
    /// <c>/bff/auth/me</c> is itself activity — <c>SessionActivityMiddleware</c> slides
    /// <c>LastActivity</c> on every cookie-bearing request and excludes only the session-status
    /// probe (ADR-0018) — so this value is always "now plus the full window" by the time a caller
    /// reads it. Measured: two <c>/me</c> calls six seconds apart returned <c>LastActivity</c> six
    /// seconds apart. A countdown driven off this field would freeze at the window and never move.
    /// </para>
    /// <para>
    /// It is here so a client can learn the POLICY rather than hardcode it. The frontend duplicated
    /// the inactivity window as a constant that was wrong by 3x against a Development configuration
    /// and ignored the absolute cap entirely, so its warning could only ever fire after the session
    /// was already dead. For time REMAINING, poll the session-status probe, which does not disturb
    /// what it measures. The effective deadline is the EARLIER of the two rules.
    /// </para>
    /// </remarks>
    public DateTime InactivityExpiresAt { get; set; }

    /// <summary>
    /// Whether PIN verification is currently valid.
    /// </summary>
    public bool IsPinVerified { get; set; }

    /// <summary>
    /// When PIN verification expires (null if not verified).
    /// </summary>
    public DateTime? PinExpiresAt { get; set; }
}

/// <summary>
/// Response for /bff/auth/verify-pin.
/// </summary>
public class BffPinVerificationResponse
{
    public bool Verified { get; set; }
    public int AuthLevel { get; set; }
    public DateTime? PinExpiresAt { get; set; }
}

/// <summary>
/// Response for /bff/auth/session-status.
/// </summary>
public class BffSessionStatusResponse
{
    public bool IsAuthenticated { get; set; }
    public int? AuthLevel { get; set; }
    public bool? IsPinVerified { get; set; }

    /// <summary>
    /// When the session expires from inactivity. Null when unauthenticated.
    /// </summary>
    /// <remarks>
    /// <b>This is the only endpoint where a remaining-time value means anything.</b>
    /// <c>SessionActivityMiddleware</c> slides <c>LastActivity</c> on every cookie-bearing request
    /// and excludes exactly one route — this probe (ADR-0018). Everywhere else, reading the deadline
    /// resets it, so the answer is always "now plus the full window" and a countdown built on it
    /// would never move. Here the value genuinely counts down.
    /// </remarks>
    public DateTime? InactivityExpiresAt { get; set; }

    /// <summary>
    /// When the session expires regardless of activity. Null when unauthenticated.
    /// </summary>
    /// <remarks>
    /// Spelled out rather than called <c>ExpiresAt</c> like the field on <c>/bff/auth/me</c>, because
    /// two expiry rules are in force and a bare name cannot say which one it means. The effective
    /// deadline is the EARLIER of this and <see cref="InactivityExpiresAt"/>; a client that watches
    /// only one of them is wrong half the time. Nothing warned about this rule at all until now.
    /// </remarks>
    public DateTime? AbsoluteExpiresAt { get; set; }
}
