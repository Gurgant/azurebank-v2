using AzureBank.Shared.Constants;
using AzureBank.Shared.Validation;
using System.ComponentModel.DataAnnotations;

namespace AzureBank.Shared.DTOs.Transaction;

public class WithdrawRequest
{
    [Required]
    [NotEmptyGuid(ErrorMessage = "A valid account ID is required.")]
    public Guid AccountId { get; set; }

    [Required]
    [MoneyRange]
    public decimal Amount { get; set; }

    [Required]
    [Pin]
    public required string Pin { get; set; }

    [MaxLength(ValidationRules.TransactionDescriptionMaxLength, ErrorMessage = ValidationRules.DescriptionMaxLengthMessage)]
    public string? Description { get; set; }
}
