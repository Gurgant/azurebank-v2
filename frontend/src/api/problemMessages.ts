/**
 * What to tell a user when the failure was decided by the TRANSPORT, not by the server.
 *
 * `problemBaseQuery` synthesises `status: 'NETWORK'` when the request never completed and
 * `'PARSE'` when the answer was not readable — neither has an `errorCode`, because no server ever
 * chose one. Five call sites independently hand-typed the same sentence for that case (the two
 * money dialogs, both transfer surfaces via `classifyMoneyProblem`, and PIN setup), which is the
 * drift this codebase keeps paying for: identical copy that a wording, tone or a11y change has to
 * find in five places and will eventually find in four.
 *
 * Deliberately NOT a lookup table keyed by status. The point is one sentence with one owner, not a
 * dispatcher; a second transport case would earn its own export here, named for what it says.
 *
 * `StepUpModal` keeps its own wording — "Couldn't verify right now" — and that is not drift: it is
 * the only surface where the request that failed was a verification rather than the operation the
 * user asked for, so naming the operation would misdescribe what did not happen.
 */
export const CONNECTION_FAILED = "Couldn't reach the server — check your connection and try again.";
