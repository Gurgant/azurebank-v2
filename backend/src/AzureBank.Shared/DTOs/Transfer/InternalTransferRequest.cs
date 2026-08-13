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

    /// <summary>
    /// The step-up PIN, carried IN-BAND so the API can refuse the transfer on its own.
    ///
    /// <para>
    /// Until ADR-0041 the only step-up check for a transfer lived in the BFF's AuthLevelMiddleware,
    /// as a session flag. A call made straight to the API — which is reachable, and whose JWT is
    /// obtainable from a [AllowAnonymous] login in every environment — carried no second factor at
    /// all and simply succeeded. Withdraw never had that hole, because its PIN travels here.
    /// </para>
    /// </summary>
    [Required]
    [Pin]
    public required string Pin { get; set; }

    [MaxLength(ValidationRules.TransactionDescriptionMaxLength, ErrorMessage = ValidationRules.DescriptionMaxLengthMessage)]
    public string? Description { get; set; }
}
