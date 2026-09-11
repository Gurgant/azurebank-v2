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
      travel as numeric extension members, which the client formats itself (fixed en-IE). The
      member names follow the available/requested precedent — undeclared in the published component
      and, like `available`/`requested`, not yet typed on the client (the SPA's hand-written
      ApiProblem carries neither today; the frontend mirror PR adds these four to it) — and
      `remaining` is deliberately not sent, so the client (once it types the members) derives it as
      limit − used and a fourth number cannot become a second source of truth.

      ⚠️ Noted 2026-09-07 (review round 1): THE FOUR ARE NOW DECLARED IN THE DOCUMENT, on the
      INLINE 422 schemas of POST /api/transfers and POST /api/transfers/authorizations
      (BusinessRulesDocumentTransformer.DeclareDailyLimitMembers), and typed in schema.d.ts. The
      paragraph above survives only in its narrow readings — the shared ProblemDetails COMPONENT
      still declares none of them, and the SPA's hand-written ApiProblem still carries none — but
      the "precedent" it cites was a silence rather than a decision: `available` appears zero times
      in the committed document (ADR-0050 D7, dated note). `available` / `requested` do remain
      undeclared everywhere, which is its own small PR.

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
