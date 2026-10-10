# ADR-0049: Closing an account is authorised like a transfer

**Status:** Accepted · **Date:** 2026-09-06 · **Amended:** 2026-09-07 (ADR-0050),
2026-09-11 (ADR-0044), 2026-09-14 (ADR-0044), 2026-09-21 (ADR-0056), 2026-10-10 (D9)

## Context

ADR-0008's table of protected operations says *Delete account: Level 2*, and before this decision
nothing enforced it: `DELETE /api/accounts/{id}` was `[Authorize]` and no more. Measured then, a
DELETE carrying a `Step-Up-Authorization` header answered 200 at level 1 with no PIN enrolled, while
the reveal of an account number answered 403 `STEP_UP_REQUIRED` from the BFF. Two 422 guards, a
non-zero balance and the primary account, already make the money case unreachable. Their order,
balance before primary, is code (`RefuseIfNotClosable`), held by a unit test and by no probe.

## Decision

- **D1: A closure costs a PIN**, because the account's owner cannot undo it: no endpoint lists or
  restores a deleted account, and its history leaves their view with it.
- **D2: At the API, on the ADR-0042 rail, not as a BFF level-2 rule**, because a session flag is a
  property of the session and not of the act, a BFF rule leaves a caller that reaches the API
  directly unprotected, and a parameterised DELETE fits neither branch of `RequiresPinVerification`:
  a third branch is what ADR-0041's trigger (a) names as reopening its lockout gap. The reveal's
  exemption does not apply: a closure has a subject to bind, the account id.
- **D3: The binding is `(AccountDeletion, user, account, amount rendered 0)` under the v1 payload.**
  `StepUpBinding.ForAccountDeletion(accountId)` is `(accountId, null, null, 0m)`, and nothing else
  is bound. No field is added, so the payload stays v1, with no migration. The operation name is in
  the hash: a transfer's authorisation on the same account answers 401 `AUTHORIZATION_INVALID`. A
  unit test of the two hashes holds that: on the wire a transfer also differs by payee and amount.
- **D4: The mint is `POST /api/accounts/{id}/deletion-authorizations`, operation in the segment**,
  because the resource is the account: a mint that hangs off a resource other than its operation
  names the operation in the segment. Body `{pin}`, 201 `{authorizationId, expiresAt}`, no
  `[RequireIdempotency]`, because minting moves nothing and a repeat costs a PIN attempt. It runs
  ownership → the two 422 guards → `MintAsync`: a guard that reveals only the caller's own state
  answers before the PIN is consulted, so a wrong PIN on a funded or primary account costs nothing.
  No PIN enrolled is 422 `PIN_REQUIRED`: "PIN must be set before authorising this operation."
- **D5: `ConsumedByTransactionId` is NULL for a closure**, because a closure has no ledger row, and
  an account id in that column would mislead the evidence pack, which joins it to a movement's id.
  The `AccountDeleted` row's `Detail` names the authorisation consumed (ADR-0044). For a row written
  before 2026-09-14 the operator query below is the record; its 5 s window is no key.
- **D6: The two 422 guards stay ahead of the presence check**, a deliberate difference from
  ADR-0042, because `NON_ZERO_BALANCE` and `PRIMARY_ACCOUNT_DELETE` reveal only the caller's own
  account state, which `GET /api/accounts` already gives: there is no oracle to protect. The order
  is the contract: ownership → balance → primary → presence → validation → the transaction (D8).
- **D7: One refusal event, and the 422s stay log-only.** A DELETE with no authorisation writes
  `AccountDeletionRefused` through `RecordRefusalAsync`, after the ownership check, so the row names
  an account the caller owns and outlives the 401. A wrong or locked PIN at the mint writes the same
  event (ADR-0044). The 422s write none, because the account's owner can trigger them at will.
- **D8: An explicit transaction, consume after the save, re-entrancy written into the delegate.**
  `DeleteAccountAsync` runs the soft delete, the `AccountDeleted` row, the save, then
  `ConsumeAsync`, then the commit, because two autocommits would leave the account closed with
  nothing spent: a consume that matches no row throws and rolls the soft delete back. Each attempt
  reloads the account, answers 404 if it is closed and runs the guards again, so a deposit that
  raced the closure ends in 422. An outer loop retries a `DbUpdateConcurrencyException`; without it
  eight DELETEs at once answered `500,500,500,500,500,200,500,500`.
- **D9: A money write whose account closed while it ran is refused as a request on a closed
  account**, and so is one whose account row is gone: 404 `ACCOUNT_NOT_FOUND` for the caller's own
  account, with nothing written and nothing spent. A deposit, a withdrawal and both transfers look
  at their accounts again after every reload, because a reload returns a closed row with a current
  `RowVersion`. An external transfer whose payee account closed pays another open account of the
  same payee, the primary first, then the oldest: the payer names a person, and the authorisation
  binds that person, not an account. It answers 422 `RECIPIENT_NO_ACCOUNT` only when none is open.

