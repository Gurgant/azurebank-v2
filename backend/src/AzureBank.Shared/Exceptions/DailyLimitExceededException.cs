using AzureBank.Shared.Constants;

namespace AzureBank.Shared.Exceptions;

/// <summary>
/// The day's ceiling on outgoing external transfers would be exceeded (ADR-0050). 422, with the
/// figures as extension members rather than in the sentence.
/// </summary>
public class DailyLimitExceededException : BusinessRuleException
{
    /*
      No figures in the sentence, for the reason InsufficientFundsException gives: the numbers
      travel as numeric extension members and the client formats them (fixed en-IE). The four are
      declared on the 422 of POST /api/transfers and POST /api/transfers/authorizations
      (BusinessRulesDocumentTransformer.DeclareRefusalMembers) and typed in the SPA's ApiProblem.
      `remaining` is not sent: the client derives it as limit − used, so a fourth number cannot
      become a second source of truth.

      `resetsAt` is the start of the next UTC day, as a UTC instant: the window is a calendar day,
      and the client should not have to know that to say when it reopens.
    */
    public DailyLimitExceededException(decimal limit, decimal used, decimal requested, DateTime resetsAt)
        : base("Daily transfer limit exceeded.", ErrorCodes.DailyLimitExceeded)
    {
        Details = new Dictionary<string, object>
        {
            { "limit", limit },
            { "used", used },
            { "requested", requested },
            { "resetsAt", resetsAt }
        };
    }
}
