using AzureBank.Shared.Constants;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace AzureBank.Shared.Validation;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public class PinAttribute : ValidationAttribute
{
#pragma warning disable SYSLIB1045
    private static readonly Regex Pattern = new(
        ValidationRules.PinPattern,
        RegexOptions.Compiled);
#pragma warning restore SYSLIB1045

    public override bool IsValid(object? value)
    {
        if (value is not string pin)
        {
            return true;
        }
        return Pattern.IsMatch(pin);
    }

    public override string FormatErrorMessage(string name) =>
        ValidationRules.PinPatternMessage;
}