```sql
SELECT s.Id, s.Operation, s.Status, s.CreatedAt, s.ConsumedAt, s.ConsumedByTransactionId,
       e.Sequence, e.Event, e.OccurredAt
FROM StepUpAuthorizations s
JOIN AuditEvents e ON e.ActorUserId = s.UserId AND e.Event = 'AccountDeleted' AND e.SubjectId = @a
 AND s.ConsumedAt BETWEEN DATEADD(second, -5, e.OccurredAt) AND DATEADD(second, 5, e.OccurredAt)
WHERE s.UserId = @u AND s.Operation = 'AccountDeletion' AND s.Status = 'Consumed'
ORDER BY s.ConsumedAt;  -- @u is the actor (AuditEvents.ActorUserId), @a the account
```

**Error codes on `DELETE /api/accounts/{id}`**, in the order of D6. The mint answers the two 422s
too; `PIN_REQUIRED` (422), `INVALID_PIN` (401) and `PIN_LOCKED` (429) come only from the mint.

| Order | Code | Status | Means |
| --- | --- | --- | --- |
| 1 | `ACCOUNT_NOT_FOUND` / `ACCESS_DENIED` | 404 / 403 | not the caller's account |
| 2 | `NON_ZERO_BALANCE` | 422 | guard, ahead of the presence check |
| 3 | `PRIMARY_ACCOUNT_DELETE` | 422 | guard, ahead of the presence check |
| 4 | `AUTHORIZATION_REQUIRED` | 401 | none presented: "This account closure has not been authorised." An empty header binds to null and lands here |
| 5 | `AUTHORIZATION_EXPIRED` | 401 | valid, window passed: the PIN is asked again, no attempt spent |
| 5 | `AUTHORIZATION_INVALID` | 401 | uniform: unknown, not the caller's, spent, wrong operation, wrong account |
| — | model-state 400 | 400 | header present but not a UUID, keyed `Step-Up-Authorization` |

## Rejected

- Rejected: striking the Level 2 row as won't-do, because it contradicts what ADR-0008 always said.
- Rejected: `/api/accounts/{id}/authorizations`, because a second kind of mint would collide there.
- Rejected: the account id in `ConsumedByTransactionId`, because the evidence join never matches it.
- Rejected: the presence check ahead of the guards, because the guards are no oracle (D6).
- Rejected: a "closing" state between open and closed, because every operation is one database
  transaction, so nothing is in flight for longer than a request. The row version says which of
  the two committed first, and the one that lost reads the row again: a closure that meets a
  deposit sees the money and refuses, a deposit that meets a closure sees the closed account and
  refuses (D8, D9).

## Consequences

- The Level 2 row is enforced at the API by a third `StepUpOperation`, a mint and a required header
  on the DELETE, with no migration, no new column and no change to `AuthLevelMiddleware`.
- `DeleteAccountDialog` mints on the sixth digit, then deletes with the header; no idempotency key.
- Measured, under the row numbers the code cites. **Row 2:** a wrong PIN on a funded account answers
  422 with the PIN counter unchanged. **Row 11:** a DELETE 130 s after the mint answers 401
  `AUTHORIZATION_EXPIRED`, no attempt spent, the authorisation still Pending. **Row 14:** a
  `[ProducesResponseType(422)]` on the mint action outranks the document transformer's entry and
  publishes the bare "Unprocessable Entity", so the mint action carries none.
- A probe on the real stack leaves its audit rows: the chain is append-only, the row is the record.

**Not done**, each one not covered:

- No `SubscriberNotice` on a closure: whether one is owed is its own decision, in ADR-0047's shape.
- An expired or invalid authorisation on the DELETE writes no audit row, as on a transfer.
- The history loss stays open: `GET /api/transactions` leaves out the rows of a closed account.
- `DeletedAt` and `UpdatedAt` are stamped from two clocks; ADR-0050 gave only the ledger its clock.
- No `evidence` verb for closures: the audit row's `Detail`, or D5's query, is the operator's tool.

## Revisit when

- A second non-money operation wants a mint: `StepUpBinding` gets its own shape, the payload `v2|`.
- A closure notice is decided: it joins an audit row (ADR-0047's shape), and the gate does not move.
- The reveal moves onto a mint: `AuthLevelMiddleware`'s level-2 rule then protects nothing.
- An operation outlives its request, a scheduled payment or a hold: a closing state then earns its
  column, because something is in flight while no transaction holds the account.

## Verified by

- `AccountDeletionSqlServerTests` (D8), `AccountDeletionAuthorizationTests`, `AccountServiceTests`,
  `StepUpAuthorizationServiceTests` (the operation name in the hash).
- D9, on SQL Server: `DepositIntoClosedAccountSqlServerTests`,
  `TransferOrWithdrawalOnClosedAccountSqlServerTests`, `PayeeAccountClosedMeanwhileSqlServerTests`.
- The SPA: `stepUp.contract.test.ts`, `money.contract.test.ts` and `deleteAccount.spec.ts`.

## Related

ADR-0008, ADR-0041, ADR-0042, ADR-0044, ADR-0047, ADR-0050, ADR-0056.
