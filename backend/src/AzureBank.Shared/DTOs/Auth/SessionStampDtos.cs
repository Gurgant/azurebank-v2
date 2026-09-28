using System.ComponentModel.DataAnnotations;

namespace AzureBank.Shared.DTOs.Auth;

/// <summary>
/// Request body for POST /api/auth/session-stamps: the users who hold a session in the BFF.
/// </summary>
public class SessionStampsRequest
{
    /// <summary>The most users one call reads.</summary>
    /// <remarks>
    /// The same bound as <see cref="RevokeRequest.MaxRefreshTokens"/>, and for its reason: far above
    /// the users one BFF replica holds sessions for, and a caller holding more sends several calls.
    /// </remarks>
    public const int MaxUserIds = 1000;

    /// <summary>
    /// The users whose session stamps to read. An unknown user is not an error: the answer simply
    /// has no entry for it.
    /// </summary>
    // Length, not MinLength and MaxLength, for the reason RevokeRequest gives: it publishes minItems
    // and maxItems on the array.
    [Required]
    [Length(1, MaxUserIds)]
    public required IReadOnlyList<Guid> UserIds { get; set; }
}

/// <summary>What POST /api/auth/session-stamps answers: one entry per known user asked about.</summary>
/// <remarks>
/// A wrapper rather than a bare list in the envelope, measured 2026-09-28: the frontend's
/// <c>generate:zod</c> (typed-openapi 3.0.1, whose CLI tree-shakes component schemas by default)
/// follows the items of an array but not of a nullable one, and the envelope's <c>data</c> is
/// nullable. With <c>ApiResponse&lt;List&lt;T&gt;&gt;</c> it emitted the list's schema and dropped
/// <c>T</c>, leaving a reference to a schema that was never declared. This property is a required,
/// non-nullable array, which it follows.
/// </remarks>
public class SessionStampsResponse
{
    /// <summary>The stamps, in no particular order. An unknown user has no entry.</summary>
    public required IReadOnlyList<UserSessionStamp> Stamps { get; set; }
}

/// <summary>One user's session stamp.</summary>
public class UserSessionStamp
{
    /// <summary>The user.</summary>
    public required Guid UserId { get; set; }

    /// <summary>
    /// The user's current session stamp. A session that was given a lower one at sign-in has been
    /// signed out since.
    /// </summary>
    public required int SessionStamp { get; set; }
}
