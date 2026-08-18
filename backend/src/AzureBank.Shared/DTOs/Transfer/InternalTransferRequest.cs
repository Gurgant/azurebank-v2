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

    // No PIN here either, for the reasons set out on TransferRequest. An internal move mints at
    // POST /api/transfers/internal/authorizations and presents the reference in the
    // Step-Up-Authorization header.

    [MaxLength(ValidationRules.TransactionDescriptionMaxLength, ErrorMessage = ValidationRules.DescriptionMaxLengthMessage)]
    public string? Description { get; set; }
}
