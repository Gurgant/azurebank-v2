using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.User;
using AzureBank.Shared.Entities;
using Riok.Mapperly.Abstractions;

namespace AzureBank.Api.Mappers;

/// <summary>
/// Mapperly-based mapper for ApplicationUser entity to DTO conversions.
/// Source generator - no runtime reflection overhead.
/// </summary>
/// <remarks>
/// Uses RequiredMappingStrategy.Target: only validates DTO properties are filled.
///
/// SECURITY: The following properties are NEVER exposed to DTOs (by design):
/// - PinHash, PasswordHash, SecurityStamp: Security-critical secrets
/// - All IdentityUser inherited properties: Internal system fields
///
/// Other unmapped entity properties:
/// - CreatedAt, UpdatedAt: Internal audit timestamps
/// - Accounts, FullName: Navigation / computed properties
/// </remarks>
[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public partial class UserMapper
{
    /// <summary>
    /// Maps ApplicationUser entity to UserResponse DTO.
    /// </summary>
    [MapProperty(nameof(ApplicationUser.Id), nameof(UserResponse.UserId))]
    public partial UserResponse ToResponse(ApplicationUser entity);

    /// <summary>
    /// Maps ApplicationUser entity to UserLoginInfo DTO with HasPin calculated.
    /// </summary>
    public UserLoginInfo ToLoginInfo(ApplicationUser entity)
    {
        return new UserLoginInfo
        {
            Id = entity.Id,
            AzureTag = entity.AzureTag,
            Email = entity.Email ?? string.Empty,
            FirstName = entity.FirstName,
            LastName = entity.LastName,
            HasPin = !string.IsNullOrEmpty(entity.PinHash)
        };
    }

}
