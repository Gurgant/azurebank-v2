# ADR-0050: A UTC day bounds a user's external transfers, and the mint says so before the PIN

**Status:** Accepted · **Date:** 2026-09-07 · **Amended:** 2026-09-08 and 2026-09-11 (D7),
2026-09-16 (D5) · Builds on ADR-0042, ADR-0044, ADR-0046 and ADR-0049. Supersedes nothing.

## Context

Before this decision nothing aggregated: measured through the BFF, five external transfers of 150 in
one second all answered 201. The mint, the call that checks the PIN and issues a transfer's
authorisation (ADR-0042), checked nothing about money (B1: a mint of 400 with 250 left answered 201;
B2: the transfer then refused, 422 `INSUFFICIENT_FUNDS` with `available` and `requested` in the
body), so a doomed transfer cost a PIN entry. The balance guard itself has no ADR. Every writer sets
`Completed` (207 of 207 rows measured); the seeded admin's external day was empty (0). PSD2 Art.
68(1) lets payer and provider agree limits and prescribes no figure, window or zone.

## Decision

- **D1 — The window is the UTC calendar day, `[00:00:00Z, next 00:00:00Z)`**, because the ledger
  lives there (`CreatedAt` is stamped in UTC from the context's `TimeProvider`) and no user row
  has a time zone. The day start comes from that same `TimeProvider`, never `DateTime.UtcNow`.
- **D2 — Only external transfers count: `TransferOut` rows with `RecipientAzureTag != null`**,
  because only that tag tells the two `TransferOut` writers apart. Not counted: internal transfers
  (the user keeps the money; SCA-exempt in RTS Art. 15), inflows, withdrawals (D8, ADR-0056).
- **D3 — Per user, completed rows only, and no `IsDeleted` filter.** The sum joins
  `Accounts.UserId`, because the customer is the payer and two accounts would otherwise hold two
  limits. It says `IgnoreQueryFilters()`, because a drained-then-closed account's outflows are
  money that left that day. `Completed` decides nothing until something writes `Pending`.
- **D4 — Three checks, in this order: daily → balance, and the mint's check before the PIN.**
  1. At the external mint, after the payee is resolved and before `IPinVerifier` spends an attempt,
     because a 422 guard that reveals only the caller's own state belongs on that rung (ADR-0049
     D4): an over-limit mint with a wrong PIN answers 422 and spends nothing. No mint checks funds.
  2. Before the transfer's retry loop, after `ValidateAsync`, so that an absent, expired or invalid
     authorisation still wins and the check is no oracle; and before the balance check, so that
     mint and transfer refuse in the same order. Both bounds violated: `DAILY_LIMIT_EXCEEDED`.
  3. Inside the transaction, the authoritative check (D5). The midnight edge is the definition: a
     mint that passes at 23:59:30Z and is spent at 00:00:10Z is decided against the new day.
- **D5 — The control is a per-user application lock, taken first, with the sum before any row.**
  Right after `BeginTransactionAsync`, `TransferService.DailyLimitLockSql` takes `sp_getapplock`
  on `daily-limit:{userId}`, then the sum over committed rows runs and `used + requested > limit`
  throws, because two transfers from two accounts of one user to different payees share no row:
  without the lock, eight transfers of 100 against a ceiling of 500 answered eight 201s and a day
  of 800. The batch raises on a negative return, which `ExecuteSqlRawAsync` cannot see. The wait is
  bounded by `DailyLimit:LockTimeoutSeconds` (default 10, range 1 to 29, below the 30 s command
  timeout), because without `@LockTimeout` it is `@@LOCK_TIMEOUT`, measured -1, for ever; with
  2,000 ms a waiter was refused -1 after 2,006 to 2,012 ms. A timeout is a fault, never the 422.
- **D6 — The number is an option, 5,000, validated on start; and the ledger clock's DI registration
  is app-owned.** `DailyLimit:Amount` is an option, not a constant, because tiers will need one and
  an aggregate has no JSON-schema slot (`MONEY_MAX` stays the one published maximum, ADR-0046).
  5,000 is below `TransactionMaxAmount` (100,000), so a mint of 5,000.01 is refused with no money
  moved. `AddDailyLimit` repeats the framework's clock registration with a `TryAdd`, and the one
  helper behind the three checks, `DailyOutflowLimitService`, requires it, so the ledger cannot run
  on a second clock; the step-up mint and expiry still read `DateTime.UtcNow`.
- **D7 — The refusal: `422 DAILY_LIMIT_EXCEEDED`, figure-free, four numeric extension members.**
  "Daily transfer limit exceeded." with `limit`, `used`, `requested` and `resetsAt` at the top of
  the body, declared in the document on the two operations that answer the code, because figures
  travel as numbers and the client formats them (fixed en-IE). `remaining` is not sent: the client
  derives `limit − used` and branches on `errorCode`, never on a member's presence (`requested`
  also rides `INSUFFICIENT_FUNDS`). Log-only, no audit row (ADR-0044); never replayed (ADR-0009).
- **D8 — Not decided here:** rolling windows; tiers; amount-scaled step-up; velocity rules;
  withdrawals and internal transfers under an aggregate; per-account or adjustable limits; a
  "remaining today" read (`GET /api/transactions/allowance`); an API limiter on the mint.

