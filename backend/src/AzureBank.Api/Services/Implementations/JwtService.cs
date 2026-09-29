using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Options;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AzureBank.Api.Services.Implementations;

/// <summary>
/// JWT token generation and validation service.
/// Uses HMAC-SHA256 for signing with configurable options.
/// </summary>
public class JwtService : IJwtService
{
    private readonly JwtOptions _options;
    private readonly ILogger<JwtService> _logger;

    public JwtService(IOptions<JwtOptions> options, ILogger<JwtService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Generates a JWT access token for the specified user, returning the token AND
    /// its exact expiry so callers never recompute the lifetime (no config drift, no
    /// UtcNow skew — the returned expiry is read back from the token's own exp claim).
    /// </summary>
    /// <param name="user">Whom the token is for.</param>
    /// <param name="notAfter">
    /// When given, the token expires then if that is sooner than its normal lifetime: a renewal
    /// passes its grant's expiry, so no access token outlives the grant it came from
    /// (ADR-0057 §4.1). The <c>exp</c> claim is whole seconds, truncated, so the token never ends
    /// even a fraction of a second after <paramref name="notAfter"/>.
    /// </param>
    public TokenResult GenerateToken(ApplicationUser user, DateTime? notAfter = null)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expiresAt = DateTime.UtcNow.AddMinutes(_options.ExpirationMinutes);
        if (notAfter is { } cap)
        {
            // A grant's expiry read back from SQL Server arrives as Unspecified, and the token
            // library converts an Unspecified value AS LOCAL TIME: on a machine two hours east of
            // UTC the token would end two hours early. Every DateTime this API stores is UTC.
            cap = cap.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(cap, DateTimeKind.Utc)
                : cap.ToUniversalTime();
            if (cap < expiresAt)
            {
                expiresAt = cap;
            }
        }

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            new Claim("azure_tag", user.AzureTag),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(JwtRegisteredClaimNames.Iat, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)
        };

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            expires: expiresAt,
            signingCredentials: credentials);

        var accessToken = new JwtSecurityTokenHandler().WriteToken(token);

        // token.ValidTo is the exp claim (whole seconds), i.e. the authoritative expiry.
        _logger.LogInformation("Generated JWT for user {UserId}, expires at {ExpiresAt}", user.Id, token.ValidTo);

        return new TokenResult(accessToken, token.ValidTo);
    }

    /// <summary>
    /// Validates a JWT token and extracts the user ID.
    /// </summary>
    public (bool IsValid, Guid UserId) ValidateToken(string token)
    {
        if (string.IsNullOrEmpty(token))
            return (false, Guid.Empty);

        var tokenHandler = new JwtSecurityTokenHandler();
        var key = Encoding.UTF8.GetBytes(_options.Secret);

        try
        {
            var principal = tokenHandler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = _options.Issuer,
                ValidAudience = _options.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(key),
                ClockSkew = TimeSpan.Zero
            }, out var validatedToken);

            var userIdClaim = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (Guid.TryParse(userIdClaim, out var userId))
            {
                return (true, userId);
            }

            _logger.LogWarning("Token validation failed: could not parse user ID from claims");
            return (false, Guid.Empty);
        }
        catch (SecurityTokenExpiredException)
        {
            _logger.LogInformation("Token validation failed: token expired");
            return (false, Guid.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Token validation failed: {Message}", ex.Message);
            return (false, Guid.Empty);
        }
    }
}
