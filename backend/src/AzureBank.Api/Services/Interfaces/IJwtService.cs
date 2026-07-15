using AzureBank.Api.Services;
using AzureBank.Shared.Entities;

namespace AzureBank.Api.Services.Interfaces;

public interface IJwtService
{
    TokenResult GenerateToken(ApplicationUser user);
    (bool IsValid, Guid UserId) ValidateToken(string token);
}
