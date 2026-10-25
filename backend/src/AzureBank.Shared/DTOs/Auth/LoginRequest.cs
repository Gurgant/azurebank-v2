using AzureBank.Shared.Constants;
using System.ComponentModel.DataAnnotations;

namespace AzureBank.Shared.DTOs.Auth;

public class LoginRequest
{
    [Required]
    [EmailAddress]
    [MaxLength(ValidationRules.EmailMaxLength)]
    public required string Email { get; set; }
    // A password verified, not chosen, so no [Password]: a wrong one of any shape reaches the
    // credential check, answers the same 401 INVALID_CREDENTIALS and counts toward the lock
    // (ADR-0012). Only its length is bounded: no account holds a longer one.
    [Required]
    [MaxLength(ValidationRules.PasswordMaxLength)]
    public required string Password { get; set; }
}
