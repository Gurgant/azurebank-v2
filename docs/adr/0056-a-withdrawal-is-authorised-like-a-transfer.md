# ADR-0056: A withdrawal is authorised like a transfer

**Status:** Accepted · **Date:** 2026-09-21 · **Amended:** 2026-09-22 (the SPA mints) ·
**Extends:** ADR-0042 (the rail), ADR-0049 (account closures), ADR-0050 (what a mint does not check)

## Context

Before this decision a withdrawal was the last operation that carried its PIN in the request body:
ADR-0042 took transfers off that shape and ADR-0049 account closures. A PIN in the body costs three
things the step-up rail already solves: it proves that someone knew the PIN, not that they
authorised this movement; the secret travels inside the bytes the idempotency fingerprint is taken
over, so a retry cannot change it; and it is proved before the money is checked. The last was the
defect: an over-balance withdrawal with a mistyped PIN answered 401 `INVALID_PIN` and spent one of
the three attempts that lock the PIN for fifteen minutes (ADR-0010), when the answer owed was "you
do not have that much", which costs nothing to give.

## Decision

- **D1: The withdrawal joins the step-up rail.** `StepUpOperation.Withdrawal = 3` is stored as the
  string `"Withdrawal"` in the `nvarchar(30)` column its siblings use, so no migration is required.
- **D2: The binding is the account and the amount, with no counterparty**
  (`StepUpBinding.ForWithdrawal(accountId, amount)`), because a withdrawal moves a real sum and has
  no payee: the amount is the only field besides the account a re-presentation could profitably
  change. One minted for 10 and presented against 20 is refused 401 `AUTHORIZATION_INVALID`.
- **D3: The mint is `POST /api/transactions/withdraw/authorizations`**, the transfer mints' route
  shape, because a bare `authorizations` would hang off a controller that also serves deposits and
  history. It has no `[RequireIdempotency]`, because a mint creates nothing a caller can be charged
  for, only one of two mints can be spent, and it must be easy to call again after a wrong PIN.
- **D4: The funds guard runs ahead of the authorisation check.** The withdrawal's order is ownership
  (404/403), funds (422), header absent (401 `AUTHORIZATION_REQUIRED`), binding (401
  `AUTHORIZATION_INVALID`), then the transaction, because the affordability answer then arrives
  without the caller proving anything, and the PIN is consulted only at the mint.
- **D5: The mint does not check the balance** (ADR-0050 D4), because a mint is an authentication
  event, the balance is a racing value the withdrawal re-reads inside its own transaction, and a
  refusal at the mint would teach a caller the balance at no cost. Authorising more than the account
  holds returns 201.
- **D6: The withdrawal runs in an explicit transaction, opened through the execution strategy, and
  the consume happens after the save**, so a zero-row consume rolls the withdrawal back, because
  `ConsumeAsync` is a separate `ExecuteUpdate`: with no caller transaction a withdrawal could commit
  with its authorisation still pending, spendable twice, or burn one on a withdrawal never made.
- **D7: `consumedByTransactionId` is the ledger row, not null**, because a withdrawal produces a
  movement and the evidence verb joins the authorisation to it on that id. The success row also
  names the authorisation in its hashed `Detail` (`AuditDetails.ConsumedAuthorisation`): the two
  agreeing is what makes a withdrawal strongly authenticated, and disagreement is a finding.
- **D8: A refused PIN at this mint writes `MoneyWithdrawalRefused`.** The mapping from operation to
  refusal event is a switch, held by a test that walks `Enum.GetValues<StepUpOperation>()`, because
  a two-armed choice files a refused withdrawal PIN as a refused transfer and no compiler objects.

**Error codes, in the order they can fire.**

- Withdraw: 400 `IDEMPOTENCY_KEY_MISSING`; 400 model-state (`Step-Up-Authorization` not a UUID); 404
  `ACCOUNT_NOT_FOUND`; 422 `INSUFFICIENT_FUNDS`, before any authorisation; 401
  `AUTHORIZATION_REQUIRED` (header absent or empty); 401 `AUTHORIZATION_INVALID` (unknown, already
  spent, or another user's); 401 `AUTHORIZATION_EXPIRED` (past its two-minute window); 401
  `AUTHORIZATION_INVALID` (wrong binding: expiry is checked first).
- Mint: 404 `ACCOUNT_NOT_FOUND`, which costs no PIN attempt; 422 `PIN_REQUIRED` (no PIN enrolled);
  401 `INVALID_PIN`; 429 `PIN_LOCKED`, with `Retry-After`.

## Rejected

- Rejected: keeping the PIN in the withdrawal's body, because of the three costs in Context.
- Rejected: a funds check at the mint, because it is a second place for the two checks to disagree.
- Rejected: converting the transfers' four inline `new StepUpBinding(...)` sites with this change,
  because it is a refactor with its own blast radius. Four of eight construction sites stay inline.

## Consequences

- Before → After, measured: over balance with a wrong PIN answered 401 `INVALID_PIN`; it answers 422
  `INSUFFICIENT_FUNDS` and spends no attempt. A withdrawal with no header answers 401
  `AUTHORIZATION_REQUIRED`, and a leftover `pin` beside a valid header is ignored: 201.
- No monetary body carries a PIN, and the PIN rails are two, not three. The request fingerprint
  stays a keyed digest, because every body is low-entropy and mostly known (ADR-0009).
- The withdraw dialog mints when WITHDRAW is pressed and sends the reference in
  `Step-Up-Authorization`. The PIN does not rotate the idempotency key, and a retry on a retained
  key presents the same authorisation again, because the first attempt may have consumed it.
- The contract suite withdraws through `withdrawViaMint` (`client.ts`). The empty-account probe in
  `money.contract.test.ts` sends a bare withdrawal on purpose: it asserts D4's order.
- `pinLockExpiry.spec.ts` drives the change-PIN dialog: its subject is `RetryCountdown` against a
  lock the server issued, and that dialog needs no minted authorisation and no throwaway account.
- The evidence verb's `NO AUTHORISATION APPLIES` against a withdrawal is a finding (ADR-0044). The
  money bound is published on seven request schemas (ADR-0046), and the scale rule is wired into
  all seven money endpoints, so no mint is stricter than the movement it authorises.

## Revisit when

- A withdrawal gains a counterparty: `ForWithdrawal`'s two null fields become wrong.
- The balance stops being a racing value: a funds check at the mint.
- The two-minute window proves too short for a real cash flow: it is `StepUpOptions.Window`, and by
  design not extendable per authorisation.

## Verified by

- `WithdrawalStepUpSqlServerTests`: single use under concurrency, a rolled-back consume, a zero-row
  consume, and a transient that must not withdraw twice; it asserts the balance and the ledger rows.
- `AuditTrailPersistenceTests` (D7: the two halves agree), `SecurityEventConstantTests` (D8).

## Related

ADR-0009, ADR-0010, ADR-0042, ADR-0044, ADR-0046, ADR-0049, ADR-0050.
