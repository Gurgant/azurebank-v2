using AzureBank.Shared.Constants;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace AzureBank.Shared.Validation;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public class AzureTagAttribute : ValidationAttribute
{
#pragma warning disable SYSLIB1045 // Can't use GeneratedRegex with const reference
    private static readonly Regex Pattern = new(
        ValidationRules.AzureTagPattern,
        RegexOptions.Compiled);
#pragma warning restore SYSLIB1045

    public override bool IsValid(object? value)
    {
        if (value is not string tag)
        {
            return true;  // Let [Required] handle null
        }
        return tag.Length >= ValidationRules.AzureTagMinLength
            && tag.Length <= ValidationRules.AzureTagMaxLength
            && Pattern.IsMatch(tag);
    }

    public override string FormatErrorMessage(string name) =>
        ValidationRules.AzureTagPatternMessage;
}
