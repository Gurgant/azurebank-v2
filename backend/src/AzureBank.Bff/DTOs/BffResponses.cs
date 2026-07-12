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
}
