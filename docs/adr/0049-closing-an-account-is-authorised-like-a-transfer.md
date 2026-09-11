# ADR-0049: Closing an account is authorised like a transfer

**Status:** Accepted · **Date:** 2026-09-06 · Closes the one row of ADR-0008's Protected Operations
table that was gated nowhere — *Delete account · Level 2* — by the mechanism
[ADR-0042](0042-a-transfer-authorisation-is-bound-and-spent-once.md) built for transfers, not by the
BFF session gate [ADR-0041](0041-the-api-verifies-the-transfer-pin.md) took transfers off. Adds a
third `StepUpOperation`, a mint endpoint, a required header on `DELETE /api/accounts/{id}` and one
refusal event in the [ADR-0044](0044-the-audit-trail-is-append-only-and-chained.md) inventory. No
migration, no new column, no change to `AuthLevelMiddleware`, no notice. Supersedes nothing; the
four records it moves are corrected in place and named below.

## Context

ADR-0008's table has said *Delete account → Level 2 → Destructive* since 2026-01-15, and its own
2026-08-12 correction recorded that the row was enforced by nothing: `DELETE /api/accounts/{id}` was
`[Authorize]` and no more. The 2026-08-18 correction made the case two-sided and left it open:

> *"Whether deletion should cost a PIN is a product call, and the case is genuinely two-sided.
> Against: the two 422 guards make the money case unreachable, so nothing here can lose funds —
> which is what A2/ADR-0042 exists to protect, and 'add a PIN because withdraw has one' is
> cargo-culting when the risk differs. For: this table says Level 2, the owner cannot undo it, and
> closing an account at a real bank is not a level-1 act. Recorded as still open rather than decided
> in passing, and the mechanism is now cheap either way — `StepUpAuthorization` binds its fields
> inside an HMAC rather than in columns, and
> `ToAccountId`/`RecipientUserId`/`ConsumedByTransactionId` are already nullable, so a third
> `StepUpOperation` needs no migration."*

Measured on `main` @ `d93ba10` before any of this was written — BFF `:5000` → API `:7215`, the
`AzureBankDev` LocalDB store, a fresh user at level 1 with no PIN enrolled. The rows below are
excerpted verbatim from the measurement transcript
(`plans/account-deletion/measure-before-2026-09-06.txt` in the working-state repo); its setup lines,
the first DB probe that hit wrong column names, and its own reading notes are omitted:

```
## DELETE /api/accounts/{id} BEFORE — 2026-09-06T09:24:40Z — BFF :5000 -> API :7215, AzureBankDev, main d93ba10
D1 DELETE the FUNDED account (balance 5)                   422  errorCode=NON_ZERO_BALANCE  detail="Cannot delete an account with a non-zero balance."
D2 DELETE the PRIMARY account (balance 0)                  422  errorCode=PRIMARY_ACCOUNT_DELETE  detail="Cannot delete primary account. Set another account as primary first."
D3 DELETE a nonexistent id                                 404  errorCode=ACCOUNT_NOT_FOUND  detail="Account with identifier '55a95337-6d77-4c96-bb43-2e8cd18c490d' was not found."
D4 DELETE with a Step-Up-Authorization header (ignored?)   200  OK "Account deleted successfully"
      accounts after D4: 2 (EMPTY still listed: False)
D8 DELETE the first user's PRIMARY as another user         403  errorCode=ACCESS_DENIED  detail="You do not have access to this account."
D9 DELETE the first user's FUNDED as another user          403  errorCode=ACCESS_DENIED  detail="You do not have access to this account."
D10 DELETE with the dead cookie                            401  errorCode=AUTH_TOKEN_MISSING  detail="Authentication is required to access this resource."
R1 GET .../full-number at level 1 (the reveal)             403  {"type": "STEP_UP_REQUIRED", "title": "PIN Verification Required", "detail": "This operation requires PIN verification", "requiredLevel": 2, "currentLevel": 1,  X-Auth-Level-Required=2 X-Auth-Level-Current=1
measured 2026-09-06T09:24:42Z  users=del3e3cf98c,del23e3cf98c

## DB checks re-run with the right column names (AuditEvents.ActorUserId / Event; Accounts.IsDeleted)
audit rows for the first user, in order:  36 MoneyDeposited Succeeded Transaction 09:24:41 | 37 AccountDeleted Succeeded Account 09:24:42
Accounts of the first user: Funded IsDeleted=0 Balance 5 | Empty IsDeleted=1 DeletedAt 09:24:42 Balance 0 | Primary IsDeleted=0
SubscriberNotices for the user: none -> a deletion writes NO notice today
```

