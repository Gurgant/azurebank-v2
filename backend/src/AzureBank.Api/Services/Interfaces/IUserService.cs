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
    Task<UserResponse> GetUserByIdAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks up a user by AzureTag for transfer recipient verification.
    /// Returns masked display name for privacy. Only a user of the caller's own demo copy is
    /// found; outside the demo no user belongs to a copy and every user finds every other.
    /// </summary>
    /// <param name="azureTag">The AzureTag to look up</param>
    /// <param name="currentUserId">Current user ID (to exclude from results; its demo copy bounds them)</param>
    /// <param name="cancellationToken">Cancels the work: the request's token, which its deadline or the caller hanging up cancels (ADR-0058).</param>
    Task<RecipientLookupResponse> GetUserByAzureTagAsync(
        string azureTag, Guid currentUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renames the caller's own public AzureTag handle (ADR-0015). Throws
    /// <see cref="Shared.Exceptions.ConflictException"/> if the new handle is already taken.
    /// </summary>
    /// <returns>The new, normalised AzureTag.</returns>
    Task<string> RenameAzureTagAsync(Guid userId, string newAzureTag, CancellationToken cancellationToken = default);
}
