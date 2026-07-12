using AzureBank.Shared.Constants;
using AzureBank.Shared.Validation;
using System.ComponentModel.DataAnnotations;

namespace AzureBank.Shared.DTOs.Auth;

public class RegisterRequest
{
    [Required]
    [AzureTag]
    public required string AzureTag { get; set; }

    [Required]
    [EmailAddress]
    [StringLength(ValidationRules.EmailMaxLength)]
    public required string Email { get; set; }

    [Required]
    [Password]
    public required string Password { get; set; }

    [Required]
    [StringLength(ValidationRules.FirstNameMaxLength, MinimumLength = ValidationRules.FirstNameMinLength, ErrorMessage = ValidationRules.FirstNameLengthMessage)]
    [RegularExpression(ValidationRules.NamePattern, ErrorMessage = ValidationRules.NamePatternMessage)]
    public required string FirstName { get; set; }

    [Required]
    [StringLength(ValidationRules.LastNameMaxLength, MinimumLength = ValidationRules.LastNameMinLength, ErrorMessage = ValidationRules.LastNameLengthMessage)]
    [RegularExpression(ValidationRules.NamePattern, ErrorMessage = ValidationRules.NamePatternMessage)]
    public required string LastName { get; set; }
}
