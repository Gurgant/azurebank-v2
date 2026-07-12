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
    /// When the session was created - for absolute timeout enforcement.
    /// </summary>
    public required DateTime SessionCreated { get; init; }

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
    /// Cached user information to avoid API calls on /bff/auth/me.
    /// </summary>
    public required UserSessionInfo UserInfo { get; init; }

    // Computed properties
    public bool IsTokenExpired => DateTime.UtcNow >= TokenExpiry;

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
    public required string AzureTag { get; init; }
    public bool HasPin { get; set; }
}