| Refusals in order (the rung table) | 1 | 2 | 3 | 4 | 5 |
| --- | --- | --- | --- | --- | --- |
| mint | not the caller's account: 404 / 403 | payee: 404 / 422 | `DAILY_LIMIT_EXCEEDED` 422 | the PIN: `PIN_REQUIRED` 422, `INVALID_PIN` 401, `PIN_LOCKED` 429 | |
| transfer | `AUTHORIZATION_REQUIRED` 401 | payee: 404 / 422 | `AUTHORIZATION_EXPIRED` / `_INVALID` 401 | `DAILY_LIMIT_EXCEEDED` 422 | `INSUFFICIENT_FUNDS` 422 |

## Rejected

- Rejected: a rolling 24-hour window, because "remaining" moves and has no fixed reset instant.
- Rejected: re-summing under the audit chain's tail lock, because ADR-0044 may partition that lock.
- Rejected: a row lock on `AspNetUsers` or `Accounts`, because audited saves lock tail → row, so a
  transfer locking row → tail closes a 1205 cycle; applock → tail is the only order added.
- Rejected: restoring `ValidationRules.DailyTransferLimit = 1_000`, because it was a dead number.
- Rejected: an audit row per refusal, because it is an unbounded write the caller triggers at will.

## Consequences

- A single external transfer can never reach the published per-request maximum on a fresh day.
- The SPA composes its sentence from the members; without `limit` or `used` it shows the server's: a
  correct refusal in the server's words, not a crash. The mock sums its ledger rather than counting
  mints, and one zero-money contract row runs on both targets: a mint of 5,000.01 answers 422.
- The mint's 422 names its four codes, because `[ProducesResponseType(422)]` is gone from
  `AuthoriseTransfer`; `IdempotencyOperationTransformer` composes each money endpoint's own entry.
- Not covered: only the BFF's limiter (300 requests / 60 s per IP) bounds the mint's query, which
  runs before the PIN's lockout: 11 logical reads on `Transactions` (a full scan), 32 on `Accounts`.

### After

| # | Probe (2026-09-07, through the BFF, the default 5,000) | Observed |
| --- | --- | --- |
| A1 | mint 5,000.01 with a wrong PIN, three times; then mint 100 with the right PIN | A1.1: 422 `DAILY_LIMIT_EXCEEDED` "Daily transfer limit exceeded." `{limit 5000, used 0.0, requested 5000.01, resetsAt "2026-09-08T00:00:00Z"}` each time; `PinAccessFailedCount` 0 → 0; nothing minted; A1.4: then 201 |
| A2 | used 4,000: mint 1,000.01; mint 1,000 | A2.3: 422 `{limit 5000, used 4000.0, requested 1000.01}`; A2.4: 201, the bound is inclusive |
| A3 | used 4,000: mint A 1,000 and B 1,000; spend A; spend B; B's key again | A3.1: 201 and 201, the mint does not reserve; 201; A3.3: 422 `{used 5000.0, requested 1000}`, B stays Pending; A3.4: 422 again, no `Idempotency-Replayed` |
| A4 | used 4,900 and balance 300: spend 400 | 422 `DAILY_LIMIT_EXCEEDED` `{limit 5000, used 4900.0, requested 400}`, daily before balance. A4.3, the first probe: a mint of 401 with used 4,600 and balance 300 answers the same code (a mint has no balance rung, so it shows no order). A4.4: with the day intact, a spend of 400 on a balance of 300 answers `INSUFFICIENT_FUNDS` `{available 300.0, requested 400}` |
| A5 | day exhausted: internal 100 (A5.1); withdraw 100 (A5.2); deposit 100 | 201 each: the excluded rails |
| A6 | transfer 4,600 from a spare account; close it; mint 500 from the primary; mint 400 | A6.3: 422 `{used 4600.0, requested 500}`, the closed account still counts; A6.4: then 201, exactly 5,000 |
| A7 | the sender's rows afterwards | external `TransferOut` today: 3 rows, 5000.0000; audit rows for the successes only; the refused authorisations Pending |
| A8 | the 422 body | `limit`, `used`, `requested` as top-level JSON numbers, `resetsAt` a string, with `errorCode` and `traceId` |
| A9 | the midnight boundary and the two-account race | not measurable on the real stack: the fake-clock tests and the SQL Server proof are the record |

## What would change this

- A writer of `Pending` rows in `Transactions`: D3's filter becomes a decision about reservations.
- A second aggregate, a time zone on `ApplicationUser` (D1), or a bound on the mint's query (D8).
- Transaction-level `SNAPSHOT` isolation, which nothing sets: D5's lock does not hold under it.

## Verification

- `DailyLimitConcurrencySqlServerTests`, only with `AZUREBANK_TEST_SQLSERVER` set:
  `EightAccountsOfOneUser_OneTransferEach_NeverExceedTheDay` (eight payees) is the lock's tripwire,
  red without it; `OneAccount_ParallelTransfers_NeverExceedTheDay_ThroughTheRowVersionRetry` is the
  retry's, because one payee's row serialises that shape with or without the lock.
- `DailyOutflowLimitServiceTests` (D1–D3), `TransferServiceTests` (D4), `DailyLimitEndpointTests`,
  `DailyLimitOptionsTests`, `PublishedDailyLimitTests`, `DbContextReceivesRegisteredClockTests`,
  `LedgerClockHygieneTests`, `ATransferRefusedForDailyLimit_WritesNoRow_AndThatIsTheDecision`.

## Related

ADR-0009, ADR-0010, ADR-0042, ADR-0043, ADR-0044, ADR-0046, ADR-0049, ADR-0056.
