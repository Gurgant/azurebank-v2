using AzureBank.Shared.Constants;
using System.ComponentModel.DataAnnotations;

namespace AzureBank.Shared.DTOs.Account;

public class UpdateAccountRequest
{
    [Required(ErrorMessage = "Account name is required.")]
    [StringLength(ValidationRules.AccountNameMaxLength, MinimumLength = ValidationRules.AccountNameMinLength, ErrorMessage = ValidationRules.AccountNameLengthMessage)]
    public required string Name { get; set; }
}
