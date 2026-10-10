# ADR-0046: One money cap for every move, and the client promises what the contract publishes

**Status:** Accepted · **Date:** 2026-09-03 · **Amended:** 2026-09-04 (D4 and D6), 2026-09-07 (D6
and D7, ADR-0050), 2026-09-10 (D5, ADR-0053), 2026-09-21 (seven schemas, ADR-0056)

## Context

The server enforces one per-transaction bound on every money move (deposit, withdrawal, transfer,
internal transfer and the authorisations minted for them) from a single constant,
`ValidationRules.TransactionMaxAmount` (100,000, in the product's currency), through `[MoneyRange]`
and a FluentValidation rule, and publishes it as `maximum: 100000.00` on seven request schemas of
`docs/api/openapiv1.json`. Nothing inflow-specific exists on the server or in the document. Before
this decision the deposit form held a literal of its own, 1,000,000, which no record decided: a
deposit of 500,000 passed the form and was refused by the API. A client bound kept by hand drifts
from the contract unless something goes red when it does.

## Decision

**D1 — One cap, every move, and the client promises exactly what the contract publishes.** The
per-transaction bound is 100,000 for a deposit as for everything else, and the forms hold one
constant, `MONEY_MAX`, because no bound that depends on direction exists on the server.

**D2 — A literal, kept honest by a tripwire, not a runtime import.** A unit test in the tripwire
suite asserts that every generated money request schema's amount bound equals `MONEY_MAX`, so a
regenerated contract turns the forms red until the constant follows; importing a request schema
for one number would couple the forms to the schema library's getter API for no gain.

**D3 — The copy is derived from the number.** "Maximum deposit is €100,000." is built from
`MONEY_MAX` through a whole-euro formatter, because a sentence and a bound written apart drift.

**D4 — The mock quotes the measured sentences, and the real stack pins them**, because a mock and
its tests stay green against a sentence the API no longer sends. One cent above the bound answers
`400` in the framework's model-state envelope,
`{"Amount":["Amount must be between 0.01 EUR and 100000.00 EUR"]}`: the contract suite reads the
document's `maximum`, posts one cent above it and asserts envelope and sentence. The other two
sentences carry no amount: `422 INSUFFICIENT_FUNDS`, "Insufficient funds.", and
`422 NON_ZERO_BALANCE`, "Cannot delete an account with a non-zero balance.". Funds are checked at
the transfer, not at the mint (ADR-0042): a mint of 1,000 on a balance of 16 answers 201.

**D5 — The committed document is guarded against a constant changed without a regeneration.** A
backend architecture test reads the committed `openapiv1.json` and asserts that all seven money
schemas publish `maximum` equal to `TransactionMaxAmount`, `minimum` equal to
`TransactionMinAmount`, and the description `[MoneyRange]` writes, because the regeneration step is
manual. ADR-0053's test proves the document is what the code generates; only this one asserts the
value, and it alone would see a generator and a document that agree on a wrong bound.

**D6 — No constant promises a daily limit.** `ValidationRules.DailyTransferLimit = 1_000` had one
definition and no reader, and is deleted: it was a dead number, never a decision, and no document
may cite it as a control. The aggregate that exists is ADR-0050's, an option (`DailyLimit:Amount`,
default 5,000) and not a constant.

**D7 — Not in this decision.** Showing the cap before the amount is typed, or clamping the input;
per-account and tiered limits, which stay undecided (the per-day aggregate on external transfers
is ADR-0050's); raising the server's bound for any operation.

## Rejected

- Rejected: raising the server's deposit cap to 1,000,000 and publishing it, because it honours a
  number nobody decided and gives the bank an inflow ceiling ten times its outflow one. Limits in
  retail banking are payer-side (PSD2 Art. 68, the EBA guidelines, the FFIEC guidance), and where
  inflows are capped they are capped lower than outflows.
- Rejected: splitting the server's constant into inflow and outflow bounds of equal value, because
  it is speculative surface: the seam costs nothing to add the day a second number exists.
- Rejected: deriving the client bound at runtime from the generated request schema, because it
  couples the forms to the schema library's introspection API, and the number could then change
  with no decision on record.

## Consequences

- An amount above 100,000 in the deposit form is refused before the button is pressed, in the
  same words as every other money form, and not after a round trip in the server's words.
- Two guards go red on the two ways the bound can drift: the contract regenerated under the forms
  (the tripwire), and the constant changed without a regeneration (D5). A form's literal edited
  away from the constant is the same tripwire, run through each form.
- Not covered: a single external transfer can be refused below the published `maximum`. ADR-0050's
  UTC-day aggregate has no schema `maximum`, so the effective bound for one transfer is
  min(`MONEY_MAX`, what remains of the day).
- Not covered: the forms do not show the cap in advance and do not clamp the input (D7).

## Revisit when

- The server gains a second per-request money bound, tiered or per direction: `MONEY_MAX` becomes a
  map keyed by operation, the tripwire iterates it, and D1's "one cap" is reopened as a decision.

## Verified by

- `PublishedMoneyBoundsTests` (D5) and `TransactionEndpointTests` (one cent above the maximum).
- `moneySchemas.test.ts` (the tripwire, `npm run test:tripwire`) and `money.contract.test.ts` (one
  cent above the published maximum, and the two figure-free sentences, on the real stack).

## Related

ADR-0023, ADR-0042, ADR-0050, ADR-0053, ADR-0056.
