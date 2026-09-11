# ADR-0050: A UTC day bounds a user's external transfers, and the mint says so before the PIN

**Status:** Accepted · **Date:** 2026-09-07 · The first slice of the backlog's umbrella entry on
transaction limits and tiering: ONE aggregate bound — the sum of a user's completed outgoing
external transfers in the current UTC calendar day, plus the amount asked for, may not exceed a
configured figure — checked at the mint before the PIN is consulted, again before the transfer's
retry loop, and authoritatively inside the transfer's transaction under a per-user application lock.
Builds on [ADR-0042](0042-a-transfer-authorisation-is-bound-and-spent-once.md) (the rail),
[ADR-0044](0044-the-audit-trail-is-append-only-and-chained.md) (what is audited and what is not),
[ADR-0046](0046-one-money-cap-for-every-move-and-the-client-promises-what-the-contract-publishes.md)
(the per-transaction cap this does not touch) and
[ADR-0049](0049-closing-an-account-is-authorised-like-a-transfer.md) (the mint's guard rung).
Supersedes nothing; the four records it moves carry a dated note each, named at the end.

## Context

**Nothing aggregates.** Measured on `main` @ `3c30122` before any of this was written — BFF `:5000`
→ API `:7215`, the `AzureBankDev` LocalDB store, two throwaway users registered by the probe. The
lines below are the transcript `plans/daily-limit/measure-before-2026-09-07.txt` in the
working-state repo, quoted verbatim; its setup lines are omitted:

```
## daily-limit BEFORE -- 2026-09-07T12:47:41Z -- BFF :5000 -> API :7215, AzureBankDev, main 3c30122 (backend as 19742ff)
=== nothing aggregates per day: five external transfers of 150 in a row (750 of a 1000 balance) ===
T1 mint 150 -> 201; transfer 150                                 201  OK {"newBalance": 850.0}
T2 mint 150 -> 201; transfer 150                                 201  OK {"newBalance": 700.0}
T3 mint 150 -> 201; transfer 150                                 201  OK {"newBalance": 550.0}
T4 mint 150 -> 201; transfer 150                                 201  OK {"newBalance": 400.0}
T5 mint 150 -> 201; transfer 150                                 201  OK {"newBalance": 250.0}
   ledger rows for the sender today (UTC): 5 750.0000
   CreatedAt of the last row vs now UTC: 2026-09-07T12:47:42.861 | now 12:47:43
=== the balance guard, the nearest sibling of a daily refusal: 300 left, ask 400 ===
B1 mint 400 with 250 left                                        201  OK {"authorizationId": "01a07be9-0257-74d7-9adf-be8df029e532", "expiresAt": "2026-09-07T12:49:43.1916117Z"}
B2 transfer 400 with that authorisation                          422  errorCode=INSUFFICIENT_FUNDS  detail="Insufficient funds."  extra={'available': 250.0, 'requested': 400}
   authorisation after the refusal: Pending
B3 withdraw 400 (in-body PIN) with 250 left                      422  errorCode=INSUFFICIENT_FUNDS  detail="Insufficient funds."  extra={'available': 250.0, 'requested': 400}
=== internal transfers and withdrawals also unbounded per day ===
I1 internal 100                                                  201  OK {}
W1 withdraw 50                                                   201  OK {"newBalance": 100.0}
   transaction types written for the sender: Deposit 1 | TransferIn 1 | TransferOut 6 | Withdrawal 1
   statuses ever written in the table: Completed 207
measured 2026-09-07T12:47:43Z  sender=dls1303fa8a recipient=dlr1303fa8a
```

Four things that transcript settles. **T1–T5**: five external transfers of 150 within one second all
answered 201 — the only bound any of them met was the balance. **B1 is the sentence the mint half of
this ADR exists for**: a mint of 400 with 250 left answered 201, because the mint checks nothing
about money; the transfer then refused (B2) and the authorisation stayed Pending, so today a doomed
transfer costs the user a PIN entry before it is refused. **B2/B3 are the precedent for the
refusal's shape**: 422, a figure-free sentence, and numeric extension members (`available`,
`requested`) at the top level of the body, through the BFF. **The ledger's status column is moot**:
every row ever written is `Completed` (207 of 207), so "settled" and "committed" coincide until
something writes `Pending`; and `CreatedAt` is UTC (12:47:42 written, 12:47:43 read).

**The old constant was never a decision.** `ValidationRules.DailyTransferLimit = 1_000` had one
definition and zero readers; ADR-0046 D6 said so and its 2026-09-04 correction deleted it. This ADR
does not restore 1,000 — it was a dead number, not a policy — and it puts the new figure in
configuration rather than back in `ValidationRules`, for the reason in D6.

**The balance guard has no ADR, and this record does not invent one.** PR #111 (*"Cap every outflow
at its account's balance, before the PIN is asked"*, merged 2026-08-15, `4811667`) is frontend-only
— its own body says *"The diff touches no backend file"* — and its gate *"fails open on purpose: it
saves a doomed round trip, it is not the control"*. The server-side funds check it mirrors is two
lines of `TransferService.TransferAsync`: `:322` before the retry loop and `:340` again inside the
execution-strategy delegate after the reload (code says). The daily limit is a NEW server bound with
its own placement, not a copy of the balance guard's — see D4.

**Three facts measured on this machine on 2026-09-07 that the mechanism depends on** (all on
`(localdb)\MSSQLLocalDB`):

- `sys.databases`: `AzureBankDev` `is_read_committed_snapshot_on = 1`, `snapshot_isolation_state =
  0`; `AzureBankTests` 1 / 0; `AzureBankE2E` 1 / 0. Read-committed snapshot is ON here. The CI
  container's `AzureBankProofs` value is not measured at the time of writing — D5 says where it
  lands and why the Actions log will not carry it.
- The ledger discriminator, on `AzureBankDev`: `TransferOut` rows grouped by whether
  `RecipientAzureTag` is set and by the owner of the related row's account — SET and the other
  user's account: 20 rows; NULL and the same user's account: 10 rows; no other combination. So
  "external" == `RecipientAzureTag != null` is a measured fact of the ledger, no longer only the
  inference from the two row constructors (`TransferService.cs:426-439` sets the tag on the external
  row, at `:437`; `:676-688` sets none on the internal one, code says).
- The seeded admin's rows today (UTC): Deposit 3 / 27.00, Withdrawal 1 / 25.00, external
  `TransferOut` 0. The shared real-stack fixture's external day is empty, so a 5,000 default leaves
  every existing test's day untouched and a zero-money contract row is possible (D6).

**What the regulation says, and does not.** Read on EUR-Lex on 2026-09-07 (the consolidated texts;
the exact sentences are filed as `plans/daily-limit/standards-read-2026-09-07.txt` in the
working-state repo, and nothing regulatory is cited here that is not in that file). PSD2 Art. 68(1)
(CELEX 02015L2366-20240408): *"Where a specific payment instrument is used for the purposes of
giving consent, the payer and the payer's payment service provider may agree on spending limits for
payment transactions executed through that payment instrument."* — and Art. 4(14) defines the
instrument as *"a personalised device(s) and/or set of procedures agreed between the payment service
user and the payment service provider and used in order to initiate a payment order"*. That is the
whole legal basis: a spending limit is something the two sides AGREE on; no figure, no window, no
zone is prescribed. Of the RTS (CELEX 02018R0389-20230725), Art. 15 exempts from SCA a credit
transfer *"where the payer and the payee are the same natural or legal person and both payment
accounts are held by the same account servicing payment service provider"* — the precedent D2 uses
for treating an own-account move as a lower class, and nothing more. Art. 11(b) and Art. 16(b) carry
the only cumulative-amount counters in the articles read, and both run *"since the last application
of strong customer authentication"* (Art. 11 says *"from the date of the last application"*): they
reset on SCA, not on a day. The RTS knows no daily aggregate. A UTC-day cap is therefore institution
policy with no wire standard behind it, which is what the document's prose says and why no schema
field carries it (D6).

