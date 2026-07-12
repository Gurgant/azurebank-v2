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

    /// <summary>
    /// Maps ApplicationUser to RecipientSearchResult for transfer recipient lookup.
    /// Display name is masked (First name + last initial) for privacy.
    /// </summary>
    public RecipientSearchResult ToSearchResult(ApplicationUser entity)
    {
        return new RecipientSearchResult
        {
            AzureTag = entity.AzureTag,
            DisplayName = GetMaskedDisplayName(entity)
        };
    }

    /// <summary>
    /// Maps list of ApplicationUser to list of RecipientSearchResult.
    /// </summary>
    public List<RecipientSearchResult> ToSearchResultList(List<ApplicationUser> entities)
    {
        return entities.Select(ToSearchResult).ToList();
    }

    /// <summary>
    /// Creates RecipientLookupResponse for transfer recipient verification.
    /// </summary>
    public RecipientLookupResponse ToLookupResponse(ApplicationUser? entity, string azureTag)
    {
        return new RecipientLookupResponse
        {
            AzureTag = azureTag,
            DisplayName = entity != null ? GetMaskedDisplayName(entity) : string.Empty,
            Exists = entity != null
        };
    }

    /// <summary>
    /// Gets masked display name: "John D." instead of "John Doe".
    /// </summary>
    private static string GetMaskedDisplayName(ApplicationUser entity)
    {
        if (string.IsNullOrEmpty(entity.LastName))
            return entity.FirstName;

        return $"{entity.FirstName} {entity.LastName[0]}.";
    }
}
