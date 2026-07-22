using System.ComponentModel.DataAnnotations;

namespace AzureBank.Shared.DTOs.Auth;

/// <summary>
/// Request body for POST /api/auth/refresh: exchanges a valid refresh token for a fresh
/// access + refresh token pair (rotation). The refresh token is the SOLE credential — no
/// bearer access token is required (the access token being refreshed may already be expired).
/// </summary>
public class RefreshRequest
{
    [Required]
    public required string RefreshToken { get; set; }
}
