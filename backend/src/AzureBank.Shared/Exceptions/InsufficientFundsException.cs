using AzureBank.Shared.Constants;

namespace AzureBank.Shared.Exceptions;


public class InsufficientFundsException : BusinessRuleException
{
    public InsufficientFundsException(decimal available, decimal requested)
        : base($"Insufficient funds. Available: {available:C}, Requested: {requested:C}",
               ErrorCodes.InsufficientFunds)
    {
        Details = new Dictionary<string, object>
        {
            { "available", available },
            { "requested", requested }
        };
    }
}