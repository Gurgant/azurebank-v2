using AzureBank.Api.Services;
using AzureBank.Shared.Entities;

namespace AzureBank.Api.Services.Interfaces;

public interface IJwtService
{
    /// <summary>
    /// Mints an access token of <c>Jwt:ExpirationMinutes</c>, or one that expires at
    /// <paramref name="notAfter"/> when that is sooner (a renewal's grant expiry, 06 §4.1).
    /// </summary>
    TokenResult GenerateToken(ApplicationUser user, DateTime? notAfter = null);
    (bool IsValid, Guid UserId) ValidateToken(string token);
}