**The stale wording this ADR names and does not fix.** `docs/design/06-api-contracts.md:843` and
`:901` still publish "max 999999999.99" for the deposit and withdrawal amount, against ADR-0046 D1
and `ValidationRules.TransactionMaxAmount`; `docs/design/frontend-design/04a-ux-user-flows.md:565`'s
"Daily limit" box on the transfer flow becomes true with this ADR, and
`04l-external-transfers-design.md:508`'s "Daily/per-transaction limits (future)" becomes half stale.
The internal mint's bare "Unprocessable Entity" 422 in the published document is an existing
ADR-0049 row-14 instance, named in D8 and left for its own fix.

_Line anchors in this record are those of the working tree that carries this change — the files as
the PR ships them, re-read after the last edit made to them in this session, not `main` @
`3c30122`; only the transcript above is from before. Any later edit to `TransferService.cs` moves
every anchor below it with it, so these are re-checked against the file at commit time._

## Decision

**D1 — The window is the UTC calendar day: `[00:00:00Z, next 00:00:00Z)`.** Policy, not regulation
(Art. 68(1) above prescribes none). Chosen because the ledger already lives there:
`Transactions.CreatedAt` is stamped in UTC by `AzureBankDbContext.UpdateTimestamps` from the
context's `TimeProvider` (`AzureBankDbContext.cs:288-290`, code says; measured UTC in the transcript
above), and `ApplicationUser` carries no time-zone column, so "the customer's local day" has no data
to stand on. A rolling 24-hour window was available and declined for this slice: "remaining" would
then be a continuously moving figure that a client could neither cache nor display with a fixed
reset instant, and rolling windows belong to the umbrella entry by name (D8). The reset instant is
sent on the refusal as `resetsAt` (D7). The day start is computed from the SAME `TimeProvider` the
context stamps with — never `DateTime.UtcNow` — so one fake clock can drive both halves of a
day-boundary test (D6).

**D2 — Only external transfers count: `TransferOut` rows with `RecipientAzureTag != null`.** The tag
is the ledger's only discriminator between the two `TransferOut` writers, measured 20 / 10 above,
and the query depends on it as a fact — pinned by unit tests that an internal row does not count and
an external one does. Excluded, and each exclusion argued rather than assumed:

- **Internal transfers** — the money stays with the user; the balance guard already bounds them; and
  the RTS itself treats an own-account transfer at the same PSP as a lower class (Art. 15, quoted in
  Context — an SCA exemption, cited as a precedent for the class, not as a rule about limits).
