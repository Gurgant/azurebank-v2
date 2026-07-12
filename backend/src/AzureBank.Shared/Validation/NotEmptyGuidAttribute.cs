using System.ComponentModel.DataAnnotations;

namespace AzureBank.Shared.Validation;

/// <summary>
/// Validates that a Guid is not empty (00000000-0000-0000-0000-000000000000)
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public class NotEmptyGuidAttribute : ValidationAttribute
{
    public NotEmptyGuidAttribute()
        : base("The {0} field must not be an empty GUID.")
    {
    }

    public override bool IsValid(object? value)
    {
        return value switch
        {
            Guid guid => guid != Guid.Empty,
            null => true,  // Let [Required] handle null for nullable Guid? types
            _ => false
        };
    }
}