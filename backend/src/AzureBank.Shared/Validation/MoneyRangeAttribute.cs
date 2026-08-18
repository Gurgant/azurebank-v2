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

    // This message is PUBLISHED, not merely shown: DataAnnotationSchemaTransformer copies it into
    // the OpenAPI description of every money field, so a culture-dependent render here would bake
    // whichever culture regenerated the spec into the committed contract.
    public override string FormatErrorMessage(string name) =>
        $"{name} must be between {ValidationRules.DescribeAmount(ValidationRules.TransactionMinAmount)} " +
        $"and {ValidationRules.DescribeAmount(ValidationRules.TransactionMaxAmount)}";
}
