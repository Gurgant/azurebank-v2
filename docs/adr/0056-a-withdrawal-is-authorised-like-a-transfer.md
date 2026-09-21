# ADR-0056: A withdrawal is authorised like a transfer

**Status:** Accepted · **Date:** 2026-09-21 · **Supersedes nothing** · **Extends** ADR-0042 (the
rail), ADR-0049 (its first non-money operation), ADR-0050 (what a mint does not check)

## Context

A withdrawal was the last operation on this API that carried its PIN in the request body. ADR-0042
took transfers off that shape in August and ADR-0049 took account closures off it in September; the
withdrawal stayed, and SECURITY.md described the result as "a PIN for every move of money … on
three rails", the third of which existed for this endpoint alone.

Carrying the PIN in the body is not merely untidy. It costs three things the rail already solved:
the PIN proves only that someone knew the PIN rather than that they authorised THIS movement; the
secret travels inside the bytes the idempotency fingerprint is taken over, so a retry cannot change
it; and the PIN is proved before the money is checked, which is the defect below.

## POST /api/transactions/withdraw BEFORE — 2026-09-21 — API :7215, main `a02a298`

Full transcript with its provenance in the working-state repo,
`plans/2026-09-21-item34-item28/evidence-withdraw-before-2026-09-21.txt`. The rows that matter:

```
withdraw 10, correct PIN                       201  errorCode='ok'
withdraw 1000 (over balance), correct PIN      422  errorCode='INSUFFICIENT_FUNDS'
withdraw 1000 (over balance), WRONG PIN        401  errorCode='INVALID_PIN'
withdraw 10, NO pin field                      400  errorCode=None
withdraw on a foreign account id               404  errorCode='ACCOUNT_NOT_FOUND'
```

**Row three is the decision's reason.** A customer who mistyped their PIN on a withdrawal they
could never afford was told the PIN was wrong, and it cost them one of three attempts — three of
which lock the PIN for fifteen minutes (ADR-0010). The answer they were owed was "you do not have
that much", and it costs nothing to give.

## Decision

**D1 — The withdrawal joins the step-up rail.** `StepUpOperation.Withdrawal = 3`, stored as the
string `"Withdrawal"` in an `nvarchar(30)` column that already holds its siblings
(`HasConversion<string>()`), so **no migration is required** — verified rather than assumed, and
worth stating because no CI job checks for a missing one.

**D2 — The binding is the account and the amount, with no counterparty.**
`StepUpBinding.ForWithdrawal(accountId, amount)` = `(accountId, null, null, amount)`. A closure
renders its amount 0 (ADR-0049 D3) because it moves none; a withdrawal moves a real sum and has no
payee, which makes the amount the only field besides the account that a re-presentation could
profitably change. Measured: an authorisation minted for 10 and presented against 20 is refused
401 `AUTHORIZATION_INVALID`.

**D3 — The mint is `POST /api/transactions/withdraw/authorizations`,** on the transfer mints'
route shape (operation path + `/authorizations`) rather than a bare `authorizations` hanging off a
controller that also serves deposits and history. No `[RequireIdempotency]`, for the reason on the
other three mints: minting creates nothing a caller can be charged for, only one of two mints can
ever be spent, and the endpoint's job is to be easy to call again after a wrong PIN.

**D4 — The funds guard runs AHEAD of the authorisation check.** Order on the withdrawal:
ownership (404/403) → funds (422) → header absent (401 `AUTHORIZATION_REQUIRED`) → binding
(401 `AUTHORIZATION_INVALID`) → the transaction. This is what closes the before-table's third row:
the affordability answer now arrives without the caller proving anything, and the PIN is only ever
consulted at the mint, for a withdrawal that could actually happen.

**D5 — The mint does NOT check the balance**, following ADR-0050 D4. A mint is an authentication
event, not a decision about whether the money can move. Balance is a racing value the withdrawal
re-reads inside its own transaction; refusing at the mint would teach a caller the balance at no
cost and add a second place for the two checks to disagree. **Consequence, stated because it is a
behaviour and not an oversight: asking to authorise more than the account holds returns 201.**

**D6 — The withdrawal gets an explicit transaction, which it never had.** ADR-0050 reserved this
restructure and this is it. The ledger row and its `MoneyWithdrawn` audit row already committed as
one unit (ADR-0044 D1), but `ConsumeAsync` is a separate `ExecuteUpdate`: with no caller
transaction it would autocommit beside that save, and a withdrawal could commit with its
authorisation still Pending — spendable twice — or burn one on a withdrawal that never committed.
The transaction is opened through the execution strategy, and the consume happens **after** the
save, so a zero-row consume rolls the withdrawal back.

