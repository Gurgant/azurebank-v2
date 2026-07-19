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
    public required string FirstName
    {
        // Trim in the setter so validation (DataAnnotations + FluentValidation) sees the
        // NORMALISED value. Otherwise "  a  " passes the raw {2,50} length rule but trims to a
        // 1-char name below the guarantee. Whitespace-only becomes "" and is rejected by
        // [Required]/NotEmpty upstream.
        get => _firstName;
        set => _firstName = value?.Trim() ?? string.Empty;
    }
    private string _firstName = string.Empty;

    [Required]
    [StringLength(ValidationRules.LastNameMaxLength, MinimumLength = ValidationRules.LastNameMinLength, ErrorMessage = ValidationRules.LastNameLengthMessage)]
    [RegularExpression(ValidationRules.NamePattern, ErrorMessage = ValidationRules.NamePatternMessage)]
    public required string LastName
    {
        get => _lastName;
        set => _lastName = value?.Trim() ?? string.Empty; // normalise before validation (see FirstName)
    }
    private string _lastName = string.Empty;
}
