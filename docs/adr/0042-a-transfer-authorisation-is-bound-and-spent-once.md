# ADR-0042: A transfer authorisation is bound to its amount and payee, and spent once

**Status:** Accepted · **Date:** 2026-08-16 · Builds on
[ADR-0041](0041-the-api-verifies-the-transfer-pin.md) (the API verifies the PIN) and
[ADR-0009](0009-idempotency-monetary-operations.md) (idempotency). Supersedes neither.

## Context

ADR-0041 moved the transfer PIN into the request so the API could refuse on its own. That fixed
*where* the check happens. It did not change *what the check proves*.

Measured on `main` @ `4811667`, fresh user, PIN enrolled, €300 deposited:

```
POST /api/transfers  {amount: 10, recipientAzureTag: "admin", pin: "123456"}  -> 201
POST /api/transfers  {amount: 20, recipientAzureTag: "admin", pin: "123456"}  -> 201
```

**One static credential authorised two different movements of money**, and nothing recorded that an
authorisation had been given, let alone spent. A PIN proves *"someone knows the PIN"*. It never
proved *"the account holder approved THIS amount to THIS payee"*.

Against PSD2-RTS Art. 5 (dynamic linking), only the first of four requirements held:

| Requirement | Before |
| --- | --- |
| (a) payer made aware of amount and payee | ✅ the review step shows both |
| (b) code **specific to** amount and payee | ❌ the same six digits authorised anything |
| (c) accepted code corresponds to what was agreed | ❌ nothing to correspond to |
| (d) any change invalidates the code | ❌ it never changed |

## Decision

A PIN entry now **mints an authorisation**: a row bound by HMAC to the operation, the payer, the
source account, the payee and the amount, valid for two minutes, and consumable exactly once by the
transfer it authorises.

What becomes single-use is **not the secret the user types** but the authorisation minted from it.
That satisfies (b), (c) and (d) — the value presented to the server is specific, changes with the
operation, and is spent once. It does **not** make the PIN a one-time code; that needs a second
channel, which is the same missing infrastructure as T13.

### Five decisions worth their own paragraphs

**The check lives inside `TransferService`, not in front of the pipeline.** This is the one that was
easiest to get wrong. Measured: `IdempotencyMiddleware` writes a stored response and `return`s
*before* `await _next(context)`, so a **replay never enters MVC** — no model binding, no validation,
no controller, no service, and therefore no authorisation check. That is correct, and it is what
every payment API that documents the ordering does: authenticate the **caller** upstream of the
replay lookup, authorise the **customer** downstream of it. A gate in front of the idempotency claim
would refuse a retry whose two-minute authorisation had lapsed *before* the stored 201 could be
handed back — leaving the one client that provably cannot know whether its money moved unable to
find out. `StepUpAuthorizationTests.Replay_OfACompletedTransfer_SucceedsEvenWithAnExpiredAuthorisation`
is the falsifiable form of this decision.

**The reference travels in a header, never in the body.** `ComputeRequestHashAsync(Stream body, …)`
fingerprints the **body alone**. Keeping the authorisation out of it is what lets the same transfer be
resent byte-identically — same `Idempotency-Key` — while carrying a different, expired or absent
authorisation. In the body, each of those would change the fingerprint and be refused as
`IDEMPOTENCY_KEY_REUSE` (422) before the endpoint ever saw it. This is a one-way door: choosing the
body would make the retry path unfixable.

**Bound to `RecipientUserId`, not the handle.** An AzureTag is renameable (ADR-0015). An
authorisation naming `@admin` would outlive `@admin` becoming someone else's handle — the binding
would survive the payee it named. The tag stays a display concern.

**A keyed hash, not a column per bound field.** Same reasoning as `IdempotencyRecord.RequestHash`: a
hash cannot be partially compared by accident, and adding a field to the operation forces the hash
definition to change (hence the `v1|` prefix) instead of silently leaving the new field unbound.
Keyed rather than bare, because `(operation, payer, account, payee, amount)` is a small space — an
unkeyed digest would let anyone with database read access confirm guesses about who paid whom. The
key is separate from `Idempotency:HashKey`: the two hashes answer different questions, and one leaked
key must not forge the other's answer.

