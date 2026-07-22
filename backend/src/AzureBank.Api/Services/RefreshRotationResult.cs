using AzureBank.Shared.Entities;

namespace AzureBank.Api.Services;

/// <summary>
/// Outcome of a successful refresh-token rotation: the owning user (so the caller can mint a
/// fresh access token without a second lookup) and the NEW plaintext refresh token (shown once).
/// </summary>
public sealed record RefreshRotationResult(ApplicationUser User, string RefreshToken);