**D7 — `consumedByTransactionId` is the ledger row, not null.** Unlike a closure, a withdrawal
produces a movement, and the evidence verb joins the authorisation to it on exactly that id. The
success row ALSO names the authorisation in its hashed `Detail`
(`AuditDetails.ConsumedAuthorisation`), so the unchained pointer and the chained name can be
checked against each other — agreement is what makes a withdrawal strongly authenticated, and
disagreement is a finding.

**D8 — A refused PIN at this mint writes `MoneyWithdrawalRefused`.** The mapping from operation to
refusal event was a **two-armed ternary over a three-member enum**
(`operation == AccountDeletion ? AccountDeletionRefused : MoneyTransferRefused`), so adding a
fourth member would have filed every refused withdrawal PIN as a refused TRANSFER — silently,
durably, with nothing failing. It is a switch now, and because an enum's domain is every `int` the
compiler cannot be made to catch a fifth member, a test walks `Enum.GetValues<StepUpOperation>()`
instead.

### Error codes, in the order they can fire

| Order | Endpoint | Status | errorCode | When |
|---|---|---|---|---|
| 1 | withdraw | 400 | `IDEMPOTENCY_KEY_MISSING` | no `Idempotency-Key` |
| 2 | withdraw | 400 | *(model-state)* | `Step-Up-Authorization` present but not a UUID |
| 3 | withdraw | 404 | `ACCOUNT_NOT_FOUND` | unknown or foreign account |
| 4 | withdraw | 422 | `INSUFFICIENT_FUNDS` | the balance does not cover it — **before any authorisation** |
| 5 | withdraw | 401 | `AUTHORIZATION_REQUIRED` | header absent or empty |
| 6 | withdraw | 401 | `AUTHORIZATION_INVALID` | wrong binding, already spent, or another user's |
| 7 | withdraw | 401 | `AUTHORIZATION_EXPIRED` | past its two-minute window |
| 8 | mint | 404 | `ACCOUNT_NOT_FOUND` | unknown or foreign account — **costs no PIN attempt** |
| 9 | mint | 422 | `PIN_REQUIRED` | no PIN enrolled |
| 10 | mint | 401 | `INVALID_PIN` | wrong PIN |
| 11 | mint | 429 | `PIN_LOCKED` | attempts exhausted, with `Retry-After` |

## Consequences

### Shipped here

`StepUpOperation.Withdrawal` and `StepUpBinding.ForWithdrawal`; the `WithdrawalAuthorizationRequest`
DTO and its validator; `AuthoriseWithdrawalAsync` and the restructured, transactional
`WithdrawAsync`; the mint action and `[RequireStepUpAuthorization]` on the withdrawal so the
transformer publishes the header as required; the switch that replaced the two-armed ternary and
the guard that walks the enum; the evidence verb widened in three places; the regenerated
`docs/api/openapiv1.json` (27 → 28 operations) and the regenerated frontend types; unit, integration
and architecture tests; the MSW mock realigned to the measured order; this record and the
corrections below.

### Before → After, measured

Full transcript: `plans/2026-09-21-item34-item28/evidence-withdraw-after-2026-09-21.txt`.

| Request | Before | After |
|---|---|---|
| withdraw 10, correct PIN in body | 201 | **401 `AUTHORIZATION_REQUIRED`** (the PIN is ignored) |
| mint 10, correct PIN | *(no endpoint)* | 201 |
| mint 5000 over a 100 balance | *(no endpoint)* | **201** — a mint checks no funds (D5) |
| withdraw 10 presenting its authorisation | *(n/a)* | 201 |
| withdraw 10 again, same authorisation | *(n/a)* | 401 `AUTHORIZATION_INVALID` |
| withdraw 20 with the one minted for 10 | *(n/a)* | 401 `AUTHORIZATION_INVALID` |
| **over balance, WRONG PIN** | **401 `INVALID_PIN`** | **422 `INSUFFICIENT_FUNDS`** (D4) |
| over balance, no authorisation | 422 | 422 `INSUFFICIENT_FUNDS` |
| empty `Step-Up-Authorization` | *(n/a)* | 401 `AUTHORIZATION_REQUIRED` |
| `Step-Up-Authorization: not-a-guid` | *(n/a)* | 400, model-state keyed on the header |
| foreign account id | 404 | 404 `ACCOUNT_NOT_FOUND` |
| mint, WRONG PIN | *(n/a)* | 401 `INVALID_PIN` |

### The interim `main` is stated, not hidden

