using AzureBank.Shared.Constants;
using AzureBank.Shared.Validation;
using System.ComponentModel.DataAnnotations;

namespace AzureBank.Shared.DTOs.Auth;

public class LoginRequest
{
    [Required]
    [EmailAddress]
    [MaxLength(ValidationRules.EmailMaxLength)]
    public required string Email { get; set; }
    [Required]
    [Password]
    public required string Password { get; set; }
}
