using AzureBank.Shared.Constants;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace AzureBank.Shared.Validation;

/// <summary>
/// Validation attribute for AzureTag query parameters.
///
/// Validates:
/// - Pattern: Must start with lowercase letter, followed by lowercase letters, numbers, or underscores
/// - Length: Between 3 and 20 characters
///
/// Note: Does NOT include [Required] - use [Required] separately if the parameter is mandatory.
///
/// Usage:
///   [FromQuery] [Required] [AzureTagQuery] string azureTag
///
/// Alternative: Add validation attributes directly to controller parameter:
///   [FromQuery]
///   [Required]
///   [RegularExpression(ValidationRules.AzureTagPattern, ErrorMessage = ValidationRules.AzureTagPatternMessage)]
///   [StringLength(ValidationRules.AzureTagMaxLength, MinimumLength = ValidationRules.AzureTagMinLength)]
///   string azureTag
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public class AzureTagQueryAttribute : ValidationAttribute
{
#pragma warning disable SYSLIB1045 // Can't use GeneratedRegex with const reference
    private static readonly Regex Pattern = new(
        ValidationRules.AzureTagPattern,
        RegexOptions.Compiled);
#pragma warning restore SYSLIB1045

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        // If null or empty, let [Required] handle it - we don't enforce required here
        if (value is not string tag || string.IsNullOrEmpty(tag))
        {
            return ValidationResult.Success;
        }

        // Validate length
        if (tag.Length < ValidationRules.AzureTagMinLength)
        {
            return new ValidationResult(
                $"AzureTag must be at least {ValidationRules.AzureTagMinLength} characters.",
                new[] { validationContext.MemberName ?? "azureTag" });
        }

        if (tag.Length > ValidationRules.AzureTagMaxLength)
        {
            return new ValidationResult(
                $"AzureTag cannot exceed {ValidationRules.AzureTagMaxLength} characters.",
                new[] { validationContext.MemberName ?? "azureTag" });
        }

        // Validate pattern
        if (!Pattern.IsMatch(tag))
        {
            return new ValidationResult(
                ValidationRules.AzureTagPatternMessage,
                new[] { validationContext.MemberName ?? "azureTag" });
        }

        return ValidationResult.Success;
    }

    public override string FormatErrorMessage(string name) =>
        ValidationRules.AzureTagPatternMessage;
}