Between this PR and the frontend follow-up, the SPA's withdraw dialog sends no header and is
answered 401 `AUTHORIZATION_REQUIRED`, which the dialog renders as the problem's `detail` — "This
withdrawal has not been authorised." It does **not** sign the user out, and that is measured rather
than assumed: `sessionMiddleware`'s `IN_FLOW_401_CODES` already contains `AUTHORIZATION_REQUIRED`
and routes on `errorCode`, **never on endpoint identity**, so it covers this endpoint with no
change. A stale `pin` left in the body is ignored, not refused — measured: a withdrawal carrying a
valid header and a leftover `pin` answered 201.

The SPA's real-stack contract suite is **not** left on the interim behaviour, the same decision
ADR-0049 took: `client.ts` gains `withdrawViaMint`, which mints and then presents, and the two
drains that used it now actually drain. One call site was deliberately **not** converted — the
empty-account probe in `money.contract.test.ts` still sends a bare withdrawal, because with D4 in
place that is what asserts the ordering on the real stack.

### What moved in other records, all struck in place with today's date

- `SECURITY.md` — "on three rails" and "a withdrawal carries the PIN in its request body". The
  body-PIN rail no longer exists; there are two.
- ADR-0009 and both idempotency sources — "the withdraw body contains a 6-digit PIN" was the stated
  reason the request fingerprint is a **keyed** digest. No monetary body carries one now. The
  digest stays keyed on the surviving reason: every body here is low-entropy and mostly known.
- ADR-0044's runbook — `NO AUTHORISATION APPLIES` listed a withdrawal among the movements that
  carry none. That verdict against a withdrawal is now a **finding**.
- ADR-0046 — the money bound is published on seven request schemas, not six. The two "six" figures
  inside its **rejected** option are deliberately left at six: they price that option as it was
  priced when it was rejected.
- ADR-0053 and `ci.yml` — the conformance floor moved 27 → 28. `CommittedOpenApiDocumentTests`'
  floor stays at **20**, deliberately below the real count, so deleting an endpoint does not trip it.
- `BusinessRulesDocumentTransformer` — the withdrawal's 422 named `PIN_REQUIRED` "checked before the
  balance". Both halves are now false.

### Not done, and not pretended otherwise

- **The SPA still does not mint.** PR-C replaces the PIN step with one that mints and submits with
  the header. Until then the dialog is answered 401 as above.
- **`pinLockExpiry.spec.ts` is RE-AIMED at the change-PIN dialog, not skipped and not deleted.**
  ~~It is not re-aimed; it drives the lock through the withdrawal, which no longer spends
  attempts.~~ ~~It is SKIPPED, because the state it asserts is unreachable until PR-C.~~
  *(Corrected twice before merge, both times by CI. First: "not re-aimed" implied it still ran, and
  run 35618553090 showed it waiting out its timeout on a 429 that never comes — the withdrawal
  earns no PIN attempts now, and the dialog does not mint yet. Then a `test.skip` with its reason
  written at the skip was refused by `scripts/assert-e2e-ran.mjs`, whose message names the only two
  remedies — make it run, or delete it — because two money-safety specs once skipped on every run
  while the job read green.*

  *The subject did not change with the endpoint. What is under test is `RetryCountdown` against a
  lock the SERVER issued: that it disables the entry, that it MOVES, and that it releases at zero.
  `ChangePinDialog`, `DeleteAccountDialog` and `WithdrawDialog` render that one component with the
  same banner, so any of the three proves it. Change PIN was chosen because it needs no minted
  authorisation and no throwaway ACCOUNT — a spare account created for the closure dialog could not
  be closed afterwards, since the PIN this spec deliberately locks is the same PIN its closure would
  need. PR-C may point it back at the withdrawal once that dialog mints; it does not have to.)*

- **The transfers' four inline `new StepUpBinding(...)` sites are left alone.** Adding a fourth
  factory leaves four of eight construction sites inline; converting them is a refactor with its own
  blast radius and belongs in its own change.
- **This ADR is not added to `AuditProseGuardTests`' policed corpus.** That corpus is a hand-written
  list of the audit-trail documents (ADR-0044, ADR-0045, the two runbooks, the deferred notes);
  ADR-0049, the direct precedent, is not in it either.

## What would change this

- A withdrawal gaining a counterparty — a payee, a destination — would make `ForWithdrawal`'s two
  null fields wrong and the binding under-specified.
- A funds check at the mint, if the balance ever stopped being a racing value.
- Evidence that the two-minute window is too short for a real cash flow; it is `StepUpOptions.Window`
  and is not extendable per authorisation by design.