Four things that transcript settles. **D4 is the sentence this ADR exists for**: a
`Step-Up-Authorization` header on the DELETE was not read — the request answered 200 at level 1
with no PIN enrolled, so the row's "Level 2" was enforced by nothing in either process. **R1 is the
comparison**: the same user, logged in again at level 1, one route later — the reveal — answered
403 `STEP_UP_REQUIRED` from the BFF, which is the level-2 mechanism ADR-0008's table promised for
deletion too. **The transcript settles ownership → the 422 guards → soft delete** (D8/D9 answered
403 on the same accounts D1/D2 answered 422 on; D4 closed the empty spare); the order BETWEEN the
two guards — balance before primary — is not in the transcript (no probe deleted a funded primary)
and is read from `RefuseIfNotClosable`, pinned by a row of the unit Theory
`DeleteAccountAsync_TheGuardsAnswerBeforeThePresenceCheck`, not measured. An `AccountDeleted` row
rode the save (ADR-0044 D1), and **no refusal on the first user's path wrote
a row**: the 422s and the 404 ran between the deposit (sequence 36) and the closure (sequence 37),
and the two sequence numbers are consecutive. The 403s were answered to the SECOND user, whose rows
that query did not read, so they are not measured; at `d93ba10` `AccountService` called nothing that
writes a refusal row, so none could have been written — that is what the code did, not what the
run saw. What was NOT measured in that run, and is not claimed: a headerless DELETE on a spare (the
transcript's only 200 carried a header), and the `StepUpAuthorizations` row count for the probe
user.

The probe users `del3e3cf98c` and `del23e3cf98c` and their rows were left in `AzureBankDev` by that
run: the chain is append-only, and the rows are the record of the measurement.

## Decision

**D1 — A closure costs a PIN.** The row has said so since day one. What the 2026-08-18 note weighed
against it — the two 422 guards make the money case unreachable — is true and is not the point:
from the owner's side the act is irreversible (ADR-0008, 2026-08-18: no endpoint lists or restores a
deleted account, and the owner asking for it by id is told it does not exist), and the account's
history leaves their view with it (`TransactionService` scopes `GET /api/transactions` to
`!a.IsDeleted`). An empty account is not a historyless one. The alternative — strike the row and
close the pair as won't-do — was a legitimate one-paragraph outcome; it contradicts the row the
record has carried since January rather than honouring it. Taken here; Vlad ratifies or reverses
it at the PR, and this paragraph is corrected in place if he reverses. *(Ratified 2026-09-06 by
the merge of PR #154, 19742ff; the frontend half is the frontend follow-up.)*

**D2 — At the API, on the ADR-0042 rail — not a BFF level-2 rule.** ADR-0041 took transfers off the
session gate for three reasons, and each applies to a closure unchanged:

- *A session flag is a property of the session, not of the act* (0041, Context). One PIN entry
  raises the session for `PinValidityMinutes`; a DELETE four minutes after a reveal would ride a
  flag set for a different purpose, on an operation the owner cannot undo.
- *Anything reaching the API directly is unprotected* (0041, Context). D4 above is that sentence
  measured on this endpoint: a header was sent and the API answered 200 without reading it (the
  transcript does not record the header's value, only that D4 carried one). A BFF rule would leave
  `AccountController.DeleteAccount` exactly as it is.
- *The reopen trigger.* `RequiresPinVerification` has two branches — an exact-path set that is
  POST-only and empty, and a prefix × suffix pair that gates `/full-number`. A parameterised DELETE
  fits neither, so the "cheap" BFF option is a THIRD matching branch, which is what ADR-0041's
  trigger (a) names as the condition for reopening its own cross-process lockout gap.

The one exemption ADR-0041 granted does not cover a closure. The reveal keeps the session model
because *"a GET with no body, no amount and no payee has nothing to bind an in-band credential
to"* (`AuthLevelMiddleware`). A closure has a subject — the account id — and ADR-0042's HMAC already
binds an operation name and a `FromAccountId`. Binding a closure to `(AccountDeletion, user,
account)` costs no new field.

**D3 — The binding is `(AccountDeletion, user, account, amount rendered 0)` under the v1 payload.**
`StepUpOperation.AccountDeletion = 2`; string-stored (`HasConversion<string>`, `nvarchar(30)`), so
no migration. `StepUpBinding.ForAccountDeletion(accountId)` is a static factory over the existing
record — `(accountId, null, null, 0m)` — with the reinterpretation written beside the record:
`FromAccountId` is the account the operation is about, `Amount` is 0 for a non-money operation.
ADR-0042's `v1|` rule bumps the version when a field is ADDED; nothing was added, so the payload is
v1 unchanged and a transfer authorisation minted on the same account can never be presented as a
deletion — the operation name is in the hash. Pinned by a unit test that computes the two hashes and
by an integration test that presents a transfer authorisation to the DELETE and reads 401
`AUTHORIZATION_INVALID`. The integration test alone would not hold the operation name in the
payload: a transfer binding already differs from a closure's by its payee and its amount, both
inside the hash, so it is the unit test that does (mutated 2026-09-06 with `operation.ToString()`
removed from `ComputeBindingHash`: only
`ComputeBindingHash_ForAccountDeletion_DiffersFromATransferOnTheSameAccount` went red, the wire test
stayed green — `measure-tests-2026-09-06.txt` in the working-state repo).

**D4 — The mint is `POST /api/accounts/{id}/deletion-authorizations`, operation in the segment.**
Not `/{id}/authorizations`: the transfer mints are `/api/transfers/authorizations` and
`/api/transfers/internal/authorizations` because the resource IS the operation; under an account the
resource is the account, and a second account-scoped authorisation of another kind would collide
with a bare `/authorizations`. Rule for the next one: when the mint hangs off a resource that is not
itself the operation, the segment names the operation. No `[RequireIdempotency]`, for the reason
written on `TransferController`'s mints: minting moves nothing, and a repeat costs a PIN attempt,
which is the point. Body `{pin}`, answer 201 `{authorizationId, expiresAt}` — the same
`StepUpAuthorizationResponse` the transfer mints return, so the SPA's strict unwrap is one schema.

The mint runs ownership → the two 422 guards → `MintAsync`, in that order, so a wrong PIN on a
funded or primary account is refused 422 BEFORE `IPinVerifier` spends an attempt, and a foreign
account is 403 with nothing learned. The 422 PIN_REQUIRED sentence on the mint is generalised from
*"PIN must be set before authorising a transfer."* to *"PIN must be set before authorising this
operation."*; its only pins were two mock lines, and the mock aligns to the measured sentence in
the frontend follow-up. *(Done 2026-09-06 in the frontend follow-up: `handlers.ts`, both transfer
mints and the deletion mint.)*

**D5 — `ConsumedByTransactionId` is NULL for a closure, and the operator query is recorded here.**
`IStepUpAuthorizationService.ConsumeAsync` took a non-nullable `Guid consumedByTransactionId` while
the column has been `Guid?` since ADR-0042. A closure produces no ledger row, and writing the
account id into a column named `…TransactionId` would lie to the evidence pack, whose join is
`a.ConsumedByTransactionId == movement.Id` — a value that is not a movement id can never match and
would only mislead a reader of the raw table. The parameter is now `Guid?`; the transfer call sites
still pass the outgoing transaction id. The consequence is that a closure's authorisation is not
reachable from the closure by a foreign key. It is reachable by this query, and the query is the
record rather than a promise of a later `evidence` verb:

```sql
-- The authorisation a closure spent: same actor, the deletion operation, consumed within a few
-- seconds of the AccountDeleted row. @u is the actor (AuditEvents.ActorUserId); @a the account.
SELECT s.Id, s.Operation, s.Status, s.CreatedAt, s.ConsumedAt, s.ConsumedByTransactionId,
       e.Sequence, e.Event, e.OccurredAt
FROM StepUpAuthorizations s
JOIN AuditEvents e
  ON e.ActorUserId = s.UserId
 AND e.Event = 'AccountDeleted'
 AND e.SubjectId = @a
 AND s.ConsumedAt BETWEEN DATEADD(second, -5, e.OccurredAt) AND DATEADD(second, 5, e.OccurredAt)
WHERE s.UserId = @u
  AND s.Operation = 'AccountDeletion'
  AND s.Status = 'Consumed'
ORDER BY s.ConsumedAt;
```

The five-second window is a reading aid, not a key: the consume and the audit row commit in one
transaction (D8), so on the running stack they are milliseconds apart. The query is run once against
the after-table (row 16) and its output pasted there.

**D6 — The two 422 guards stay AHEAD of the presence check.** This is a deliberate placement
difference from ADR-0042, which put the transfer's `AUTHORIZATION_REQUIRED` at the ownership rung so
that a caller holding no second factor could not use the endpoint to enumerate payee handles one 404
at a time. That argument needs an oracle, and the deletion guards are not one: NON_ZERO_BALANCE and
PRIMARY_ACCOUNT_DELETE reveal only the caller's OWN account state, which `GET /api/accounts` already
hands them. And there is a pin on the wire: `money.contract.test.ts` asserts on the real stack that
a HEADERLESS DELETE on a funded account answers 422 `NON_ZERO_BALANCE` with a figure-free sentence
(its own comment dates that measurement 2026-09-03). Guards-first keeps that suite green on the
interim `main` between this PR and the frontend follow-up; presence-first would turn it red from a
backend-only PR that cannot touch the frontend file. The order of `DeleteAccountAsync` is therefore:
ownership (404/403) → balance (422) → primary (422) → presence (401, D7) → `ValidateAsync` (401
expired/invalid) → the transaction (D8).

**D7 — One refusal event, and the 422s stay log-only.** An absent authorisation writes
`AccountDeletionRefused` through `RecordRefusalAsync` on its own connection, `Detail` =
`AUTHORIZATION_REQUIRED`, subject the account — the shape and the placement of
`MoneyTransferRefused` in `TransferService` (after ownership, so the row names an account the caller
owns; the refusal survives the 401's own rollback). Nothing else on the path writes a row, for
ADR-0044's reasons: the two 422s are business validation the owner can trigger at will from a list
they already hold; ~~a wrong or locked PIN at the mint is unaudited exactly as it is for the transfer
mints (the mint has no `IAuditService`)~~ *(audited at all three mints since 2026-09-11, ADR-0044)*;
EXPIRED and INVALID on the DELETE are unaudited as they are
on a transfer. The ADR-0044 inventory moves by one event and one `RecordRefusalAsync` site and no
log template: the refusal emits no `SecurityEvent {SecurityEvent}` line, following the money-refusal
precedent, so the seventeen logged API sites stay seventeen.

**D8 — An explicit transaction, consume after the save, re-entrancy written into the delegate.**
`ConsumeAsync` is one set-based `ExecuteUpdate`; the `AzureBankDbContext` save funnel opens a
transaction of its own only when the caller has none, and it wraps the chain and the save alone. So
a naive `DeleteAccountAsync` would commit the soft delete and the consume as two autocommits, and a
consume that matched zero rows — the authorisation spent by a concurrent request — would leave the
account closed with nothing spent. The method now adopts `TransferService`'s shape:
`CreateExecutionStrategy().ExecuteAsync` around `BeginTransactionAsync`, the soft delete, the
`AccountDeleted` row, `SaveChangesAsync`, THEN `ConsumeAsync`, then commit. Consume after the save
so that `ConsumeAsync`'s `affected != 1` throw rolls the soft delete back; with a caller transaction
present the funnel applies the chain inside it, so the audit row is visible only after the commit.
Because the retrying strategy re-runs the delegate against the SAME context on a transient fault,
the delegate starts by reloading the account, resetting `IsDeleted`/`DeletedAt`/`UpdatedAt` from the
reload and detaching any `Added` `AuditEvent` a previous attempt left behind — the discipline
`PrepareTransferAttemptAsync` already follows. Around the strategy sits the second loop
`TransferService` has: a `DbUpdateConcurrencyException` — the account's `RowVersion` refusing a
stale write, because a deposit landed between the guard and the save or because a second DELETE
presenting the same authorisation committed first — reloads, jitters and goes round, bounded by
`ConcurrencyRetry.MaxAttempts`. The first version of the method had no such loop, and the eight-way
proof said so: `500,500,500,500,500,200,500,500` (the test's own `_output.WriteLine`, the
implementer's ~10:20Z run on 2026-09-06, filed in the working-state repo as
`plans/account-deletion/measure-tests-2026-09-06.txt` — a test run, not a running-stack
measurement). Two branches at the top of the delegate then decide whether there is still a closure
to make. A reload that shows the account already deleted — an earlier attempt of the same request
whose acknowledgement was lost, or a concurrent winner; the delegate cannot tell them apart and does
not try — throws the same 404 the ownership rung answers any later DELETE with, and writes nothing,
so no second `AccountDeleted` row and no refusal at the consume for a closure that happened. The
reload sees the closed row: `EntityEntry.ReloadAsync` queries the store without the global query
filter, so the entry comes back Unchanged with `IsDeleted` true, never detached (measured
2026-09-06 on SQL Server LocalDB and InMemory, EF Core 10.0.1, for a loser whose flag was never set
and for a retry whose own attempt had set it). A reload that shows the account open runs the two
guards again, so an account funded by the racing deposit is refused 422 rather than closed on the
strength of a balance read before it. Each of the ~~three~~ four *(2026-09-06: the deposit race
gained its own, below)* proofs is a SQL Server test, not a sentence: a pre-consumed authorisation
rolls the soft delete back and the chain still verifies; eight concurrent DELETEs presenting one
authorisation close the account once and every loser answers 404 or 401, never 500 (the test's own
`_output.WriteLine`, three `dotnet test --no-build` runs on the working tree at d93ba10, 2026-09-06
10:40:59Z–10:41:10Z, filed in `measure-tests-2026-09-06.txt`: `404,404,200,404,404,404,404,404`,
`404,404,404,404,404,200,404,404`, `404,200,404,404,404,404,404,404`; three more at 11:24Z in the
same file); a forced transient fault writes `AccountDeleted` once; a deposit that lands between the
first attempt's guard and its UPDATE is refused 422 `NON_ZERO_BALANCE` on the retry with nothing
spent (`ADepositThatRacesTheClosure_IsRefusedOnTheRetry_AndSpendsNothing` — with the guards-again
call deleted it answered 200 and closed the funded account; mutated and restored 2026-09-06, output
in the same file).

### Error codes on `DELETE /api/accounts/{id}`

The three ADR-0042 codes apply unchanged, at the same statuses and with the same uniformity rule.
What this table adds is where they sit among the endpoint's older answers, because the order is a
decision (D6):

| Order | Code | Status | Means |
| --- | --- | --- | --- |
| 1 | `ACCOUNT_NOT_FOUND` / `ACCESS_DENIED` | 404 / 403 | not the caller's account (unchanged) |
| 2 | `NON_ZERO_BALANCE` | 422 | guard, ahead of the presence check (unchanged) |
| 3 | `PRIMARY_ACCOUNT_DELETE` | 422 | guard, ahead of the presence check (unchanged) |
| 4 | `AUTHORIZATION_REQUIRED` | 401 | none presented; empty header binds to null and lands here |
| 5 | `AUTHORIZATION_EXPIRED` | 401 | valid, window passed — re-prompt the PIN, no attempt spent |
| 5 | `AUTHORIZATION_INVALID` | 401 | uniform: unknown, not yours, spent, wrong operation, wrong account |
| — | model-state 400 | 400 | header present but not a UUID, keyed `Step-Up-Authorization` |

The 422s come from the mint too (D4), before the PIN is checked; `PIN_REQUIRED` (422),
`INVALID_PIN` (401) and `PIN_LOCKED` (429) come only from the mint, which is the only place on this
path that can spend an attempt.

## Consequences

**Shipped here (backend only).** The enum member and the binding factory; the nullable consumed-by;
the DTO and validator; `AuthoriseDeletionAsync` and the gated, transactional `DeleteAccountAsync`;
the mint action and the required header on the DELETE, with `[RequireStepUpAuthorization]` so the
document transformer publishes the header as required; `SecurityEvents.AccountDeletionRefused`;
the regenerated `docs/api/openapiv1.json`; unit, integration and SQL Server tests; this record and
the four corrections below.

**The interim `main` is stated, not hidden.** Between this PR and the frontend follow-up the SPA's
delete dialog sends no header and is answered 401 `AUTHORIZATION_REQUIRED`, which `AccountsPage`
renders inline as the problem's `detail` (its fallback for an unmapped code); `sessionMiddleware.ts`
exempts the three step-up codes from its sign-out, measured so far only on `POST /api/transfers` —
the DELETE case is row 12 of the after-table, not a claim made here. The frontend follow-up replaces
the confirm dialog with a PIN step that mints and then deletes with the header, its mock values
copied from the after-table below and from nothing else. The SPA's real-stack contract suite is not
left on the interim behaviour: its two account cleanups (`money.contract.test.ts`) sent a headerless
DELETE that after this PR answers 401 `AUTHORIZATION_REQUIRED` — row 6 above is that request — and
since `call` resolves on every status their `.catch` never fired, so every real-target run would
have left the "Contract Empty"/"Contract Funded" probes listed (`IsDeleted` 0) and appended one
`AccountDeletionRefused` row each. This PR gives `client.ts` a `closeAccount` that mints from the
fixture PIN and deletes with the header, ~~falling back to the bare DELETE where the mint route is
unhandled (the mock, until the follow-up adds it)~~ *(struck 2026-09-06: both targets handle the
mint; the `mint?.status === 201` branch stays as cleanup shape, not as a fallback)*.

*Closed 2026-09-06 by the frontend follow-up (PR number and sha to be filled at its merge):*
`DeleteAccountDialog` mints on the sixth digit and deletes with the header; the mock's delete
handler enforces binding → ownership → guards → presence → validate → spend, and its mint mirrors
the API's. Every mock status, `errorCode` and `detail` on the mint (M0–M5) and on the DELETE
(D1–D12, D16) quotes a row of the after-table (re-measured on `main` @ `19742ff`,
2026-09-06T19:16Z, `measure-after-main-19742ff-2026-09-06.txt` beside the original), and the
expiry pair E1/E2 quotes `measure-after-2026-09-06.txt`. Labelled
NOT measured on this endpoint, in `handlers.ts`'s own mint doc block: the mint's 429 `PIN_LOCKED`
and the four 400 body shapes are the transfer mints' 2026-08-16 rows; the funded-and-no-PIN
combination, a repeat mint on an account already holding a Pending authorisation, and the
header-400 precedence on a funded/unknown id are code, not rows; the 403 `ACCESS_DENIED` rows
(D14/D15) are not modelled at all. The DELETE line joined `sessionMiddleware`'s doc block and
`errorPath.integration` pins it. `closeAccount`'s fallback sentence above no longer applies: both
targets handle the mint.

### Before

The measured block in Context, `main` @ `d93ba10`, 2026-09-06T09:24Z. In one line: a DELETE with a
`Step-Up-Authorization` header answered 200 at level 1 with no PIN enrolled, no refusal on the path
wrote a row, and the reveal for the same user at level 1 answered 403 `STEP_UP_REQUIRED`.

### After

**AFTER — observed 2026-09-06T10:44Z on this PR's working tree, API `:7215`, BFF `:5000`,
`AzureBankDev`** (rows 0–13 are the transcript of `plans/account-deletion/del-after-probe.py` in
the working-state repo, filed beside it as `measure-after-2026-09-06.txt`; row 11 is
`del-expiry-probe.py`'s E1/E2; rows 14–16 are three commands run by hand at ~10:45Z whose output
is appended to that same file), a fresh user, PIN enrolled after the first row, four accounts
(primary, funded with 16, three spares). Every request went through the BFF at level 1; no row
carried `WWW-Authenticate` or `X-Auth-Level-*`, and `GET /bff/auth/me` after the first 401 answered
200 with `authLevel` 1. The sixteen rows are the plan's Step 1.12, in order, expectation beside
observation; none disagreed.

| # | Probe | Expected | Observed |
| --- | --- | --- | --- |
| 0 | mint before any PIN is enrolled | 422 `PIN_REQUIRED` | 422 `PIN_REQUIRED` "PIN must be set before authorising this operation." |
| 1 | mint on the spare, wrong PIN | 401 `INVALID_PIN`; counter 0 → 1 | 401 `INVALID_PIN` "Invalid PIN."; `PinAccessFailedCount` 0 → 1 |
| 2 | mint on the funded account, WRONG PIN | 422 `NON_ZERO_BALANCE`; counter unchanged | 422 `NON_ZERO_BALANCE`; counter still 1 |
| 3 | mint on the primary | 422 `PRIMARY_ACCOUNT_DELETE` | 422 `PRIMARY_ACCOUNT_DELETE` "Cannot delete primary account. Set another account as primary first." |
| 4 | mint on another user's account; on an unknown id | 403; 404 | 403 `ACCESS_DENIED` (row 13's second user); 404 `ACCOUNT_NOT_FOUND` |
| 5 | mint on the spare, correct PIN | 201; row Pending, `AccountDeletion`, consumed-by NULL | 201 "Account closure authorised", `expiresAt` = mint + 2m; row `AccountDeletion Pending NULL` |
| 6 | DELETE the spare, no header | 401 `AUTHORIZATION_REQUIRED`; refusal row; `IsDeleted` 0 | 401 "This account closure has not been authorised."; `AccountDeletionRefused Refused AUTHORIZATION_REQUIRED` committed; account still listed |
| 7 | empty header; whitespace; `not-a-guid` | 401; 401; 400 keyed `Step-Up-Authorization` | 401 `AUTHORIZATION_REQUIRED`; 401 `AUTHORIZATION_REQUIRED`; 400 `{"Step-Up-Authorization":["The value 'not-a-guid' is not valid."]}` |
| 8 | random GUID; a transfer authorisation minted on the same account | 401 `AUTHORIZATION_INVALID`, both | 401 `AUTHORIZATION_INVALID` "This authorisation cannot be used.", both |
| 9 | DELETE the spare with the id from row 5 | 200; `IsDeleted` 1, Consumed, consumed-by NULL, `AccountDeleted` row | 200 "Account deleted successfully"; `IsDeleted` 1 at 10:44:20; `Consumed NULL`; last row `AccountDeleted Succeeded Account` |
| 10 | DELETE the spare again, spent header; again, no header | 404; no second refusal row | 404 `ACCOUNT_NOT_FOUND` both; refusal rows for the user still 3 (rows 6 and 7) |
| 11 | mint, wait past `StepUp:Window` (00:02:00), DELETE | 401 `AUTHORIZATION_EXPIRED`; counter unchanged | 401 `AUTHORIZATION_EXPIRED` "This authorisation has expired. Enter your PIN again to confirm." 130 s after a mint whose `expiresAt` was mint + 2m; counter 0 → 0; row still Pending; `/bff/auth/me` 200 |
| 12 | mint on a spare, deposit into it, DELETE with the valid authorisation | 422 `NON_ZERO_BALANCE`; nothing spent | 422 `NON_ZERO_BALANCE`; authorisation still Pending |
| 13 | another user presents the owner's authorisation; mints on the owner's account; the owner then uses that authorisation | 403; 403; 200 | 403 `ACCESS_DENIED`; 403 `ACCESS_DENIED`; 200 — a refusal at the ownership rung spends nothing |
| 14 | `docs/api/openapiv1.json` | header `required: true`; 200/400/401/403/404/422; mint 201; the mint's 422 names its codes *(expectation added 2026-09-06 after the pre-review; not part of the 10:45Z check)* | `[('Step-Up-Authorization', True)]`, responses 200/400/401/403/404/422; mint 201/400/401/403/404/422/429, body `AccountDeletionAuthorizationRequest`. At 10:45Z the mint's 422 read the bare "Unprocessable Entity" — its `[ProducesResponseType(422)]` outranked the transformer, which had no entry for it. Regenerated later on 2026-09-06 with the attribute removed and the entry added; read from the regenerated document: 422 "Business Rule Violation - the account cannot be closed (errorCode NON_ZERO_BALANCE or PRIMARY_ACCOUNT_DELETE, checked before the PIN is consulted), or no PIN is enrolled (errorCode PIN_REQUIRED)." with the inline business-rule schema (`errorCode` documented), the same shape as the DELETE's |
| 15 | `AzureBank.AuditVerifier verify`; `evidence` for a deposit into a later-closed spare | intact; resolves | `CHAIN INTACT: 55 rows verified.` (at ~10:45Z; later probes on the same database add rows); the pack prints the deposit, "NO AUTHORISATION APPLIES", `#54 MoneyDeposited -> Succeeded` |
| 16 | the D5 operator query for row 9 | one row, Consumed, NULL, seconds from the closure | `01A07651-B064… Consumed 10:44:20.726 NULL`, beside `AccountDeleted` sequence 53 at 10:44:20.719 |
| 17 | SPA — `npm run test:contract:real` + `test:contract:mock`, the three deletion rows | green on both targets | real 22:47Z: 7 files / 68 tests; mock: 7 / 68 — the same assertions, both targets, on main 19742ff + the SPA change |
| 18 | SPA — `npm run test:integration`, the headerless DELETE row | 401 `AUTHORIZATION_REQUIRED`, auth `'authenticated'` | 22:49Z: 5 files / 18 tests; the row observed 401 `AUTHORIZATION_REQUIRED` "This account closure has not been authorised.", auth still `'authenticated'`, cache intact |
| 19 | SPA — `npm run test:e2e`, `deleteAccount.spec.ts` | 2 passed | 20:50Z, Playwright's own headless Chromium against the running BFF: 9 passed, 2 skipped by design, both deletion specs among the passes (a first run at 20:00Z failed two older specs on four probe accounts left by the morning's bare-DELETE cleanups; closed with the mint, then green) |

```
D1 DELETE spare, NO header                                     401  errorCode=AUTHORIZATION_REQUIRED  detail="This account closure has not been authorised."
   /bff/auth/me straight after that 401                        200  authLevel=1
   refusal row: AccountDeletionRefused Refused AUTHORIZATION_REQUIRED
D4 DELETE spare, header not-a-guid                             400  model-state {"Step-Up-Authorization": ["The value 'not-a-guid' is not valid."]}
D6 DELETE spare with that transfer authorisation               401  errorCode=AUTHORIZATION_INVALID  detail="This authorisation cannot be used."
D7 DELETE FUNDED, NO header (guards first?)                    422  errorCode=NON_ZERO_BALANCE  detail="Cannot delete an account with a non-zero balance."
D9 DELETE spare WITH the minted authorisation                  200  OK "Account deleted successfully"
   account row: 1 2026-09-06T10:44:20
   authorisation: Consumed NULL
   last audit row: AccountDeleted Succeeded Account
```

Row 13's third step is worth a sentence: an authorisation that a stranger tried to present is not
spent by the attempt, because the ownership rung refuses before anything reads it — so the owner's
own later DELETE with it succeeds. Nothing about the authorisation leaked to the stranger either;
the 403 is the one every foreign account id gets.

### What moved in other records, all struck in place with today's date

- **ADR-0008**: the Protected Operations row gains its mechanism; the 2026-08-18 *"Recorded as still
  open rather than decided in passing"* and the 2026-09-04 *"still not behind the level-2 gate"* are
  struck; *"One level-2 path"* stays true, because the BFF gate did not change; point 2 of the
  2026-08-12 correction is closed; the history loss of 2026-08-18 stays open.
- **ADR-0041**: *"account deletion still has no PIN check anywhere (C.1)"* is struck; trigger (a)
  gains a note that the closure was gated without touching `RequiresPinVerification`, so the trigger
  did not fire and the cross-process lockout gap still protects only the reveal; *"Two questions,
  two mechanisms"* gains closures.
- **ADR-0042**: a generalisation note — the rail carries a non-money operation, `StepUpOperation`
  has three members, the payload is v1 unchanged, consumed-by is nullable, the idempotency-placement
  paragraph does not apply to a DELETE that carries no `[RequireIdempotency]`, and "Not done" still
  names withdraw.
- **ADR-0044**: the inventory gains `AccountDeletionRefused` — fifteen row-writing events, three
  refusal events of which one is not money, five refusal sites; the `Detail` rule applies to it; the
  security-signal versus business-validation paragraph gains the closure as an instance.
- **ADR-0045 and ADR-0048: no change.** Neither promises a closure notice nor lists one as a
  trigger; a closure notice is a separate, 0047-shaped decision (below).

## Not done, and not pretended otherwise

- **No `SubscriberNotice` on a closure.** Measured above: none is written today, and none is written
  after. ADR-0045's test — could the attacker have prevented the notice — was written for
  authenticator events; whether a closure owes the holder a notice is a decision in ADR-0047's
  shape, taken on its own, not folded into a PR about the gate.
- ~~**A wrong or locked PIN at the mint writes no row**, as at the transfer mints: the mint has no
  `IAuditService`, and adding one there is a change to every mint, not to this one.~~ *(Done
  2026-09-11 as exactly that: one change to every mint, recording `AccountDeletionRefused` here —
  ADR-0044, "Wrong and locked PINs at the three mints".)*
- **EXPIRED and INVALID on the DELETE write no row**, as on a transfer. The transfer precedent
  audits the absent authorisation only, and this ADR follows it rather than widening it in passing.
- **No `authorization:<id>` link on the `AccountDeleted` success row.** It would make a closure
  traceable to its second factor without D5's query, and it would be the first success row to carry
  a non-null `Detail` — ADR-0044 says success rows carry none because the ledger row reaches the
  facts, and an account row has no column that reaches the authorisation. That is a dated ADR-0044
  correction of its own, named here rather than taken as a free rider.
- **The owner's history loss stays open** (ADR-0008, 2026-08-18): `TransactionService` still scopes
  `GET /api/transactions` to accounts that are not deleted, so the closed account's rows leave the
  owner's view. A read-side change, not a gate change.
- **`DeletedAt` and `UpdatedAt` are stamped from two clocks** — `DateTime.UtcNow` in the service and
  the context's `TimeProvider` in `UpdateTimestamps`. ~~Fixing it wants a `TimeProvider` in DI,
  which `AzureBankDbContext` has deliberately not registered until a service needs it; that is a
  DI decision, not a closure one.~~ *(corrected 2026-09-07, with
  [ADR-0050](0050-a-utc-day-bounds-a-users-external-transfers-and-the-mint-says-so-before-the-pin.md):
  the struck sentence was wrong on its facts, not merely superseded. There was no deferred
  registration to land — the framework had registered the clock all along. `AddAuthentication()` in
  `Microsoft.AspNetCore.Authentication` calls `services.TryAddSingleton(TimeProvider.System)`, and
  `Program.cs` reaches it twice before `AddApplicationServices` runs, so `AzureBankDbContext`'s
  optional `TimeProvider` parameter was already being resolved from the container on the tree this
  record describes; found by reading the installed shared framework's IL, and pinned on a bare
  `ServiceCollection` by `DailyLimitOptionsTests.TheFrameworkRegistersTheClockFirst`. What ADR-0050
  adds is the dependency made explicit and app-owned in `AddDailyLimit` — a `TryAdd`, which
  therefore registers nothing in the real host — plus the first REQUIRED consumer,
  `DailyOutflowLimitService`, which needs the ledger's clock to compute the UTC day it sums, and
  `DbContextReceivesRegisteredClockTests`, which pins that the context receives it. The first
  sentence of this bullet stays true: `DeletedAt` and `UpdatedAt` in the services still read
  `DateTime.UtcNow`, so the two-clock question is narrowed to those sites, not closed.)*
- ~~**The deposit-between-guard-and-save race is retried, not proven.** The loop in D8 reloads and
  re-runs the guards, so a racing deposit ends as 422 `NON_ZERO_BALANCE` rather than a closed
  funded account; the eight-way proof drives the second-DELETE race, and no SQL Server test drives
  the deposit one. A loser whose reload finds no row at all — `ReloadAsync` meeting the `IsDeleted`
  filter — is detached with the flag its own attempt set still in memory and takes the 404 branch
  just the same; that is what the code does, and this record does not claim which of the two the
  provider takes.~~ *(struck 2026-09-06, the same day, after the pre-review: the deposit race now
  has its own SQL Server proof, `ADepositThatRacesTheClosure_IsRefusedOnTheRetry_AndSpendsNothing`,
  which goes red with the guards-again call deleted — 200, the funded account closed — and green
  with it: 422, `IsDeleted` 0, balance 5, authorisation still Pending. And there is no "no row"
  branch to hedge on: `ReloadAsync` ignores the global query filter, so a loser's entry comes back
  Unchanged with `IsDeleted` true on both providers — measured, EF Core 10.0.1, LocalDB and
  InMemory — and the 404 branch reads the store's flag.)*
- **No `evidence` verb for closures.** D5's query is the operator's tool until one is written.

## What would change this

- **A second non-money operation wanting a mint.** `StepUpBinding` keeps its money-shaped four
  fields with one reinterpreted; a second such operation is the point at which it gets a
  purpose-built shape and the payload goes `v2|`, per ADR-0042's own rule.
- **A closure notice.** Reopens D7's "nothing else writes" only insofar as a notice is joined to an
  audit row (ADR-0047's shape); the gate does not move.
- **The reveal moving onto a mint.** Then `AuthLevelMiddleware`'s level-2 rule protects nothing,
  ADR-0041's trigger (a) can never fire, and its cross-process lockout gap closes by having nothing
  left to protect.
