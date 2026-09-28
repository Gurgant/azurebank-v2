namespace AzureBank.Api.Services;

/// <summary>
/// A grant just issued: its plaintext, shown to the caller this once and never stored, and its fixed
/// expiry, which login and registration return as <c>refreshTokenExpiresAt</c> (06 §4.1).
/// </summary>
public sealed record IssuedGrant(string RefreshToken, DateTime ExpiresAt);
