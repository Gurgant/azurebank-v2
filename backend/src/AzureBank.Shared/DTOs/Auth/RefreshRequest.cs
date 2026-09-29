using System.ComponentModel.DataAnnotations;

namespace AzureBank.Shared.DTOs.Auth;

/// <summary>
/// Request body for POST /api/auth/refresh: presents the session's grant for a fresh access token.
/// The grant is the SOLE credential — no bearer access token is required (the access token being
/// renewed may already be expired) — and it is not consumed: the same grant renews again until its
/// session ends or it expires.
/// </summary>
public class RefreshRequest
{
    [Required]
    public required string RefreshToken { get; set; }
}
