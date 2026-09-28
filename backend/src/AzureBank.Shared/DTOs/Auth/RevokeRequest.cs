using System.ComponentModel.DataAnnotations;
using AzureBank.Shared.Validation;

namespace AzureBank.Shared.DTOs.Auth;

/// <summary>
/// Request body for POST /api/auth/revoke: the grants of sessions the BFF has ended. One
/// for "Esci", idle expiry or a new sign-in; several when the BFF drains every session it holds on a
/// graceful stop.
/// </summary>
public class RevokeRequest
{
    /// <summary>The most grants one call revokes.</summary>
    /// <remarks>
    /// Each one becomes a parameter of one <c>UPDATE … WHERE TokenHash IN (…)</c>, and SQL Server takes
    /// at most 2,100 parameters in a command. A thousand is far above the sessions one BFF replica
    /// holds, and a drain holding more sends several calls.
    /// </remarks>
    public const int MaxRefreshTokens = 1000;

    /// <summary>
    /// The grants to revoke. An unknown one, or one already revoked, is not an error: the answer is
    /// the same 200 either way (RFC 7009 §2.2). A null one names no grant, and the request is refused
    /// with 400.
    /// </summary>
    // Length, not MinLength and MaxLength: the API's schema transformer reads those two as string
    // lengths, and published minLength and maxLength on an array. Length publishes minItems and
    // maxItems only, and counts the list at run time the same way.
    [Required]
    [Length(1, MaxRefreshTokens)]
    [NoNullItems]
    public required IReadOnlyList<string> RefreshTokens { get; set; }
}
