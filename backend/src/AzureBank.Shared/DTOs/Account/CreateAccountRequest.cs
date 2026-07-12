using AzureBank.Shared.Constants;
using AzureBank.Shared.Enums;
using System.ComponentModel.DataAnnotations;

namespace AzureBank.Shared.DTOs.Account;

public class CreateAccountRequest
{
    [Required]
    [StringLength(ValidationRules.AccountNameMaxLength, MinimumLength = ValidationRules.AccountNameMinLength, ErrorMessage = ValidationRules.AccountNameLengthMessage)]
    public required string Name { get; set; }
    public required AccountType Type { get; set; } // can delete required as enum has default value: AccountType.Checking
}
