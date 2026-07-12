namespace AzureBank.Shared.DTOs.User;

public class UserResponse
{
    public required Guid UserId { get; set; }
    public required string AzureTag { get; set; }
    public required string Email { get; set; }
    public required string FirstName { get; set; }
    public required string LastName { get; set; }


}
