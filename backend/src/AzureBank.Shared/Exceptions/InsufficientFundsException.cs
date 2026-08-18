using AzureBank.Shared.Constants;

namespace AzureBank.Shared.Exceptions;


public class InsufficientFundsException : BusinessRuleException
{
    /*
      NO FIGURES IN THE SENTENCE, deliberately. Both numbers already travel as the numeric
      extensions below, so the client has them and formats them in the user's own locale — which
      is the only place that knows it. The old message rendered them with `:C` against the server
      process culture, so the amounts a user was shown depended on how the container happened to
      start.
    */
    public InsufficientFundsException(decimal available, decimal requested)
        : base("Insufficient funds.", ErrorCodes.InsufficientFunds)
    {
        Details = new Dictionary<string, object>
        {
            { "available", available },
            { "requested", requested }
        };
    }
}