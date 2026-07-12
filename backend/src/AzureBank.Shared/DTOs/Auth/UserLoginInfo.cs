namespace AzureBank.Shared.DTOs.Auth;

/// <summary>
/// User info returned after successful login (nested in LoginResponse)
/// </summary>
public class UserLoginInfo
{
    public required Guid Id { get; set; }
    public required string AzureTag { get; set; }
    public required string Email { get; set; }
    public required string FirstName { get; set; }
    public required string LastName { get; set; }
    public bool HasPin { get; set; }
}