- **Withdrawals** — a different rail: in-band PIN, no mint, one `SaveChangesAsync` with no explicit
  transaction (`TransactionService.cs:235`, code says). Putting them under an aggregate means
  restructuring `WithdrawAsync` into the strategy-plus-transaction shape and moving a business check
  ahead of its PIN — a contract-visible ordering change ADR-0042 (*"Withdraw … should follow this
  route as its own task"*) reserved for the withdraw convergence, and this slice does not half-do
  it.
- **Deposits and `TransferIn`** — inflows never offset an outbound bound.

The name is honest about the scope: a daily TRANSFER limit, matching the backlog's wording and the
design flow's "Daily limit" box.

**D3 — Per user, completed rows only, and NO `IsDeleted` filter.** The customer is the payer (Art.
68(1)), and the backlog says per user per day; a user with two accounts would otherwise hold two
limits. `Transaction` has no `UserId`, so the sum joins `Accounts.UserId`. `Status == Completed`
mirrors `GetSummaryAsync` at zero cost — every writer sets `Completed` and the table holds nothing
else (207 / 207 measured), so the filter decides nothing today and is stated so that whoever first
writes `Pending` revisits it. The join deliberately does NOT copy `GetSummaryAsync`'s
`!Account.IsDeleted`: closing an account requires a zero balance (`NON_ZERO_BALANCE`), so a
drained-then-closed account's earlier outflows that day are real money that left the user, and
filtering them out would hand the user the day's headroom back by closing an account. Because the
soft-delete filter on `Account` is a GLOBAL query filter that reaches the sum through the
navigation, the query says `IgnoreQueryFilters()` explicitly — omitting the predicate alone would
not be enough. Pinned by a unit test that a row on a soft-deleted account of the same user still
counts.

**D4 — Three checks, in this order: daily → balance, and the mint's check BEFORE the PIN.**

1. **At the external mint**, `AuthoriseTransferAsync`, between `ResolveExternalPayeeAsync`
   (`TransferService.cs:197`) and `_stepUp.MintAsync` (`:222`) — the call at `:220`, on ADR-0049
   D4's rung: a 422 guard that reveals only the caller's own state runs BEFORE `IPinVerifier`
   spends an attempt. The aggregate reveals only the caller's own ledger, so it belongs there. This
   is why B1 is in Context: the mint checks nothing about money today, so a doomed transfer costs a
   PIN entry; after this ADR an over-limit mint with a WRONG PIN answers 422
   `DAILY_LIMIT_EXCEEDED`, spends no attempt and mints no row. The balance guard is deliberately
   LEFT unchecked at the mint (its own rule is that funds are checked at the transfer; ADR-0046's
   2026-09-04 correction measured a mint of 1,000 answering 201 on a balance of 16) — the
   asymmetry is recorded, not hidden. The internal mint gets no call, with a one-line comment
   saying why (D2).
2. **Before the transfer's retry loop**, `TransferAsync`, after `_stepUp.ValidateAsync` (`:295`)
   and before the loop's balance check (`:322-324`) — the call at `:312`: cheap, nothing written.
   An absent authorisation still wins first (`RequireAuthorization` at `:278`), and an expired or
   invalid one too (`ValidateAsync` at `:295`), so the check is not an oracle for a caller holding
   no second factor — ADR-0042's placement argument is intact. Daily BEFORE balance is a decision:
   the mint has no balance check, so keeping daily first makes mint and transfer refuse in the
   same order, and a request violating both bounds answers `DAILY_LIMIT_EXCEEDED`. Expected from
   the code, and measured as A4 on the transfer path in the After block: with `used` 4,900 and a
   balance of 300, a spend of 400 answered `DAILY_LIMIT_EXCEEDED`, not `INSUFFICIENT_FUNDS`.
3. **Inside the transaction**, the authoritative check — D5.

**The midnight edge is the definition, not a bug.** A mint refused at 23:59:59Z and retried at
00:00:01Z succeeds because the window rolled; a mint that passed at 23:59:30Z and is spent at
00:00:10Z is decided by the spend-time check against the new day. The mint is a courtesy that saves
a PIN entry; the transaction check is the control.

**D5 — The control is a per-user application lock, taken first, with the sum before any row.**
Inside the execution-strategy delegate, immediately after `BeginTransactionAsync`
(`TransferService.cs:345`) and before the rows are built (`:426`), guarded on
`_context.Database.IsRelational()` as `ConsumeAsync` and `AuditChain` already guard their raw SQL,
one batch goes through `ExecuteSqlRawAsync` with the resource as its parameter
(`TransferService.DailyLimitLockSql`, resource `daily-limit:{userId:D}`):

```sql
DECLARE @r int;
EXEC @r = sp_getapplock @Resource = N'daily-limit:{userId}', @LockMode = N'Exclusive',
     @LockOwner = N'Transaction';
IF @r < 0
BEGIN
    DECLARE @m nvarchar(80) = CONCAT(N'sp_getapplock returned ', @r);
    THROW 50000, @m, 1;
END
```

then the same sum over COMMITTED rows, then `used + requested > limit → throw`. The batch captures
the procedure's RETURN VALUE and raises when it is negative, because `sp_getapplock` reports a
refused lock as a return value and not as an error (−1 timed out, −2 cancelled, −3 deadlock
victim, −999 bad call) and `ExecuteSqlRawAsync` cannot see a return value: without that guard a
refused lock would read as a lock held and the sum would run unserialised in silence. The loser
therefore refuses before it has written anything — no ledger row, no audit row, nothing to roll
back. A `RowVersion` retry (the balance guard's loop) re-enters the delegate and re-takes lock and
sum, so the same-account race is covered by the same statement.

_Noted 2026-09-07 (review round 1, `limits/daily-external-transfers`): **the batch above waited
without a bound, and it now takes one.** As written it passed no `@LockTimeout`, so `sp_getapplock`
waited at `@@LOCK_TIMEOUT` — measured **-1, wait forever**, read off the waiting connection itself
on LocalDB — and the only thing that could end the wait was the global 30-second `CommandTimeout`
set in `AddInfrastructure` (`sqlOptions.CommandTimeout(30)`). Under same-payer contention that is a
transaction and a pooled connection held for half a minute per queued transfer, which is a cost
this decision never argued for. Measured beside it: a holder took the lock in one transaction and a
second connection asking with `@LockTimeout = 2000` was refused `-1` after **2,006-2,012 ms across
three runs** (`plans/daily-limit/measure-cr1-2026-09-07.txt`, run 1). The batch now declares
`@t int = {1}` and passes it as `@LockTimeout`; the bound is `DailyLimit:LockTimeoutSeconds`,
default **10**, `[Range(1, 29)]` and `ValidateDataAnnotations().ValidateOnStart()` — above
`Audit:TailTimeoutSeconds` (5), because the holder's own audit tail read sits INSIDE the span a
waiter waits on, and strictly below the 30-second command timeout, so the refusal is always this
bound's and never the statement's. The `IF @r < 0` guard stays and its message now distinguishes
`-1` — *"sp_getapplock timed out after N ms waiting for the payer's daily-limit lock"* — from every
other negative return, because **a timeout is a fault, not a limit refusal**: it says the server was
busy, and it must never surface as `DAILY_LIMIT_EXCEEDED` with figures nothing computed.
`DailyLimitConcurrencySqlServerTests`'s
`TheApplock_RefusesInsideTheConfiguredBound_WithAMessageThatNamesTheTimeout`
asserts the ELAPSED time against the configured bound rather than the exception alone — an
assertion on the throw would have been green under the unbounded regime too. The SQL block above is
left as it was written; this note is the correction._

*Why not the audit tail.* Two transfers from two DIFFERENT accounts of one user to two DIFFERENT
payees touch no shared row, so the `Account.RowVersion` loop never fires (with ONE payee it does,
on the payee's row — the finding below); the only other thing that serialises them today is the
audit chain's global tail lock, held from the first audited save to commit. A re-sum under that
lock is correct as the code stands (verified while designing: the chain is applied inside the
caller's transaction, `HOLDLOCK` is held to commit, and under read-committed snapshot the `SUM` is
a statement snapshot taken after the winner's commit). It was declined because it would make a
business invariant depend on the lock ADR-0044 lists as an open question — *"Whether the chain
should be partitioned is a real question this ADR does not answer"* — so partitioning later would
break the limit with every InMemory test still green. That alternative is kept on record beside
ADR-0044's paragraph; it is the one Vlad may prefer at the PR, and this paragraph is corrected in
place if he does.

*Why not a row lock on `AspNetUsers` or `Accounts`.* The first design took `SELECT … FROM
[AspNetUsers] WITH (UPDLOCK, HOLDLOCK)` on the user row and claimed no ordering cycle. The claim is
false, and it was found in code, not in a run: an AzureTag rename (`UserService.cs:96-118`) and a
PIN enrolment or change (`AuthService.cs:672-743`, `_userManager.UpdateAsync` through the same
context) modify the user row in a save that carries an audit row, and
`AzureBankDbContext.SaveChangesAsync` applies the chain — tail `UPDLOCK`/`HOLDLOCK` — BEFORE
`base.SaveChangesAsync`. Those paths lock tail → user row; a transfer holding the user row and then
saving would lock user row → tail: a 1205 cycle whenever a user renames or changes a PIN while
transferring. `EnableRetryOnFailure` retries 1205 (the Infrastructure registration's comment, code
says), so the victim would be retried rather than 500 — but an ADR must not deny the cycle. The same
analysis kills `Accounts WITH (UPDLOCK) WHERE UserId = @u`: a deposit locks tail → `Account` row, a
transfer locking the account rows first and then the tail closes the cycle from the other side. That
paragraph is the second proposal's deposit-deadlock analysis, credited here because it is what
showed the lock order had to be thought through rather than asserted.

*Why `sp_getapplock` has neither defect.* An application lock participates in no table-lock order,
nothing else in `backend/src` takes one (grep: none), so the only order this ADR adds is applock →
tail and no cycle can form; it does not depend on the chain, so partitioning the chain later changes
nothing here; and "sum before rows are built" keeps the loser from writing. Its cost is one raw
statement with no repo precedent beyond `AuditChain.TailSql`, accepted and stated. It holds under
READ COMMITTED and under read-committed snapshot (measured ON here, above) — the `Exclusive` lock
blocks the second transaction until the first commits, and the sum then reads committed rows either
way. It does NOT hold under transaction-level `SNAPSHOT`, which nothing in the repository sets (grep
`IsolationLevel` in `backend/src`: none).

*Isolation, both databases.* LocalDB measured 2026-09-07 (`is_read_committed_snapshot_on` /
`snapshot_isolation_state`): `AzureBankDev` 1/0, `AzureBankTests` 1/0. CI's `AzureBankProofs`
container is NOT measured here, and this record does not promise a paste that cannot be kept: the
applock sanity test writes that row through `ITestOutputHelper`, and `ci.yml:126-130` runs the SQL
job with `--logger "trx;LogFileName=sql-test-results.trx"` alone, so a PASSING test's output
reaches no console — the Actions log will never carry it. It lands in the trx, uploaded by
`ci.yml:150-155` as the `backend-sql-test-results` artifact: open the `<UnitTestResult>` for
`TheApplock_ParsesUnderTheRetryingStrategy_BlocksASecondConnection_AndReleasesOnCommitAndRollback`
and read the `sys.databases [AzureBankProofs]: …` line inside its `<Output><StdOut>`. Nothing
asserts that row — it is recorded, not gated — and the applock holds under READ COMMITTED and
under read-committed snapshot alike (above), which is why nothing here waits on it.

*What the reproduction taught about the payee's row.* The premise "no shared row" holds only for
transfers to DIFFERENT payees. A transfer also UPDATEs the PAYEE's `Accounts` row, which carries a
`RowVersion`, and `ConcurrencyRetry.ShouldRetry` accepts a conflict on ANY `Account` entity: with
every transfer paying the same recipient, the payee's row serialises them, the loser re-enters the
delegate and re-sums after the winner's commit, and the day holds whether or not the applock
exists. Measured, working tree 2026-09-07 (the fence below): the panel's shape — two accounts, one
payee — stayed green with the applock removed on every run, and so did eight accounts paying one
payee; a second mutant with the in-transaction check removed as well answered eight 201s and a
day of 800, so the requests do overlap at the pre-check. The race the applock closes is between
two accounts paying different payees, and the two cross-account proofs give every transfer its own
payee. The one-account proof does not — it registers ONE recipient for all eight transfers — so by
the finding above the payee's row serialises that shape with or without the applock, and it is not
a proof of the lock; Verification says so beside it. Even so the natural window between a loser's
sum and a winner's commit is a few milliseconds on LocalDB, so the two cross-account proofs
stretch it with the fixture ADR-0044's contention proof already uses —
`SlowAuditTailInterceptor` holds the audit tail for one second on the first writer to reach
it, and `Fired` is asserted so a run in which nothing stalled cannot pass as a measurement. The
stall changes the odds, not the mechanism: with the lock the seven wait on the applock BEFORE
summing, and each then sums the day as it stands.

*The proof boundary, stated as `ConsumeAsync` states it.* InMemory skips the statement (the
`IsRelational()` branch) and proves only that the check exists and where it sits; the concurrency
property is proven only by the SQL Server test, which runs only with `AZUREBANK_TEST_SQLSERVER` set
and is a skip, not a pass, without it. Written as a REPRODUCTION first, in ADR-0044's own
discipline: run with the `sp_getapplock` batch commented out, expected to fail with more than
floor(limit / amount) 201s and a ledger sum above the limit, then run green with it. The
eight-account proof is the vehicle that goes red on every run; the two-account mixed-load proof
went red once and green once without the lock (the source accounts' own `RowVersion` retries mask
it by timing) and is kept as the deadlock proof. The lines are the implementing run's, as it
reported them — working tree, 2026-09-07, LocalDB `AzureBankTests`; a report of the runs, not the
raw console:

```
MUTANT A (applock commented out of TransferService, panel shape: two accounts, one payee, no stall) — `dotnet test AzureBank.slnx -c Release --no-build --filter "FullyQualifiedName~TwoAccountsOfOneUser"` x3: transfer statuses 201,201,201,422,201,422,422,201 | 422,201,201,422,201,201,422,201 | 201,201,201,201,201,422,422,422 — ledger sum 500.0000 each time; concurrent deposit 201, rename 200, wrong-PIN mint 401; captured warning+ lines 46-48, mentioning 1205/deadlock: 0. GREEN WITHOUT THE LOCK.
MUTANT A, eight accounts one payee, no stall, x3: 201,422,422,201,422,201,201,201 | 201,201,201,201,422,201,422,422 | 422,201,201,201,422,422,201,201 — sum 500.0000 each. Still green.
MUTANT B (applock AND in-transaction check commented out; pre-check only) x2, both proofs: transfer statuses 201,201,201,201,201,201,201,201 — ledger sum 800.0000 — used seen by the losers: (none). Proves the requests overlap at the pre-check.
MUTANT A + SlowAuditTailInterceptor(1 s) + ThreadPool.SetMinThreads(128,128) (processors=8), one payee, x2: eight-account 422,201,201,422,201,422,201,201 and 201,201,422,201,422,201,201,422, sum 500, used seen by the losers 500.0000,500.0000,500.0000 — still green: not the thread pool; the losers re-summed AFTER five commits, i.e. the payee row's RowVersion retry.
MUTANT A + stall + DISTINCT PAYEES (the reproduction, `--filter "FullyQualifiedName~EightAccountsOfOneUser|FullyQualifiedName~TwoAccountsOfOneUser"`), run 1: EightAccountsOfOneUser FAILED — Expected statuses.Count(s => s == 201) to be 5 ... but found 8; transfer statuses: 201,201,201,201,201,201,201,201; ledger sum of today's external TransferOut for the user: 800.0000; used seen by the losers: (none). TwoAccountsOfOneUser FAILED — found 6; transfer statuses: 201,201,201,422,201,201,422,201; concurrent deposit 201, rename 200, wrong-PIN mint 401; ledger sum 600.0000; used seen by the losers: 600.0000,600.0000; captured warning+ lines 45, 1205/deadlock 0. Run 2: EightAccounts FAILED again (8 x 201, sum 800.0000); TwoAccounts passed (201,201,422,201,201,422,422,201; sum 500; losers saw 500).
GREEN (applock restored), whole class `--filter "FullyQualifiedName~DailyLimitConcurrencySqlServerTests"`, three runs (two before the comment re-wraps, one on the final build), all 4/4 passed: spike — sys.databases [AzureBankTests]: is_read_committed_snapshot_on=1 snapshot_isolation_state=0; sp_getapplock (first connection, inside the transaction) returned 0; sp_getapplock (second connection, @LockTimeout = 0) while held returned -1; released on commit and on rollback (probe 0). EightAccounts e.g. 201,422,201,422,201,422,201,201 sum 500.0000, losers saw 500.0000 x3. TwoAccounts e.g. 201,422,201,422,201,201,422,201, deposit 201, rename 200, wrong-PIN mint 401, sum 500.0000, losers saw 500 x3, captured warning+ lines 46, mentioning 1205/deadlock: 0. OneAccount (RowVersion retry path) e.g. 201,201,201,422,422,201,422,201 sum 500.0000.
```

*The comparison, so a refactor cannot double-count.* Both the pre-check and the in-transaction check
compare `used + requested > limit` on COMMITTED rows, because the in-transaction call runs BEFORE
this request's rows exist. A future edit that moves the call after the first `SaveChangesAsync` —
where the request's own uncommitted `TransferOut` row is on this connection and IS in the sum — must
switch to `used > limit`, or it refuses every transfer at exactly the limit. The helper's comment
says this beside the query.

**D6 — The number is an option, 5,000, validated on start; and the ledger clock's DI registration
becomes app-owned.**
`DailyLimitOptions` (section `DailyLimit`, `Amount`, default 5,000) in `AzureBank.Shared/Options`
beside `StepUpOptions`, bound with `ValidateOnStart` — `> 0` and at most two decimals, so no
sub-cent noise reaches the 422 body — and written explicitly in `appsettings.json` so the figure is
visible, not only defaulted. An option rather than a `ValidationRules` constant because the umbrella
needs tiers, because a test factory overrides an option with one setting, and because the aggregate
is NOT a schema bound: it has no JSON-schema slot, so `MONEY_MAX` stays one number, ADR-0046's
`PublishedMoneyBoundsTests` and the forms' tripwire are untouched, and its "second bound" trigger
fires only in part (its dated note says which part). Why 5,000: below `TransactionMaxAmount`
(100,000), so one per-transaction-valid mint of 5,000.01 is refused by the daily bound with zero
money moved (the cheap contract row); above any automated real-stack traffic by two orders of
magnitude, so the shared fixture's day (empty today, measured) is never exhausted by a test run; and
not 1,000, which was never a decision. The consequence a reader of ADR-0046 will look for: a single
external transfer can never reach the published per-request `maximum` on a fresh day. The schema
bounds one REQUEST and stays honest; the effective field bound for an external transfer is
min(`MONEY_MAX`, remaining), and showing it is U8's work (D8).

`AddDailyLimit` calls `services.TryAddSingleton(TimeProvider.System)`, and this record has to be
exact about what that does. It is NOT the first registration, and the sentence this ADR first wrote
— that the context's deferred registration "landed here" — was wrong. The framework registers the
clock already: `AddAuthentication()` in `Microsoft.AspNetCore.Authentication` itself does
`services.TryAddSingleton(TimeProvider.System)`, and `Program.cs` reaches it twice before
`AddApplicationServices` runs (`AddIdentityServices` → `AddIdentity` → `AddAuthentication`, then
`AddJwtAuthentication` → `AddAuthentication(options => …)`). So on `main` @ `3c30122` the container
already held it and `AzureBankDbContext`'s optional parameter was already being resolved from DI
rather than falling through to its default; in the real host this `TryAdd` registers nothing. Found
while this ADR was reviewed — read in the IL of the installed shared framework
(`Microsoft.AspNetCore.App` 10.0.11), not observed on a running host — and pinned on a bare
`ServiceCollection` by `DailyLimitOptionsTests.TheFrameworkRegistersTheClockFirst`, which asserts
the framework's descriptor is there first and that this `TryAdd` keeps it.

What this ADR adds is therefore not the registration but three things. The dependency becomes
app-owned and explicit beside the service that needs it, so `AddApplicationServices` is
self-sufficient rather than leaning on a side effect of the auth registration
(`DailyLimitOptionsTests` builds this root without authentication); `TryAdd` precisely so it can
never displace the
framework's descriptor or a test's earlier `FakeTimeProvider`. It adds the first REQUIRED consumer:
`DailyOutflowLimitService` takes `TimeProvider` as a NON-optional parameter, so the app fails to
resolve rather than run on a second clock. And it adds the pin:
`DbContextReceivesRegisteredClockTests` shows a saved row's `CreatedAt` equals the registered fake
clock's instant rather than trusting the comment. The context reaches that singleton through
`AddDbContext`'s constructor resolution (the route `IAuditChain` already takes) in the API root
only; the `AzureBank.Seeder` and `AzureBank.AuditVerifier` roots register the context through
`AddInfrastructure` alone, register no `TimeProvider`, and stamp from the parameter's default — the
same system clock, reached by a different path. The window, sum and assertion live in ONE scoped
helper (`IDailyOutflowLimit` / `DailyOutflowLimitService`) so the three call sites cannot drift and
the withdraw slice can reuse it later. ADR-0049's deferral is answered for the ledger clock only,
and its premise corrected: `DeletedAt` / `UpdatedAt` and the step-up mint and expiry in the services
still read `DateTime.UtcNow`, so the two-clock question is narrowed, not closed.

**D7 — The refusal: `422 DAILY_LIMIT_EXCEEDED`, figure-free, four numeric extension members.**
`DailyLimitExceededException : BusinessRuleException`, sentence *"Daily transfer limit exceeded."*
with no digits (the rule `InsufficientFundsException` records: figures travel as numbers and are
formatted ~~in the user's locale~~ *(by the client, in a fixed en-IE locale — corrected 2026-09-11:
the client never reads the user's)*), `Details {limit, used, requested, resetsAt}` spread into the
problem body's top level by `AppExceptionHandler` exactly as `available` / `requested` ride today
(B2/B3 in Context). Undeclared in the published document by that precedent — ADR-0043's component
declares seven members and `available` is not among them — and NOT yet typed on the client either:
`ApiProblem` (`frontend/src/api/problemBaseQuery.ts:23-37`) carries neither these four nor
`available` today, and the SPA's `INSUFFICIENT_FUNDS` branch (`moneyProblem.ts:151`) reads no
extension member. The frontend mirror PR (Consequences, "The interim `main` is stated") is what
adds `limit` / `used` / `requested` / `resetsAt` as optional members of `ApiProblem` and derives
`remaining` there as `limit − used`; `remaining` is deliberately not sent, so a fourth number cannot
become a second source of truth. Declaring extension members in the DOCUMENT is ADR-0043's own
follow-up, not this one. On the wire the mint's 422 description names the code — and, the bare
reason phrase gone, all four codes that action answers: `SELF_TRANSFER_NOT_ALLOWED` and
`RECIPIENT_NO_ACCOUNT` from the payee resolution, then `DAILY_LIMIT_EXCEEDED`, then `PIN_REQUIRED`
— because the `[ProducesResponseType(422)]` that outranked the transformer on `AuthoriseTransfer`
is removed (ADR-0049 row 14's trap, and the fix the deletion mint `AuthoriseDeletion`
(`AccountController.cs:196`) already took); and `POST /api/transfers`' 422 prose gains the code.
Log-only, no audit row: ADR-0044's rule for business validation the owner can trigger at will
from state they hold — an unbounded write into a
never-purged table, each attempt taking the tail lock — with the absence asserted by a test in the
`AWithdrawalRefusedForFunds_WritesNoRow_AndThatIsTheDecision` shape. Never replayed: a 422 releases
the idempotency claim (ADR-0009), so the same key retried is re-evaluated and answers 422 again with
no `Idempotency-Replayed` header. The authorisation stays Pending after a transfer-path refusal, as
B2 measured for the balance guard; it can be spent later only within its two-minute life.

_Noted 2026-09-07 (review round 1): **the four members are now DECLARED in the document**, which the
paragraph above says they are not. The sentence *"Undeclared in the published document by that
precedent"* rested on `available` / `requested`, and review asked what that precedent decided.
Verified: `available` appears **zero** times in `docs/api/openapiv1.json`, so `INSUFFICIENT_FUNDS`'s
members are an OMISSION and not a ruling — while ADR-0043's own thesis is that the document declares
the error body so a generated client can branch on it. Verified too that this is not a new practice
here: the 422 responses on `POST /api/transfers`, `POST /api/transfers/authorizations` and the
deletion mint are already INLINE object schemas built by `BusinessRulesDocumentTransformer` (and, on
the idempotent one, `IdempotencyOperationTransformer`), listing `type` / `title` / `status` /
`detail` / `errorCode` / `traceId` — not a `$ref` to the ProblemDetails component. So `limit`,
`used` and `requested` (`number`) and `resetsAt` (`string`, `format: date-time`) are declared on
exactly the two operations that can answer the code, each description saying it rides
`DAILY_LIMIT_EXCEEDED` only; the shared component is untouched and every other operation's 422
schema is byte-identical. `schema.d.ts` gained the four as optional typed members on both
operations, which is the point — a GENERATED client can now branch on them without hand-written
types. The SPA still cannot, and this note does not claim otherwise: its hand-written `ApiProblem`
(`frontend/src/api/problemBaseQuery.ts:23-37`) carries none of the four and `moneyProblem.ts` reads
no extension member, so the paragraph above stays true on the client half — the frontend mirror PR
is what consumes what is now typed.
`PublishedDailyLimitTests` asserts the members and their types on both operations, and their ABSENCE
on the four money moves that check no aggregate plus the deletion mint. **What this does not do:**
`INSUFFICIENT_FUNDS`'s own `{available, requested}` remain undeclared everywhere. That spans more
operations than these two and is its own small PR; it is named here and asserted in the same test
file so the gap stays a known omission rather than a silence that the next reader mistakes for a
decision._

_Noted 2026-09-08 (the frontend mirror PR): **the client half of the two paragraphs above is now
false for the four members, and still true for `available`.** `ApiProblem`
(`frontend/src/api/problemBaseQuery.ts`) carries `limit` / `used` / `requested` / `resetsAt` as
optional members, `toApiProblem`'s return literal spreads them so they survive normalization, and
`moneyProblem.ts` composes the refusal sentence from them - `limit - used` through the one
`formatCurrency`, `resetsAt` through the existing `formatDateTime`. `available` is untouched and
stays the separate small PR named above. One correction the mirror found while consuming them: the
descriptions read *"DAILY_LIMIT_EXCEEDED only"* on all four, and for `requested` that over-declares.
Measured 2026-09-07T14:17:53Z (A4.4): `INSUFFICIENT_FUNDS` on `POST /api/transfers` carries
`{"available": 300.0, "requested": 400}`. The document under-declares `INSUFFICIENT_FUNDS`, it is
not the code that is wrong, and it is the same gap the paragraph above keeps open - so no consumer
may infer the daily refusal from `requested`'s presence. The client branches on `errorCode` and says
so where the members are declared._

**D8 — Not decided here, named so the umbrella finds them.** Rolling windows; tiers by account type
or verification level; amount-scaled step-up; velocity rules; withdrawals and internal transfers
under any aggregate; per-account limits; customer-adjustable ceilings (with SCA on a raise); a
"remaining today" read — `GET /api/transactions/allowance` is named as the seam such a surface would
read from, assigned to U8 (last, as always) and not built; auditing the refusal as a fraud signal;
an API-side per-user limiter on the external mint — the bound the extra query before the PIN does
NOT have (Consequences, "Cost accepted"); the internal mint's bare "Unprocessable Entity" 422 in
the document (an existing ADR-0049 row-14 instance: `TransferController.cs:103` carries the
attribute and the transformer has no entry — its own small fix); partitioning the audit chain
(ADR-0044's question, untouched by D5).

### Error codes on the external-transfer rail after this ADR

| Where | Order | Code | Status | Means |
| --- | --- | --- | --- | --- |
| mint | 1 | `ACCOUNT_NOT_FOUND` / `ACCESS_DENIED` | 404 / 403 | not the caller's account (unchanged) |
| mint | 2 | `ACCOUNT_NOT_FOUND` (an unknown handle answers the same 404 code, `NotFoundException` says) / `SELF_TRANSFER_NOT_ALLOWED` / `RECIPIENT_NO_ACCOUNT` | 404 / 422 / 422 | payee resolution (unchanged) |
| mint | 3 | `DAILY_LIMIT_EXCEEDED` | 422 | **new** — before the PIN is consulted |
| mint | 4 | `PIN_REQUIRED` / `INVALID_PIN` / `PIN_LOCKED` | 422 / 401 / 429 | the mint's own (unchanged) |
| transfer | 1 | `AUTHORIZATION_REQUIRED` | 401 | ADR-0042, above the payee (unchanged) |
| transfer | 2 | payee resolution | 404 / 422 | unchanged |
| transfer | 3 | `AUTHORIZATION_EXPIRED` / `_INVALID` | 401 | ADR-0042, validated where the payee is known (unchanged) |
| transfer | 4 | `DAILY_LIMIT_EXCEEDED` | 422 | **new** — before the balance, pre-check then in-transaction |
| transfer | 5 | `INSUFFICIENT_FUNDS` | 422 | unchanged |

## Consequences

**Shipped in the backend PR.** The option and its registration, the `appsettings.json` value, the
explicit `TimeProvider` registration (app-owned, not the first one — D6), the error code and
exception, the helper, the three checks with the
per-user application lock, the attribute removal, the two transformer entries and the composition
in `IdempotencyOperationTransformer` that lets them reach the document, the regenerated document
and the generated `schema.d.ts` committed together (so the drift gate stays green; `apiSchemas.ts`
came out unchanged), the context comment, the test factory's `SetDailyLimit`, fake-clock swap and
log capture, the tests in Verification, this record and the dated notes below.

**The document diff is wider than the two entries, and this is why.** On a `[RequireIdempotency]`
endpoint the 422 is written by `IdempotencyOperationTransformer`, which runs before the document
transformers, and `Add422Response` returns when a 422 already exists — so until this change the
three money entries in `BusinessRulesDocumentTransformer` (transfer, internal transfer, withdraw)
never reached the document, which carried the idempotency transformer's generic blurb instead, and
a code named only in that table would have published nothing. That transformer now composes the
per-endpoint entry with its own `IDEMPOTENCY_KEY_REUSE` clause. The regenerated document therefore
also changes the 422 description of `POST /api/transfers/internal` and
`POST /api/transactions/withdraw` — their previously dead prose, naming no daily code, which
`PublishedDailyLimitTests` asserts — and the external mint's 422 schema goes from the `$ref` to the
transformer's inline object, its 422 key sorting after 429, as the deletion mint's did when its
attribute went. The same composition NARROWS one description. `POST /api/transactions/deposit` is
the only `[RequireIdempotency]` endpoint with no entry in the table, so it is now the sole consumer
of the fallback blurb — whose `(e.g. INSUFFICIENT_FUNDS)` clause named a refusal a deposit cannot
answer, `DepositAsync` throwing no `BusinessRuleException` at all. The clause is dropped and the
deposit's 422 keeps only its idempotency sentence, pinned so it cannot come back.

**The interim `main` is stated.** Until the frontend mirror lands, the SPA renders the server's
sentence for the unmapped code through its `problem.detail || fallback` branch in both the money
classifier and the withdraw dialog — a correct refusal in the server's words, not a crash. The
mirror (a separate PR, from the measured transcript and from nothing else) adds the typed members,
the classifier copy, the mock's two rungs with a ledger-sum helper rather than a counter, and one
zero-money contract row on both targets: a 5,000.01 mint on the seeded admin → 422 with `used` as
measured. `moneySchemas.ts` is untouched and said so in that PR.

_Noted 2026-09-08 (the frontend mirror PR, landing): **the first sentence above is now half past
tense, and was always half wrong.** The CLASSIFIER half is superseded - `moneyProblem.ts` has a
`DAILY_LIMIT_EXCEEDED` branch in the shared tail that composes the sentence from the figures and
falls back to `problem.detail || fallback` only when `limit` or `used` is absent. The WITHDRAW
DIALOG half was never reachable: D2 excludes withdrawals from the aggregate, and A5.2 measured a
withdraw of 100 answering 201 with the day exhausted, so `WithdrawDialog.tsx` cannot see the code
and takes no branch. Noted in place rather than left for a reader to add one._

**Behaviour change, stated plainly.** An over-limit mint with a WRONG PIN now answers 422 rather
than 401 and spends no attempt. A request violating both the daily bound and the balance answers
`DAILY_LIMIT_EXCEEDED`. Both are contract-visible and both are pinned by measurement before the
mock copies them — the first by A1, the second by A4's re-run on the transfer path.

**Cost accepted.** One raw SQL statement in `TransferService` with no precedent beyond the chain's
own; and one extra query at the mint before the PIN. The aggregate reveals only the caller's own
ledger, so it is a cost and not an oracle — but it is an UNBOUNDED cost, and the first draft of this
record said the opposite. It runs at `:220`, before `_stepUp.MintAsync` at `:222` and therefore
before `IPinVerifier`, so ADR-0010's lockout never reaches it: a locked-out caller, or one with no
PIN enrolled, runs the sum on every call and `PinAccessFailedCount` never moves —
`DailyLimitEndpointTests` pins that zero (*"the wrong PIN was never looked at"*) and
`ATransferRefusedForDailyLimit_WritesNoRow_AndThatIsTheDecision` says the same. Nothing in the API
bounds it; the only bound is the BFF's per-IP global limiter (300 requests / 60 s,
`AzureBank.Bff/appsettings.json`), which a direct JWT caller bypasses. Accepted as an unbounded read
for the same reason the refusal is accepted as log-only, and named in D8 as the limiter that would
close it. The seeded admin becomes a resource a real-stack test could exhaust for a day if it ever
moves 5,000 externally — the contract row moves nothing and the after-probe uses throwaway users.

_Measured 2026-09-07 (review round 1), because this paragraph asserted a cost without one.
`SET STATISTICS IO` on `AzureBankDev`, a **242-row `Transactions` table and a 147-row `Accounts`
table** — the transcript, the exact `sqlcmd` commands and the plan are in
`plans/daily-limit/measure-cr1-2026-09-07.txt`, run 2. The aggregate costs **11 logical reads on
`Transactions`** (scan count 1, 1 physical, 9 read-ahead) **and 32 on `Accounts`**, for the busiest
payer of the current UTC day — 5 matching rows, 2 accounts — and IDENTICALLY for the user with the
most of everything (136 transaction rows, 32 accounts). The plan says why, and it is not what a
reader would guess from the numbers: `Transactions` is a **CLUSTERED INDEX SCAN** with the day,
type, status and tag terms as a residual filter — 11 pages IS the whole table, which is why the
number does not move between users — feeding a nested loop that **SEEKS `PK_Accounts` once per
surviving row**, which is the 32. `IX_Transactions_AccountId_CreatedAt` exists and was NOT chosen;
on 242 rows a scan is genuinely the cheaper plan.

**What this settles and what it does not.** It bounds the cost on a PORTFOLIO database and settles
nothing about scale, so it cannot disprove the reviewer's concern — if anything the plan sharpens
it, because the `Transactions` side grows with the WHOLE TABLE today rather than with the caller's
own rows, and the plan a table three orders of magnitude larger would get was not measured and must
not be inferred from these numbers. What stays true either way is the sentence above: nothing in the
API bounds how often an unauthenticated-by-PIN caller can ask for it._

### Before

The measured block in Context, `main` @ `3c30122`, 2026-09-07T12:47Z. In one line: five external
transfers of 150 in one second all answered 201, the mint minted 400 on a balance of 250, and the
transfer's refusal came with `{available, requested}` at the top level of the body.

### After

**AFTER — observed 2026-09-07T14:17Z on this PR's working tree** (`plans/daily-limit/daily-after-probe.py`,
transcript `measure-after-2026-09-07.txt` in the working-state repo), BFF `:5000` → API `:7215`,
`AzureBankDev` (`is_read_committed_snapshot_on = 1`), the DEFAULT 5,000, three throwaway users
registered by the probe (never the seeded admin). Expectation beside observation; none disagreed.
A4 is the exception in provenance, not in agreement: the pre-review found that the 14:17Z probe had
run it in a shape that cannot show the order it was cited for — a MINT, which has no balance rung,
and a transfer whose day was intact — so the panel's A4, the both-bounds TRANSFER, was re-run on
this same tree at 2026-09-07T14:46:26Z. That run is the row's Observed cell and the tail of the
same transcript; the first probe's two lines are kept in the fence for what each does show.

| # | Probe | Expected | Observed |
| --- | --- | --- | --- |
| A1 | mint 5,000.01 with a WRONG PIN, three times; then a correct-PIN mint of 100 | 422 `DAILY_LIMIT_EXCEEDED` ×3, never 401; `PinAccessFailedCount` unchanged; no `StepUpAuthorizations` row; then 201 |422 `DAILY_LIMIT_EXCEEDED` "Daily transfer limit exceeded." `{limit 5000, used 0.0, requested 5000.01, resetsAt "2026-09-08T00:00:00Z"}` ×3; `PinAccessFailedCount` 0 → 0; authorisations minted 0; then 201 |
| A2 | deposit 10,000; mint+transfer 2,000 twice; mint 1,000.01; mint 1,000 | 201, 201; 422 `{limit 5000, used 4000, requested 1000.01, resetsAt}`; 201 (inclusive bound) |201, 201 (used 4,000 by SQL); 422 `{limit 5000, used 4000.0, requested 1000.01, resetsAt …}`; 201 |
| A3 | with used 4,000: mint A 1,000 and mint B 1,000; spend A; spend B; retry B's key | 201, 201 (the mint does not reserve); 201; 422, row Pending; 422 again, no `Idempotency-Replayed` |201, 201; 201 (used 5,000); 422 `{used 5000.0, requested 1000}`, B Pending; 422 again, no `Idempotency-Replayed`, and the sender's `IdempotencyRecords` hold exactly the seven 201 money POSTs — the refused key left no row |
| A4 | both bounds violated on the TRANSFER path: deposit 5,200; transfer 4,600; mint C 300 and D 400; spend C; spend D | 201 through spend C (used 4,900, balance 300); spend D → 422 `DAILY_LIMIT_EXCEEDED`, not `INSUFFICIENT_FUNDS` — daily before balance |deposit 5,200 201; transfer 4,600 201; mint C 300 and mint D 400 both 201 (the mint does not reserve); spend C 300 → 201, leaving used 4,900 and balance 300; spend D 400 (daily 5,300 > 5,000 AND balance 300 < 400) → 422 `DAILY_LIMIT_EXCEEDED` `{limit 5000, used 4900.0, requested 400, resetsAt "2026-09-08T00:00:00Z"}` — daily before balance on the transfer path too (re-run 14:46Z) |
| A5 | day exhausted: internal 100; withdraw 100; deposit 100 | 201 each (excluded rails) |201, 201, 201 |
| A6 | from a second account transfer externally, drain it, DELETE it, mint over the limit | 422 with `used` including the closed account's row |transfer 4,600 from the spare 201; DELETE the drained spare 200; mint 500 from the primary → 422 `{used 4600.0, requested 500}`; mint 400 → 201 (exactly 5,000) |
| A7 | `sqlcmd`: today's external `TransferOut` sum for the sender; `AuditEvents` for the sender during the probe; B and D rows | `5000.0000`; successes only, no refusal row; both Pending |`3  5000.0000`; audit rows MoneyDeposited 2, MoneyTransferred 3, MoneyTransferredInternally 1, MoneyWithdrawn 1, PinEnrolled 1 — no refusal row; authorisations Consumed 4, Pending 2 |
| A8 | the 422 body through the BFF | `limit`, `used`, `requested` as top-level JSON numbers, `resetsAt` a string, `errorCode` and `traceId` present |`"limit": 5000, "used": 0.0, "requested": 5000.01` as JSON numbers, `"resetsAt": "2026-09-08T00:00:00Z"`, with `errorCode` and `traceId` — through the BFF, unchanged |
| A9 | the midnight boundary and the two-account race | NOT measurable on the real stack (system clock, no burst tool) — the fake-clock tests and the SQL Server proof are the record |not measured, by design |

```
A1.1 mint 5000.01 with a WRONG pin      422  errorCode=DAILY_LIMIT_EXCEEDED  detail="Daily transfer limit exceeded."  extra={"limit": 5000, "used": 0.0, "requested": 5000.01, "resetsAt": "2026-09-08T00:00:00Z"}
   PinAccessFailedCount after three: 0   authorisations minted: 0
A2.3 mint 1000.01 (would make 5000.01)  422  errorCode=DAILY_LIMIT_EXCEEDED  extra={"limit": 5000, "used": 4000.0, "requested": 1000.01, "resetsAt": "2026-09-08T00:00:00Z"}
A2.4 mint 1000 (exactly the limit)      201
A3.3 spend B 1000 (used 5000)           422  errorCode=DAILY_LIMIT_EXCEEDED  extra={"limit": 5000, "used": 5000.0, "requested": 1000, ...}   authorisation B after: Pending
A3.4 spend B again, SAME key            422  (no Idempotency-Replayed header)
## the first probe's A4 (14:17Z) ran a different shape; both its lines are kept for what each shows:
## A4.3 is a MINT, where no balance rung exists (D4 item 1), so only one bound is violated there;
## A4.4 is a transfer whose day was intact, so it shows the balance rung alone. Neither is the order.
A4.3 mint 401 (daily 5001 AND balance 300 < 401)         422  errorCode=DAILY_LIMIT_EXCEEDED
A4.4 spend the 400 authorisation (balance 300 < 400)     422  errorCode=INSUFFICIENT_FUNDS  extra={"available": 300.0, "requested": 400}
## A4 as the panel wrote it -- the both-bounds TRANSFER -- re-run 2026-09-07T14:46:26Z, same tree:
deposit 5200 / transfer 4600                             201 / 201  OK {"newBalance": 600.0}
mint C 300 / mint D 400 (used 4600, balance 600)         201 / 201
spend C 300 -> used 4900, balance 300                    201  OK {"newBalance": 300.0}
spend D 400 -> daily 5300 > 5000 AND balance 300 < 400   422  errorCode=DAILY_LIMIT_EXCEEDED  extra={"limit": 5000, "used": 4900.0, "requested": 400, "resetsAt": "2026-09-08T00:00:00Z"}
A5   internal 100 / withdraw 100 / deposit 100           201 / 201 / 201
A6.3 mint 500 from the primary after the closed spare's 4600   422  extra={"limit": 5000, "used": 4600.0, "requested": 500, ...}
A7   sender's external TransferOut today: 3  5000.0000   audit rows: successes only
```

## Verification

Each is a test, not a sentence; the SQL Server ones run only with `AZUREBANK_TEST_SQLSERVER` set.

- **The lock is load-bearing** — `DailyLimitConcurrencySqlServerTests`, all on a ceiling of 500 and
  transfers of 100; the two CROSS-ACCOUNT proofs below give every transfer its own payee (D5's
  finding) and stretch the window with `SlowAuditTailInterceptor`, `Fired` asserted. The
  one-account proof does neither — see the bullet after these two.
  `EightAccountsOfOneUser_OneTransferEach_NeverExceedTheDay` — eight accounts of one user, one
  transfer each, no row shared by any two: exactly five 201s and three 422s
  `DAILY_LIMIT_EXCEEDED`, no 500, ledger sum 500, the three losers' authorisations Pending. This is
  the tripwire: red on every run with the `sp_getapplock` batch removed (D5's fence).
  `TwoAccountsOfOneUser_ParallelTransfers_NeverExceedTheDay` — two accounts, four transfers each,
  fired WHILE a deposit, an AzureTag rename and a wrong-PIN mint by the same user run: the same
  counts, deposit 201, rename below 500, wrong-PIN mint 401 or 422, and every captured log line
  naming 1205 or a deadlock is a retry notice (none appeared). The mixed-load proof; without the
  lock it went red once and green once.
- **The same-account race** —
  `OneAccount_ParallelTransfers_NeverExceedTheDay_ThroughTheRowVersionRetry`: eight transfers from
  one account funded 1,000, proving the `RowVersion` retry re-enters the delegate and re-sums —
  five 201s, three 422s, sum 500. What it does NOT prove is the lock: it registers ONE payee for
  all eight, so by D5's finding the payee's row serialises this shape whether or not the applock
  exists, and no `SlowAuditTailInterceptor` stretches its window. The eight-account proof above is
  the lock's tripwire; this one is the retry's.
- **The raw statement under the retrying strategy** —
  `TheApplock_ParsesUnderTheRetryingStrategy_BlocksASecondConnection_AndReleasesOnCommitAndRollback`:
  the production batch parses and returns ≥ 0 inside `strategy.ExecuteAsync` +
  `BeginTransactionAsync`; a second connection's call with `@LockTimeout = 0` returns −1 while held
  and 0 after commit and after rollback; no "does not support user-initiated transactions" error;
  and it logs the `sys.databases` isolation row (LocalDB `AzureBankTests` 1 / 0, in D5's fence).
- **The mint before the PIN** — `TransferServiceTests`: an over-limit mint throws
  `DailyLimitExceededException`, `IPinVerifier.VerifyPinAsync` is verified `Times.Never`, no
  authorisation row; an over-limit INTERNAL mint still mints.
- **The order on the transfer** — `TransferServiceTests`: the pre-check throws after `ValidateAsync`
  and before any row, authorisation left Pending; daily before balance.
- **The helper** — `DailyOutflowLimitServiceTests` on InMemory with ONE `FakeTimeProvider` handed to
  both the context and the service: internal `TransferOut` excluded; Withdrawal, Deposit,
  `TransferIn` excluded; another user's external row excluded; a row on a soft-deleted account of
  the same user counts; a row at 23:59:59Z counts and stops counting after the single clock crosses
  00:00:00Z; `resetsAt` is the next midnight; `used + amount == limit` passes, `+ 0.01` throws with
  the exact `Details`.
- **One clock** — `DbContextReceivesRegisteredClockTests`: a saved row's `CreatedAt` equals the
  registered fake clock's instant; `LedgerClockHygieneTests`: `DailyOutflowLimitService.cs` and
  `TransferService.cs` contain no `DateTime.UtcNow`.
- **Through the composition root** — `DailyLimitEndpointTests` (InMemory, `SetDailyLimit(500)`):
  fund, spend 300, wrong-PIN mint of 200.01 → 422 with `{limit 500, used 300, requested 200.01,
  resetsAt}` then a correct-PIN mint of 100 → 201; two mints of 200 both 201; the second spend 422
  with the authorisation Pending, balance unchanged, same key → 422 with no replay header; internal
  and withdraw 201 after the day is exhausted; the factory's fake clock advanced past midnight → a
  mint of 500 answers 201.
- **No row, and that is the decision** —
  `AuditTrailPersistenceTests.ATransferRefusedForDailyLimit_WritesNoRow_AndThatIsTheDecision`, at
  the mint and at the transfer.
- **The option** — `DailyLimitOptionsTests`: 0, −1 and 5000.001 fail `ValidateOnStart`; 5,000
  passes.
- **The document** — `PublishedDailyLimitTests` reads the COMMITTED `openapiv1.json`: the 422
  description on both `POST /api/transfers` and `POST /api/transfers/authorizations` names
  `DAILY_LIMIT_EXCEEDED`; the mint's is no longer the bare reason phrase and names all four codes
  that action answers — `SELF_TRANSFER_NOT_ALLOWED` and `RECIPIENT_NO_ACCOUNT` included — so an
  undercount fails for the right reason rather than passing on incomplete prose; and the deposit's
  422 keeps only its idempotency clause, claiming no refusal that endpoint cannot answer.
  `PublishedErrorContractTests` and `PublishedMoneyBoundsTests` pass with no edit, which is the
  proof that the aggregate is not a second schema maximum.

## What would change this

- **A second aggregate on the same ledger** — withdrawals joining the count, a per-account limit, a
  tier — is the day `DailyLimitOptions` grows a shape and the helper's predicate takes a parameter;
  the applock resource name `daily-limit:{userId}` already keys by user and would key by whatever
  the new scope is.
- **Something writing `Pending` into `Transactions`** reopens D3's `Completed` filter as a decision
  about reservations rather than a no-op.
- **Partitioning the audit chain** changes nothing in D5 — that is the point of the applock — and
  the SQL Server proof is the tripwire that would say otherwise.
- **A user time zone** (a column on `ApplicationUser`) is the seam a customer-local day would need;
  until it exists, D1's "UTC day" is the only day the data can name.
- **The withdraw convergence** (ADR-0042's own "not done") is the moment D2's withdrawal exclusion
  is revisited — with the helper already written, it is one call site and one restructure, not a
  second design.
- **The mint's extra query costing enough to need a bound** — an API-side per-user limiter on
  `POST /api/transfers/authorizations`. The daily check runs before `IPinVerifier`, so ADR-0010's
  lockout cannot bound it and the only bound today is the BFF's per-IP global limiter, which a
  direct JWT caller bypasses (Consequences, "Cost accepted").

  _Sharpened 2026-09-07 (review round 1): the review asked for a per-user / per-token / global
  limiter in `AzureBank.Api` in front of the aggregate. It stays HERE, named and unbuilt, and the
  reason is not reluctance. **Verified: the API has no rate-limiting infrastructure at all** —
  `AddRateLimiter` and `EnableRateLimiting` appear only under `backend/src/AzureBank.Bff`
  (`Bff/Program.cs:256`, `BffAuthController`, `RateLimitPolicies.cs`), so this is not a parameter to
  add but a first limiter to introduce, with its own decisions: the policy shape, the partition key
  read out of the JWT, the 429 contract and its entry in the document, and which other endpoints
  need it the moment one exists. That is a feature, not a fix to the PR this bullet was written in,
  and this record already accepted the unbounded read on its own terms. The measurement that would
  have argued the other way was taken and does not: the aggregate costs 11 logical reads on
  `Transactions` and 32 on `Accounts` on a 242-row table (Consequences, "Cost accepted"), which
  bounds nothing at scale in either direction._
- **Vlad reversing D5 at the PR** in favour of the re-sum under the audit tail: D5's declined
  alternative becomes the mechanism, the comparison becomes `used > limit` after the first save, the
  ADR-0044 note flips from "does not depend on the tail" to a dated dependency, and this record is
  corrected in place.

### What moved in other records, all struck or noted in place with today's date

- **ADR-0046**: D6's correction gains the note that the aggregate now exists as an option, not a
  constant; D7's "neither starts nor forecloses" is struck for external transfers per user; the
  first "What would change this" bullet records that its trigger fired in part — a second bound, but
  no second schema maximum, so `MONEY_MAX` stays one number and D1 stays true.
- **ADR-0044**: an "Instance added" note after the money-refusals paragraph (the daily-limit refusal
  is business validation and stays log-only), and a one-line note beside the partition question
  (this ADR's aggregate does not depend on the tail; the only lock order it adds is applock → tail).
- **ADR-0042**: a note under the error-code table — a non-PIN 422 ahead of the PIN at the external
  mint, the transfer's check after `AUTHORIZATION_REQUIRED` and the payee resolution so the
  enumeration argument stands, the attribute removed from `AuthoriseTransfer`, withdraw still its
  own task.
- **ADR-0049**: the `TimeProvider` deferral in "Not done" gains a dated correction — the framework
  had registered `TimeProvider.System` all along and the context was already receiving it, so there
  was no registration to land; this ADR makes the dependency explicit, adds the first required
  consumer and pins the arrival, for the ledger clock only.
