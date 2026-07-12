using AzureBank.Shared.Constants;
using System.ComponentModel.DataAnnotations;

namespace AzureBank.Shared.Validation;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public class MoneyRangeAttribute : ValidationAttribute
{
    public override bool IsValid(object? value)
    {
        if (value is not decimal amount)
        {
            return false;
        }
        return amount >= ValidationRules.TransactionMinAmount
            && amount <= ValidationRules.TransactionMaxAmount;

    }

    public override string FormatErrorMessage(string name) =>
        $"{name} must be between {ValidationRules.TransactionMinAmount:C} and " +
        $"{ValidationRules.TransactionMaxAmount:C}";
}
