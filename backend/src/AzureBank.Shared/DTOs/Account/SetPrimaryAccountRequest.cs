using AzureBank.Shared.Constants;
using AzureBank.Shared.Validation;

namespace AzureBank.Shared.DTOs.Account;

public class SetPrimaryAccountRequest
{
    [NotEmptyGuid(ErrorMessage = ValidationRules.AccountNotEmptyGuid)]
    public Guid AccountId { get; set; }
}
