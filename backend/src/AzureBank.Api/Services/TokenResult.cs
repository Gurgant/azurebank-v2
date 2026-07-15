namespace AzureBank.Api.Services;

/// <summary>
/// An issued access token together with its exact expiry, so callers never
/// recompute the lifetime and cannot drift from the token's real <c>exp</c> claim
/// (ADR-0012).
/// </summary>
/// <param name="AccessToken">The signed JWT.</param>
/// <param name="ExpiresAt">The token's expiry (UTC), read back from its <c>exp</c> claim.</param>
public sealed record TokenResult(string AccessToken, DateTime ExpiresAt);
