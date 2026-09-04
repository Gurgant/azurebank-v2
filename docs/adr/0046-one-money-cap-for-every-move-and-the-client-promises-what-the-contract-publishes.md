# ADR-0046: One money cap for every move, and the client promises what the contract publishes

**Status:** Accepted · **Date:** 2026-09-03 · Closes the deposit-cap disagreement the balance-guard
audit found (T14 in the backlog): the server refused a deposit the form had accepted. Records the
first decision on money caps this repository has, and the shape that keeps a client bound from
drifting from the contract again. Supersedes nothing; corrects one code comment.

## Context

The server enforces one per-transaction bound on every money move — deposit, withdrawal, transfer,
internal transfer and both step-up authorisations — from a single constant,
`ValidationRules.TransactionMaxAmount` (100,000, in the product's currency), through `[MoneyRange]`
and a FluentValidation rule, and publishes it as `maximum: 100000.00` on all six request schemas in
`docs/api/openapiv1.json`. The generated frontend schemas carry it as `.max(100000)`. Nothing
inflow-specific exists on the server, in the document, or in any decision record.

The deposit form said otherwise. `moneySchemas.ts` carried `DEPOSIT_MAX = 1_000_000` with the copy
"Maximum deposit is €1,000,000." under a comment calling the higher inflow cap deliberate, so a
deposit of 500,000 passed the form and was refused by the API with a sentence the form then showed
verbatim. Traced: the comment was written in the React Hook Form rewrite (PR #36), whose body
describes the file as keeping every form's exact legacy copy; the figure came from the first
deposit dialog (PR #23, "the original DepositPage is the UX spec"), which took it from an original
UI file that contradicted the original's own contract schema (100,000, citing the OpenAPI maximum).
No ADR, plan, log entry or pull-request body ever decided 1,000,000. The backend suite pins it as
refused. The balance guard (PR #111) bounds an outflow by its source account's balance, and the
deposit form passes no balance because a deposit rightly has none; that is about the balance bound,
not the amount maximum, and reading it as support for a higher inflow cap is a category error.

**Measured on the running API, 2026-09-03.** Deposits of 500,000, 1,000,000 and 100,000.01 answer
`400` in the framework's model-state envelope, `{"Amount":["Amount must be between 0.01 EUR and
100000.00 EUR"]}`; 100,000 answers `201`; 99,999.99 answers `201`.

**The mock lied, and its tests agreed with it.** The MSW handlers quoted "Amount must be between
$0.01 and $100,000.00" as observed, and a mock test asserted that sentence. The server stopped
rendering currency symbols on 2026-08-18 (`3769dc9`); the frontend side of that change touched only
the generated types. For a fortnight the mock and its tests were green against a sentence the API no
longer sends — the exact failure the "real backend is the oracle" rule exists to name — because no
real-stack test pinned it.

**Practice, briefly.** Per-transaction limits in retail banking are payer-side (PSD2 Art. 68, the
EBA guidelines, the FFIEC guidance); where institutions cap inflows they cap them lower than
outflows, for instrument risk this API cannot have (a deposit here names no funding instrument); AML
thresholds are symmetric. A 1,000,000 unfunded deposit ceiling matches no recognised pattern.

## Decision

**D1 — One cap, every move, and the client promises exactly what the contract publishes.** The
per-transaction bound is 100,000 for deposits as for everything else. The forms hold ONE constant,
`MONEY_MAX`, and the two names that used to imply a direction are gone. This retires the visible
1,000,000, which was never product scope.

**D2 — A literal, kept honest by a tripwire — not a runtime import.** The generated request schemas
are the mechanical path from the C# constant to the browser, and this repository already generates
its response validators from them (ADR-0023); importing a request schema into the forms for one
number that changes once a year at most would couple the forms to the schema library's getter API
for no gain. Instead, a unit test in the tripwire suite asserts that every generated money request
schema's amount bound equals `MONEY_MAX`, so a server change that regenerates the contract turns
the forms red until the constant follows.

**D3 — The copy is derived from the number.** "Maximum deposit is €100,000." is built from
`MONEY_MAX` through a whole-euro formatter, so the sentence and the bound cannot drift apart the way
"€1,000,000." and a 100,000 contract did. The four forms' copy is unchanged in wording.

**D4 — The mock is realigned to the measured sentence, and the real stack pins it.** The handlers'
amount-bound message is the 2026-09-03 observation, dated in the comment; the mock test that
asserted the dollar sentence now asserts the measured one; and the contract suite, which runs
against the real stack, gains a case that reads the document's `maximum`, posts one cent above it,
and asserts the envelope and the sentence. The two other server sentences the mock quotes with
dollar signs (insufficient funds, non-zero balance on delete) ~~were NOT re-measured here and are
marked as such rather than guessed at~~ (struck 2026-09-04; correction below).

> **Correction (2026-09-04): the two sentences D4 left unmeasured have been measured, and neither
> carries an amount.** `3769dc9` had not only dropped the `$`; it removed the figures altogether
> and reworded one sentence. Observed through the BFF on 2026-09-03 (23:25Z):
>
> ```
> POST   /api/transactions/withdraw  (balance 6, amount 10)      -> 422 INSUFFICIENT_FUNDS
>        detail "Insufficient funds."  available 6.0000  requested 10
> POST   /api/transfers               (balance 16, amount 1000,
>                                      minted authorisation)     -> 422 INSUFFICIENT_FUNDS
>        detail "Insufficient funds."  available 16.0000  requested 1000
> DELETE /api/accounts/{id}           (balance 16)               -> 422 NON_ZERO_BALANCE
>        detail "Cannot delete an account with a non-zero balance."
> ```
>
> The mint for 1000 on a balance of 16 answered 201: funds are checked at the transfer, not at the
> mint (ADR-0042). The mock now quotes these, `serverCurrency` is gone, and the contract suite pins
> the withdrawal and the delete on the real stack from a fresh account. Transcript:
> `measure-2026-09-04.txt` in the work log's plan for the 2026-09-04 sweep.

**D5 — The committed document is guarded against a constant changed without a regen.** The regen
step is manual and deliberately outside CI, so a backend architecture test now reads the committed
`openapiv1.json` and asserts that all six money schemas publish `maximum` equal to
`TransactionMaxAmount`, `minimum` equal to `TransactionMinAmount`, and the description
`[MoneyRange]` writes. The drift gate proves generated == committed; this proves committed ==
server, for the one number the client promises.

**D6 — `DailyTransferLimit` is declared and not enforced, and this ADR says so rather than
promising it.** `ValidationRules.DailyTransferLimit = 1_000` has no consumer anywhere. ~~It is left
in place — deleting it is a separate, cheap change~~ (struck 2026-09-04; correction below) — but no
document may cite it as a control.

> **Correction (2026-09-04): the constant is gone.** `ValidationRules.DailyTransferLimit` was
> deleted in the 2026-09-04 sweep, after re-running the grep behind D6: one definition,
> zero readers, across `backend/src`, `backend/tests`, `frontend/`, `docs/api/openapiv1.json` and
> the generated `schema.d.ts`. No behaviour changed, because nothing ever read it. A daily or
> aggregate limit remains unbuilt and undecided — D7 and the backlog's transaction-limits feature.

**D7 — Not in this decision.** Showing the cap proactively or clamping the input (U8 UI/UX, last as
always); per-day, per-account or tiered limits (the backlog's transaction-limits feature, which
this decision neither starts nor forecloses — the direction seam it would need can be added when
the second number exists); raising the server's bound for any operation.

## Alternatives declined

- **Raise the server's deposit cap to 1,000,000 and publish it.** It would honour a number nobody
  decided, cost a parameterised attribute, two transformers, six validator sites, six schema
  entries and two generated files, and give a demo bank an inflow ceiling ten times its outflow
  one, which no recognised practice supports.
- **Split the server's constant into inflow and outflow bounds at equal values, for the future.**
  Speculative surface for numbers that are the same today; the seam costs nothing to add on the
  day a second number exists.
- **Derive the client bound at runtime from the generated request schema.** Same outcome today, a
  coupling of the forms to the schema library's introspection API, and it would have let the
  number change from 1,000,000 to 100,000 with no decision on record.

## Consequences

A user typing between 100,000 and 1,000,000 into the deposit form is now told the bound before
pressing the button, in the same words as every other money form, instead of after a round trip in
the server's words. The form's copy has changed from "€1,000,000" to "€100,000" — a behaviour
change for anyone who believed the old figure, and the reason this is an ADR rather than a
one-character fix. The tests that asserted the client's own literal now assert the contract's
bound, on every form; two guards go red on the two ways this can drift again (the contract
regenerated under the forms, and the constant changed without a regen), and a third case — one
form's literal edited away from the constant — is the same tripwire, run through each form.

## What would change this

- **A second money bound on the server** — a tiered or per-direction limit — is the day
  `MONEY_MAX` becomes a map keyed by operation, the tripwire iterates it, and D1's "one cap" is
  reopened as a decision rather than a fact.
- **A relay for the contract check into CI** (`openapi-spec.mjs check` against a running API) would
  make D5's guard redundant; until then it is the only thing standing between a constant and the
  document.
