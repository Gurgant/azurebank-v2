using AzureBank.Shared.Entities;

namespace AzureBank.Api.Services;

/// <summary>
/// Outcome of a successful renewal: the owning user, so the caller can mint an access token without
/// a second lookup, and when the grant expires, so that token can be capped at it (ADR-0057 §4.1).
/// No refresh token: the grant presented stays the grant (ADR-0057 §4.3). <i>(Until PR-1 this was
/// <c>RefreshRotationResult</c> and carried the rotated successor.)</i>
/// </summary>
public sealed record RenewResult(ApplicationUser User, DateTime GrantExpiresAt);
