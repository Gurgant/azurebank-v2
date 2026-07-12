using AzureBank.Shared.Constants;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace AzureBank.Shared.Validation;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public class PasswordAttribute : ValidationAttribute
{
#pragma warning disable SYSLIB1045
    private static readonly Regex Pattern = new(
        ValidationRules.PasswordPattern,
        RegexOptions.Compiled);
#pragma warning restore SYSLIB1045

    public override bool IsValid(object? value)
    {
        if (value is not string password)
        {
            return true;  // Let [Required] handle null
        }

        if (password.Length < ValidationRules.PasswordMinLength ||
            password.Length > ValidationRules.PasswordMaxLength)
        {
            return false;
        }

        return Pattern.IsMatch(password);
    }

    public override string FormatErrorMessage(string name) =>
        $"{name} must be {ValidationRules.PasswordMinLength}-{ValidationRules.PasswordMaxLength} characters " +
        $"and {ValidationRules.PasswordPatternMessage}";
}
