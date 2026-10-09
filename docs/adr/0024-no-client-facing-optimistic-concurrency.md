# ADR-0024: No client-facing optimistic concurrency — ETag/If-Match rejected

**Status:** Accepted · **Date:** 2026-07-25 · **Decision Makers:** Vladislav Aleshaev

## Context

A rejection leaves nothing to read: no code, no type and no test records a feature that was decided
against, so it can only be rediscovered by running the same investigation again. ETag/If-Match is
such a feature. The audit of the original implementations (its findings are ADR-0025) scheduled it
as a backend item to adapt, and a sourced investigation then parked it. `If-Match` occurs nowhere in
`backend/src` or `frontend/src`, so without this record anyone working through the audit's items
builds a feature that was researched and rejected.

## Decision

1. **ETag/If-Match conditional requests are deliberately not built**, on the API or on the frontend,
   because the problem they solve does not exist here. Idempotency solves duplicate submissions;
   optimistic concurrency solves lost updates. Client-facing `If-Match` would protect a nickname
   rename on a single-owner resource that nobody edits from two devices at once, at the cost of
   version tokens on responses, 428 and 412 handling and a frontend cache slice.
2. **Idempotency keys are the only client-facing write-safety contract** (ADR-0009, ADR-0022),
   because that is what the payment industry standardizes on: Stripe, Adyen, PayPal, GoCardless,
   Marqeta, Plaid, Wise, Modern Treasury and UK Open Banking. `If-Match` is a cloud and platform
   pattern (GitHub, Cosmos DB, Microsoft Graph, Google Cloud Storage), where independent editors
   mutate shared objects. A future write-safety proposal is expressed as an idempotency key, or it
   is rejected.
3. **Concurrency is resolved server-side**, through `Account.RowVersion` and `ConcurrencyRetry`. It
   is never delegated to clients through conditional requests.
4. **Non-money resources are last-write-wins by design.** Account rename, set-primary and delete
   carry no version token and no 412 handling. It is recorded so that the absence reads as a choice,
   not as an omission to fix.
5. **The re-open trigger is a genuinely contended multi-editor resource.** None exists. The trigger
   keeps the decision falsifiable: if such a resource appears, the decision is reconsidered on its
   merits, and the arrival alone does not settle it.

Forbidden: ETag, `If-Match`, 412 or 428 plumbing in the API; an etag slice or an equivalent version
cache in the frontend; "consistency" clean-ups that expose `RowVersion` in a response contract.

## Rejected

- Rejected: shipping it as a showcase of HTTP conditional requests, because a portfolio that shows
  unnecessary machinery demonstrates the wrong judgement, and a reviewer who knows the domain
  notices that banks do not do this.
- Rejected: exposing `RowVersion` on `AccountResponse` "for consistency" without the conditional
  flow, because it puts a token in the contract that nothing consumes, which is worse than either
  building the feature or not.
- Rejected: deferring the decision without recording it, because that has already cost one
  re-proposal cycle.

## Consequences

- The rejection has a named trigger, and a contributor proposing conditional requests has something
  to read before spending a day on it. The audit's item is closed.
- `RowVersion` exists on the entity and is used server-side, so a reversal is contract and frontend
  work, not a migration: the cost of deciding otherwise later is bounded.
- The parked design sketch is version-in-body plus `If-Match`: a base64 `version` field on
  `AccountResponse`, and 428 and 412 with `CONCURRENCY_CONFLICT`. It is a starting point if the
  trigger fires, and never approved scope.
- A resource that is contended one day does not find the plumbing present.
- Not covered: last-write-wins on account metadata is a real loss. If the same user renames an
  account from two devices in the same minute, one rename disappears with no warning. Accepted: the
  resource has one owner and the data is a label.

## Revisit when

- A genuinely contended multi-editor resource appears (decision 5).

## Verified by

- `grep -rn "If-Match" backend/src frontend/src` finds nothing.

## Related

ADR-0009, ADR-0022, ADR-0025.
