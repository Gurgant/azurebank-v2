using AzureBank.Shared.DTOs.User;

namespace AzureBank.Api.Services.Interfaces;

/// <summary>
/// Service interface for user operations (profile, recipient lookup).
/// </summary>
public interface IUserService
{
    /// <summary>
    /// Gets a user by their ID.
    /// </summary>
    Task<UserResponse> GetUserByIdAsync(Guid userId);

    /// <summary>
    /// Looks up a user by AzureTag for transfer recipient verification.
    /// Returns masked display name for privacy.
    /// </summary>
    /// <param name="azureTag">The AzureTag to look up</param>
    /// <param name="currentUserId">Current user ID (to exclude from results)</param>
    Task<RecipientLookupResponse> GetUserByAzureTagAsync(string azureTag, Guid currentUserId);

    /// <summary>
    /// Renames the caller's own public AzureTag handle (ADR-0015). Throws
    /// <see cref="Shared.Exceptions.ConflictException"/> if the new handle is already taken.
    /// </summary>
    /// <returns>The new, normalised AzureTag.</returns>
    Task<string> RenameAzureTagAsync(Guid userId, string newAzureTag);
}
