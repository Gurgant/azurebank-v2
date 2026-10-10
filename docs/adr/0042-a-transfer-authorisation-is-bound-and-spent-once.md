# ADR-0042: A transfer authorisation is bound to its amount and payee, and spent once

**Status:** Accepted · **Date:** 2026-08-16 · **Amended:** 2026-09-06 (ADR-0049), 2026-09-07
(ADR-0050), 2026-09-14 (ADR-0044), 2026-10-03 (ADR-0062) · Builds on ADR-0041 and ADR-0009

## Context

ADR-0041 moved the transfer PIN into the request so that the API could refuse on its own. That fixed
where the check happens, not what it proves: before this decision one static PIN authorised a
transfer of 10 and then one of 20, and nothing recorded that an authorisation had been given, let
alone spent. A PIN proves that someone knows the PIN, never that the account holder approved this
amount to this payee. Of the four requirements of PSD2-RTS Art. 5 (dynamic linking) only (a) held,
the payer sees amount and payee: the code was not specific to them (b), matched nothing agreed (c),
and no change invalidated it (d).

## Decision

1. **A PIN entry mints an authorisation**: a row bound by HMAC to the operation, the payer, the
   source account, the payee and the amount, valid for two minutes and consumable exactly once by
   the transfer it authorises, because what the server is shown is then specific, changes with the
   operation and is spent once: (b), (c) and (d).
2. **The check lives inside `TransferService`, not in front of the pipeline**, because
   `IdempotencyMiddleware` returns a stored response before the action runs: a gate in front of it
   would refuse a retry whose authorisation had lapsed before the stored 201 could be handed back,
   to the one client that cannot know whether its money moved.
3. **The reference travels in a header (`Step-Up-Authorization`), never in the body**, because the
   idempotency fingerprint is over the body alone: the same bytes are resent with a different,
   expired or absent authorisation, which in the body would each be a 422 `IDEMPOTENCY_KEY_REUSE`.
4. **Bound to `RecipientUserId`, not the handle**, because an AzureTag is renameable (ADR-0015): a
   binding to `@admin` would outlive `@admin` becoming someone else's handle.
5. **A keyed hash, not a column per bound field**, because a hash cannot be partially compared by
   accident, and a new field forces its definition to change (the `v1|` prefix). Keyed, because the
   bound values are a small space that a bare digest would let a reader of the database guess, and
   with its own key (`StepUp:BindingKey`), so that a leaked `Idempotency:HashKey` cannot forge it.
6. **Consumption is one statement, inside the transfer's transaction**, because a read-then-write
   is a double spend (ADR-0009, ADR-0010), and an authorisation spent by a transfer that rolled back
   is money the user can no longer send.
7. **Expiry is two minutes, not refreshable**: a policy, because the RTS prescribes no lifetime for
   an authentication code (EBA Q&A 2018_4141; Art. 4(3)(d)'s five minutes limit inactivity on
   account access). The client mints and sends on the sixth PIN digit, one user action, so only a
   retry after a lost response reaches the expiry: it has its own code, costs no PIN attempt, and
   the re-prompt keeps amount and payee (WCAG 2.2 SC 3.3.7, Level A).
8. **The header is required, and no transfer body carries a PIN** (the flip, this record's second
   half: `TransferRequest.Pin` and `InternalTransferRequest.Pin` are gone), because while two proofs
   are accepted the weaker one decides. A transfer presenting none is refused at the rung the PIN
   check occupied, after the source account's ownership 404 and before the payee is resolved, so a
   caller with no second factor cannot ask this endpoint which handles exist.
   `StepUpAuthorizationOperationTransformer` publishes the header as required.

### Error codes

| Code | Status | Means |
| --- | --- | --- |
| `AUTHORIZATION_REQUIRED` | 401 | none presented: the header is absent or empty |
| `AUTHORIZATION_EXPIRED` | 401 | valid, window passed: re-prompt the PIN, keep the form |
| `AUTHORIZATION_INVALID` | 401 | uniform across unknown, not-yours, already-spent, wrong binding |

The specific reason is logged and never sent, so the endpoint is no oracle for which references
exist or whose they are. A header value that is not a UUID is refused `400` by model binding, with
no `errorCode`. `422 PIN_REQUIRED` and `429 PIN_LOCKED` live on the mints,
`POST /api/transfers/authorizations` and `POST /api/transfers/internal/authorizations`, the only
place on the transfer path that spends a PIN attempt.

## Rejected

- Rejected: a non-nullable header parameter, because the promised 401 becomes a model-state 400.
- Rejected: the in-body PIN beside the header as an end state, because the weaker proof decides.

## Consequences

- The rail also carries an account closure (ADR-0049) and a withdrawal (ADR-0056) on the same `v1|`
  payload; `v2|` arrives with the first operation that needs a field the payload does not have.
- A closure answers the same three codes, with its two 422 guards ahead of `AUTHORIZATION_REQUIRED`;
  it stores no response to replay, so its check sits in `AccountService` only because the service
  owns the action, and it consumes with a null `ConsumedByTransactionId` (ADR-0049).
- The external-transfer mint answers `422 DAILY_LIMIT_EXCEEDED` ahead of the PIN, at no cost in
  attempts; on the transfer the daily check comes after `AUTHORIZATION_REQUIRED` and the payee
  resolution, so decision 8's argument is intact (ADR-0050).
- The audit row a consumed authorisation buys names it, under the chain's hash, and the
  authorisation's `ConsumedByTransactionId` names the transfer's outgoing ledger row (ADR-0044).
- Not covered: the PIN is not a one-time code. That needs a second channel, which does not exist.
- Not covered: nothing sweeps the table by age: its rows are the PSD2 Art. 72 evidence, and
  retention is a later decision with its own index. Only a demo copy's rows leave, with the copy
  (ADR-0062).

## Verified by

- `StepUpAuthorizationTests`: `Replay_OfACompletedTransfer_SucceedsEvenWithAnExpiredAuthorisation`
  is decision 2 in falsifiable form.
- `StepUpConcurrencySqlServerTests`: eight transfers presenting one authorisation move money once.

## Related

ADR-0009, ADR-0010, ADR-0015, ADR-0041, ADR-0044, ADR-0049, ADR-0050, ADR-0056, ADR-0062.
