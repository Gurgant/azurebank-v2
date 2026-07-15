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
    public TokenResult GenerateToken(ApplicationUser user)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expiresAt = DateTime.UtcNow.AddMinutes(_options.ExpirationMinutes);

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
