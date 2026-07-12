using AzureBank.Shared.Entities;

namespace AzureBank.Api.Services.Interfaces;

public interface IJwtService
{
    string GenerateToken(ApplicationUser user);
    (bool IsValid, Guid UserId) ValidateToken(string token);
}
