using AzureBank.Shared.Constants;
using AzureBank.Shared.Validation;
using System.ComponentModel.DataAnnotations;

namespace AzureBank.Shared.DTOs.Transfer;

public class InternalTransferRequest
{
    [Required]
    [NotEmptyGuid(ErrorMessage = ValidationRules.AccountNotEmptyGuid)]
    public Guid FromAccountId { get; set; }

    [Required]
    [NotEmptyGuid(ErrorMessage = ValidationRules.AccountNotEmptyGuid)]
    public Guid ToAccountId { get; set; }

    [Required]
    [MoneyRange]
    public decimal Amount { get; set; }

    [MaxLength(ValidationRules.TransactionDescriptionMaxLength, ErrorMessage = ValidationRules.DescriptionMaxLengthMessage)]
    public string? Description { get; set; }
}