**Consumption is one statement, inside the transfer's transaction.** A read-then-write would be a
double spend, and this repository has been bitten by exactly that shape twice (ADR-0009, and the
`PinAccessFailedCount` race in ADR-0010). Riding the transaction is equally deliberate: an
authorisation marked spent by a transfer that rolled back is money the user can no longer send,
for a payment that never happened.

### Expiry

Two minutes, not refreshable. Not a regulatory figure — the RTS prescribes **no** lifetime for an
authentication code, and EBA Q&A 2018_4141 says so outright; the five minutes in Art. 4(3)(d) is an
inactivity limit on *account access*, a different provision. Two minutes is our policy.

In practice the window is invisible: the client sends on the sixth PIN digit, so mint and spend are
one user action milliseconds apart. **Expiry is only reachable on a retry after a lost response** —
which is why an expired authorisation gets its own code rather than the generic refusal, why it must
never consume a PIN attempt (an expiry is not a failed authentication), and why the amount and payee
stay on screen when the client re-prompts. WCAG 2.2 SC 3.3.7 Redundant Entry is **Level A**: only the
security information may be asked for again.

### Error codes

| Code | Status | Means |
| --- | --- | --- |
| `AUTHORIZATION_REQUIRED` | 401 | none presented |
| `AUTHORIZATION_EXPIRED` | 401 | valid, window passed — re-prompt the PIN, keep the form |
| `AUTHORIZATION_INVALID` | 401 | **uniform** across unknown, not-yours, already-spent, wrong binding |

The uniformity is the `RefreshTokenInvalid` posture: the specific reason is logged server-side and
never put on the wire, so the endpoint is not an oracle for which references exist or who owns them.
Before this ADR none of these could be said at all — an elapsed elevation and one never granted were
the same `403 STEP_UP_REQUIRED`.

## Consequences

**Shipped here (PR 1, backend only).** The entity and its migration, the two mint endpoints, the
header, validation and consumption in both transfer paths, and fourteen tests including a SQL-Server
concurrency proof that eight simultaneous transfers presenting one authorisation move money exactly
once.

**Additive at first, then flipped.** The mechanism shipped with the header OPTIONAL: the in-band PIN
of ADR-0041 was still verified on every transfer, so nothing was weaker whether or not a client sent
one. That was right for one PR and wrong as an end state — while both proofs are accepted the weaker
one decides, and six static digits authorising any amount to any payee is the finding this ADR opens
with. The flip removed `TransferRequest.Pin` and `InternalTransferRequest.Pin`, made the header
required, and refuses a transfer presenting none with `401 AUTHORIZATION_REQUIRED`.

Three things about the flip are worth keeping, because each was measured rather than reasoned:

- The refusal sits at the rung the PIN check occupied — after the source account's ownership 404 and
  BEFORE the payee is resolved. That placement is not tidiness: it is what stops a caller holding no
  second factor from asking the endpoint which handles exist, one 404 at a time. The deleted check
  was providing that property silently.
- The parameter stays `Guid?`. An EMPTY header binds to `null` exactly like an absent one, so both
  reach the same 401, while a value that is not a UUID is still refused `400` by model binding
  upstream. Making it non-nullable would have bought a `required: true` in the document at the price
  of replacing the promised 401 with a model-state 400 carrying no `errorCode`.
- Because the parameter is nullable, the generator published the header as OPTIONAL — a contract
  narrower than the code, which regenerating could not detect since the spec and the generated
  artefacts agreed with each other. `StepUpAuthorizationOperationTransformer` marks it required,
  following the mechanism `[RequireIdempotency]` already used.

`422 PIN_REQUIRED` and `429 PIN_LOCKED` left both transfer endpoints with the PIN. They live on the
mint now, which is the only place on this path that can spend an attempt.

**Not done, and not pretended otherwise.** Withdraw keeps its in-body PIN with the same weakness and
should follow this route as its own task. Nothing sweeps the table — rows are the Art. 72 evidence B3
assembles, so a retention policy is a later decision that brings its own index.
