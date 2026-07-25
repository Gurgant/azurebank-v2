# ADR-0024: No client-facing optimistic concurrency — ETag/If-Match rejected

**Status**: Accepted

**Date**: 2026-07-25

**Decision Makers**: Vladislav Aleshaev

---

## Context

A rejection leaves nothing to read. There is no code, no type and no test that records a feature
somebody decided not to build, which means it cannot be rediscovered by any means short of running
the same investigation again.

Here that is not hypothetical: a live to-do list actively resurrects it. The originals audit
schedules ETag/If-Match as a backend item — "adapt, low priority, warrants its own ADR" — and a
research pass two days later parked it after a sourced investigation. Anyone working that audit's
list top to bottom will build a feature that was researched and explicitly rejected. There are
currently **zero occurrences of `If-Match` anywhere in `backend/src` or `frontend/src`**, and no
ADR mentions it, so the parking decision exists only in a document that is about to be archived.

## Decision

1. **ETag/If-Match conditional requests are deliberately not built**, on the API or on the
   frontend.

2. **Idempotency keys are the only client-facing write-safety contract** (ADR-0009, ADR-0022). Any
   future write-safety proposal is expressed as an idempotency key, or it is rejected.

3. **Concurrency is resolved server-side**, through `Account.RowVersion` and `ConcurrencyRetry`. It
   is never delegated to clients through conditional requests.

4. **Non-money resources are last-write-wins by design.** Account rename, set-primary and delete
   carry no version token and no 412 handling. This is the positive half of the same decision, and
   it is recorded here so that the absence reads as a choice rather than as an omission somebody
   should go fix.

5. **The re-open trigger is a genuinely contended multi-editor resource.** None exists today. The
   trigger is what keeps this a falsifiable decision rather than dogma: if such a resource appears,
   this ADR is reconsidered on its merits.

## Rationale

The confusion that makes people re-propose this is a taxonomic one, so it is worth stating flatly:
**idempotency solves duplicate submissions; optimistic concurrency solves lost updates. They are
different problems.** Having solved the first thoroughly does not leave a hole where the second
should be — it leaves a question about whether the second problem exists here at all.

It does not. Client-facing `If-Match` in this system would protect a nickname rename on a
single-owner resource that nobody edits from two devices at once. The value is near zero and the
cost is real: version tokens on responses, 428 and 412 handling, and a frontend cache slice to
track them.

The industry evidence points the same way. No major card network, bank or payment service provider
standardizes on client-facing `If-Match` in its public API — Stripe, Adyen, PayPal, GoCardless,
Marqeta, Plaid, Wise, Modern Treasury and UK Open Banking all standardize on idempotency keys.
`If-Match` is a **cloud and platform** pattern — GitHub, Cosmos DB, Microsoft Graph, Google Cloud
Storage — where multiple independent editors mutate shared objects. That is not this domain.

## Alternatives considered

**Ship it as a showcase.** The honest framing of the original proposal: it demonstrates knowledge
of HTTP conditional requests. Rejected, because a portfolio that shows unnecessary machinery
demonstrates the wrong judgement, and a reviewer who knows the domain will notice that banks do not
do this.

**Expose `RowVersion` on `AccountResponse` "for consistency"** without the full conditional-request
flow. Rejected: it puts a token in the contract that nothing consumes, which is worse than either
building the feature or not.

**Defer the decision rather than record it.** That is the status quo this ADR replaces, and it has
already cost one re-proposal cycle.

## Residuals (accepted, documented)

- **Last-write-wins on account metadata is a real, accepted loss.** If the same user renames an
  account from two devices in the same minute, one rename disappears with no warning. Accepted:
  the resource has one owner and the data is a label.
- **`RowVersion` exists on the entity and is used server-side**, so a future reversal is not a
  migration — it is contract and frontend work. The cost of changing our mind is bounded, which is
  part of why rejecting now is safe.
- **This ADR closes the originals-audit item T1.3 by name.** Without that, the audit's own list
  re-opens the question the next time somebody reads it.
- The parked design sketch (version-in-body plus `If-Match`, a base64 `version` field on
  `AccountResponse`, 428 and 412 with `CONCURRENCY_CONFLICT`) survives in the archived research
  document. It may be consulted as a starting point **if the re-open trigger ever fires**, and it
  must never be read as approved scope. Its line references are pinned to a repository state many
  merges old and are already decaying.

## Consequences

**Positive** — the rejection is now falsifiable and has a named trigger; the audit item is closed
rather than perpetually pending; a contributor proposing conditional requests has something to read
before spending a day on it.

**Negative** — a future genuinely-contended resource will need this decision revisited rather than
finding the plumbing already present; anyone who wanted the HTTP-semantics showcase does not get it.

**Forbidden from here on**: adding ETag, `If-Match`, 412 or 428 plumbing to the API; adding an
etag-slice or equivalent version cache to the frontend; "consistency" cleanups that expose
`RowVersion` in a response contract.

## Related

- **ADR-0009** — idempotency keys, the write-safety contract that is used instead.
- **ADR-0022** — the client half of that protocol.
