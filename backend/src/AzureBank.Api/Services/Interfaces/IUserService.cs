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
    /// Searches for users by partial AzureTag match.
    /// Returns max 10 results with masked display names.
    /// Excludes the current user from results.
    /// </summary>
    /// <param name="azureTagQuery">Partial AzureTag to search (min 2 characters)</param>
    /// <param name="excludeUserId">User ID to exclude from results</param>
    Task<List<RecipientSearchResult>> SearchUsersAsync(string azureTagQuery, Guid excludeUserId);
}
