using AzureBank.Shared.Constants;

namespace AzureBank.Shared.Exceptions;

/// <summary>
/// The day's ceiling on outgoing external transfers would be exceeded (ADR-0050). 422, with the
/// figures as extension members rather than in the sentence.
/// </summary>
public class DailyLimitExceededException : BusinessRuleException
{
    /*
      NO FIGURES IN THE SENTENCE, for the reason InsufficientFundsException gives: the numbers
      travel as numeric extension members, which the client formats in the user's own locale. The
      member names follow the available/requested precedent — undeclared in the published component
      and, like `available`/`requested`, not yet typed on the client (the SPA's hand-written
      ApiProblem carries neither today; the frontend mirror PR adds these four to it) — and
      `remaining` is deliberately not sent, so the client (once it types the members) derives it as
      limit − used and a fourth number cannot become a second source of truth.

      `resetsAt` is the start of the next UTC day, as a UTC instant: the window is a calendar day
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
