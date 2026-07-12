namespace AzureBank.Shared.DTOs.User;

/// <summary>
/// Response for recipient lookup by AzureTag.
/// Used for transfer recipient verification.
/// </summary>
public class RecipientLookupResponse
{
    /// <summary>
    /// The user's AzureTag
    /// </summary>
    public required string AzureTag { get; set; }

    /// <summary>
    /// Masked display name for privacy (e.g., "John D.")
    /// </summary>
    public required string DisplayName { get; set; }

    /// <summary>
    /// Whether the user exists in the system
    /// </summary>
    public bool Exists { get; set; }
}
