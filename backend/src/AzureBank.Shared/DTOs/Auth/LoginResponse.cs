namespace AzureBank.Shared.DTOs.Auth;

public class LoginResponse
{
    public required string Token { get; set; }
    public required DateTime ExpiresAt { get; set; }
    public required UserLoginInfo User { get; set; }
}
