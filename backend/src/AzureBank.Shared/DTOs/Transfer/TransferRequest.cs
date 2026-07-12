using AzureBank.Shared.Constants;
using AzureBank.Shared.Validation;
using System.ComponentModel.DataAnnotations;

namespace AzureBank.Shared.DTOs.Transfer;

public class TransferRequest
{
    [Required]
    [NotEmptyGuid(ErrorMessage = ValidationRules.AccountNotEmptyGuid)]
    public Guid FromAccountId { get; set; }

    [Required]
    [AzureTag]
    public required string RecipientAzureTag { get; set; }

    [Required]
    [MoneyRange]
    public decimal Amount { get; set; }

    [MaxLength(ValidationRules.TransactionDescriptionMaxLength, ErrorMessage = ValidationRules.DescriptionMaxLengthMessage)]
    public string? Description { get; set; }
}
