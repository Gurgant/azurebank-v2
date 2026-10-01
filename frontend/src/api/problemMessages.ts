/**
 * The SPA's own sentences for a failure, where the words cannot or must not be the server's.
 *
 * Two kinds live here. A failure the TRANSPORT decided has no server sentence at all:
 * `problemBaseQuery` synthesises `status: 'NETWORK'` when the request never completed and
 * `'PARSE'` when the answer was not readable, each with a code no server chose (`NETWORK_ERROR`,
 * `TIMEOUT_ERROR`, `PARSE_ERROR`). And the outage (below), where a server did answer and its
 * sentence is replaced.
 *
 * Deliberately NOT a lookup table keyed by status. The point is one sentence with one owner, not a
 * dispatcher; each case earns its own export here, named for what it says.
 */

/**
 * A request that never completed, or whose answer could not be read — but not one the SPA stopped
 * waiting for, which is the outage (`SERVICE_UNAVAILABLE`). `problemBaseQuery` makes it the
 * `detail` of a failed fetch, so no surface prints the runtime's own error.
 *
 * Five call sites independently hand-typed this sentence (the two money dialogs, both transfer
 * surfaces via `classifyMoneyProblem`, and PIN setup), which is the drift this codebase keeps
 * paying for: identical copy that a wording, tone or a11y change has to find in five places and
 * will eventually find in four.
 *
 * `StepUpModal` keeps its own wording — "Couldn't verify right now" — and that is not drift: it is
 * the only surface where the request that failed was a verification rather than the operation the
 * user asked for, so naming the operation would misdescribe what did not happen.
 */
export const CONNECTION_FAILED = "Couldn't reach the server — check your connection and try again.";

/*
  The outage, and the wait before it (ADR-0058, ADR-0059).

  A 503 is the service saying it cannot answer right now, and a request the SPA gave up on after
  65 s is the same thing seen from the other side, so both read as one sentence on every surface,
  whoever wrote the answer. The servers' own 503 sentences ("…Try again shortly.", "The session
  could not be renewed just now…") are never printed: one promises a time nobody measured, the
  other names a session to a visitor whose session is fine. The words differ only where the
  visitor's next step differs — whether money moved, whether a change was saved, whether pressing
  the same button again is safe.

  "…" is U+2026, as in the approved copy; apostrophes are ASCII.
*/

/** A wait's hint, 5 s after it starts. */
export const WAIT_SLOW = 'Taking longer than usual…';

/** The same hint from 20 s. */
export const WAIT_STILL_TRYING = 'Still trying…';

/**
 * `WAIT_STILL_TRYING` on a money send: a transfer, a move between the visitor's own accounts, a
 * deposit or a withdrawal. While it is pending the one unsafe thing the visitor can do is reload,
 * because the idempotency key lives only in this page and a reload sends the money with a new one.
 */
export const WAIT_KEEP_PAGE_OPEN = 'Still trying… Keep this page open.';

/**
 * Read, not shown, in the hint's own region when a read that said `WAIT_SLOW` or
 * `WAIT_STILL_TRYING` loads: a visitor who heard that it was slow is told that it is done. A load
 * that said nothing says nothing at its end either.
 */
export const WAIT_LOADED = 'Loaded.';

/** Beside `WAIT_STILL_TRYING` on a read the visitor can give up on. */
export const STOP_WAITING = 'Stop waiting';

/**
 * Only beside a control that re-sends the SAME idempotency key. Never during a wait: the one
 * "retry" possible then is a reload, and a reload sends a new key.
 */
export const NO_DOUBLE_CHARGE = "Retrying won't charge you twice.";

/** `NO_DOUBLE_CHARGE` for a move between the visitor's own accounts, where nothing is charged. */
export const NO_DOUBLE_MOVE = "Retrying won't move the money twice.";

/** Every 503's `detail`, and the detail of a request that had no answer in 65 s. */
export const SERVICE_UNAVAILABLE =
  'The service is temporarily unavailable. Please try again later.';

/** The heading of the page shown when the service is down before the app can start. */
export const SERVICE_UNAVAILABLE_TITLE = 'Temporarily unavailable';

/** Offered where the visitor's next step is to send the same thing again. */
export const TRY_AGAIN = 'Try again';

/**
 * A sign-out that failed with anything but a 401, before what went wrong where there is a
 * sentence for it. The session is alive behind the page, and a visitor who leaves a shared
 * computer believing otherwise leaves it signed in. A 401 means the session was already gone,
 * which is what a sign-out asks for.
 */
export const SIGN_OUT_FAILED = "We couldn't sign you out. You're still signed in.";

/**
 * A money send answered 503 with `applied: false`: the server says it changed nothing. Only that
 * answer earns these words; a 503 without it may have landed.
 */
export const SERVICE_UNAVAILABLE_NOTHING_CHANGED =
  'The service is temporarily unavailable, and nothing was changed. Please try again later.';

/**
 * A money send's authorisation (the PIN step) failed on an outage. No send had started, so no
 * money moved — but the PIN attempt may have been counted, so this does not say "nothing changed".
 */
export const SERVICE_UNAVAILABLE_NO_MONEY_MOVED =
  'The service is temporarily unavailable, and no money was moved. Please try again later.';

/** A deposit answered 503 without `applied`, or with no answer: the same key checks it. */
export const DEPOSIT_OUTCOME_UNKNOWN =
  "The service is temporarily unavailable, and we can't tell yet whether your deposit went through. Tap Deposit again to check.";

/**
 * A withdrawal answered 503 without `applied`, or with no answer: the same key checks it. The
 * dialog follows it with `NO_DOUBLE_CHARGE`, because its button re-sends that key.
 */
export const WITHDRAWAL_OUTCOME_UNKNOWN =
  "The service is temporarily unavailable, and we can't tell yet whether your withdrawal went through. Tap Withdraw again to check.";

/**
 * A change with no idempotency key (a rename, a PIN) on an outage: it may have been saved, and a
 * second attempt is a new request, not a check of the first.
 */
export const SAVE_OUTCOME_UNKNOWN =
  "The service is temporarily unavailable, and we can't tell yet whether your change was saved. Please try again later.";

/** Opening an account on an outage: it may exist, and trying again blind could open a second. */
export const ACCOUNT_OUTCOME_UNKNOWN =
  "The service is temporarily unavailable, and we can't tell yet whether your account was opened. Check your accounts before trying again.";
