# ADR-0062: Demo visitors get private copies from a prepared pool

**Status:** Accepted · **Date:** 2026-10-03 · **Decision Makers:** Vladislav Aleshaev ·
**Amends** [ADR-0044](0044-the-audit-trail-is-append-only-and-chained.md) D6 (the ledger rows of a
demo copy are deleted, around the ledger's guard, and the audit rows that name them stay),
[ADR-0042](0042-a-transfer-authorisation-is-bound-and-spent-once.md) (a demo copy's authorisations
leave with the copy),
[ADR-0060](0060-migrations-run-as-a-one-shot-container-before-the-app.md) decisions 4 and 5 (two
commands that run on an Azure SQL name, as the app's database user where it has one of its own, and
two exits 2 that come after one count) and [ADR-0011](0011-pin-hash-pepper.md) (two more commands
check the pepper)

**Where the code is.** The pool's code is in `backend/tools/AzureBank.Seeder/Pool/`
(`DemoCopyBuilder`, `DemoCopyRecycler`, `DemoCredentials`, `PoolCounts`, `PoolExitCodes`,
`PoolRunSummary`) and `backend/tools/AzureBank.Seeder/Seeders/DemoLedger.cs`; its two commands
are `SeedPoolCommand` and `RecycleCommand`, beside `DemoMode`, in
`backend/tools/AzureBank.Seeder/Commands/`, and `compose.demo.yaml` runs them; the table is
`DemoCopy` and its configuration; the settings are `DemoOptions`; the two resolvers are
`UserService.GetUserByAzureTagAsync` and the payee resolver of `TransferService`. The comments this
record made false cite it as ADR-0062. The Seeder's README is the commands' contract: their
variables, their exit codes and their line.

## Context

**The demo is one set of users that everybody shares.** `seed` writes four: John Smith, with two
accounts and two months of history, the two people that history pays, Jane Smith and Mike Brown,
and an administrator. Their password and their PIN are in the Seeder's committed settings. A
public link to that demo hands every visitor the same account: what one visitor does, the next one
sees, and anyone can sign in as anyone.

What the deployment needs instead: each visitor gets a demo of their own at once, no account has a
password anybody else knows, and what a visitor leaves behind ends.

**What the schema says about deleting a demo user**, read at the start of this work:

- The demo history's payments between John and his two contacts link rows across the three users:
  a transfer is a row on each side, each naming the other through `RelatedTransactionId`, a foreign
  key that restricts. The owner's rows cannot leave without the contacts', nor the contacts'
  without the owner's.
- `EnforceTransactionImmutability` refuses a tracked delete or change of a ledger row
  (ADR-0044 D6).
- A closed account is a row with a flag, hidden from every query by the context's filter.
- `StepUpAuthorizations` and `IdempotencyRecords` hold a user's id with no foreign key, so nothing
  removes their rows with the user.
- `AuditEvents` has no foreign key either: its rows name their actor and subject by id, and the
  table is never purged (ADR-0044 D6).
- Handles are unique across the whole table. Six comparisons of a handle exist in `backend/src`,
  counted in the forms `DemoCopyBoundaryTests` scans for (Validation): five written
  `AzureTag ==`, and the transfer's `Equals`, which refuses a payment to oneself. Two of the six
  hand another user to the caller: the recipient lookup and the payee of an external transfer.

## Decision

**1. The copy is the unit.** A copy is the fixed demo, private to one visitor: John Smith with his
two accounts (12,450.00 and 2,300.00), Jane Smith (8,500.00), Mike Brown (25,000.00), and the 26
ledger rows that connect them. With its pool row and three role rows that is 37 rows. A copy is
seeded, claimed, kept apart from other copies and deleted as one thing, never one user at a time:
the transfers above make one user alone undeletable. The history comes from `DemoLedger`, which
`seed` builds the fixed demo from too, so the two cannot drift apart.

**2. One table and one column, in one migration (`AddDemoCopies`).**

| Column of `DemoCopies` | Meaning |
|---|---|
| `Id` | The copy, minted by the Seeder |
| `OwnerUserId` | The demo user. Unique. **No foreign key**: the key goes the other way, and the row outlives the user |
| `CreatedAt` | The seed instant. The ledger's dates are offsets from it, so "newest" is read here, never from the id |
| `ClaimedAt`, `ClaimId` | Null while the copy is free; set together or not at all (`CK_DemoCopies_ClaimIsWhole`) |
| `ClientKey` | A keyed hash of the claiming client's address, 32 bytes; null on a free copy and on a deleted one |
| `Writes` | Requests that changed something, made by the copy's users; 0 by default |
| `DeletedAt` | Set when a claimed copy's users were deleted: the row is then a record only (`CK_DemoCopies_DeletedWasClaimed`) |

`AspNetUsers.DemoCopyId` is a nullable foreign key to the copy (no action on delete), with an index
filtered to the rows that carry one. All three users of a copy carry it, and it is the only thing
that says which users are a copy's: no name pattern, no age. `ClaimId`, `ClientKey` and `Writes`
belong to the claim and its caps, which a later record decides; their columns are here so that the
pool has one migration. Outside the demo the table holds no row and the column is null on every
user.
*(Decided 2026-10-04,
[ADR-0063](0063-a-visitor-claims-a-prepared-copy-instead-of-registering.md). `ClaimId` is what a
claim whose commit's answer was lost is recognised by. `ClientKey` is the HMAC-SHA256 of the
address the BFF names for the visitor, under `Demo:ClientKeySecret`, and the claim counts a
client's copies of the last 24 hours by it. `Writes` counts more than the table above says:
every request of a signed-in user of the copy that could change something, refused ones and
replayed retries included, and each reveal of an account number; never a token endpoint.)*

**3. Off unless a deployment turns it on.** `Demo:Enabled` is false by default, and no committed
settings file sets anything under `Demo`. The defaults live in `DemoOptions`, and
`DemoOptionsValidator` checks every range whether the demo is on or not, so a bad value is refused
the day it is written.

| Setting | Default | Range |
|---|---|---|
| `Demo:CopyLifetimeHours` | 24 | 1 to 168 |
| `Demo:Pool:TargetFree` | 50 | 1 to 500 |
| `Demo:Pool:LowMark` | 20 | 0 to `TargetFree` |
| `Demo:Pool:MaxFreeAgeHours` | 44 | 1 to 720 |
| `Demo:Pool:MaxClaimsPerDay` | 150 | `TargetFree` to 10,000 |
| `Demo:Claim:MaxPerClientPerDay` | 10 | 1 to 1,000 |
| `Demo:Copy:MaxWrites` | 200 | 10 to 100,000 |

~~Today only the Seeder binds the section.~~ *(Struck 2026-10-04, ADR-0063: the API and the BFF
bind it too, and each registers `DemoOptionsValidator`, so either refuses to start on a `Demo:*`
value out of range, one it never reads included, with the demo on or off. The API also asks for
`Demo:ClientKeySecret`, 32 characters or more, when the demo is on; that rule is its own and not
the validator's, since the Seeder runs the validator and holds no secret.)* The builder and the
recycler refuse to run with the flag off,
in their own code and not only in whatever calls them, and their two commands refuse first,
before they open anything (decision 13). `seed` and `reset` refuse to run with it on. **Not behind
the flag:** the migration, the two resolver comparisons of decision 7, which change nothing where
no user belongs to a copy, and `seed`'s refusal of a database that holds a pool row.

**4. A free copy has no password.** Its users are created through Identity with none, so no
password hash exists for a sign-in to match, whatever is typed. The claim sets one when a visitor
takes the copy. Every user of a copy has the PIN `123456` (`DemoCopyDefaults.Pin`), one hash per
run: the PIN is no secret, and what keeps a copy private is its owner's sign-in.

**5. A copy's identifiers come from the cryptographic generator.** The three handles share one
suffix of four characters (`john_k7m2`, `jane_k7m2`, `mike_k7m2`), so a visitor reads the contacts'
handles off their own. The owner signs in as `demo-` plus 16 characters at `azurebank.example`, the
contacts are `contact-` plus 16; `.example` is reserved, so no mail can reach it. Each character is
one `RandomNumberGenerator.GetInt32` over `[a-z0-9]`; 16 of them are 82 bits. The address's
characters are drawn on their own, never from the copy's id (a version 7 id carries its instant)
and never from the suffix (every user of the copy can read it). A generator whose next value
follows from its last would hand a visitor the addresses of the copies seeded beside theirs.

**6. A copy is built whole or not at all.** One transaction per copy, through the execution
strategy. A unique violation (2601 or 2627: a handle, an account number or a transaction number
that another copy, or a visitor's rename, holds already) draws the whole copy again, three times
at most. A copy that fails is counted, logged by its id, and left out; three failures in a row end
the top-up. A run tops the pool up **to** its target, never by it, so it can be run again at any
time. It writes no audit row: the Seeder holds no audit chain key.

**7. A handle is resolved only inside the caller's copy.** The lookup reads the caller's
`DemoCopyId` and compares it; the payee resolver, which the mint and the transfer share, compares
the sender's. A handle another copy holds gets the answer of a handle nobody holds: 200 with
`exists: false` from the lookup, 404 `Recipient` from the mint, before the PIN is looked at, and
from the transfer. **No flag is read on this path.** Outside the demo both sides are null, EF sends
a null as `IS NULL`, and every user goes on finding and paying every other. The other four
comparisons of a handle ask whether it is taken anywhere (registration, rename) or compare the
caller's own row with what the caller typed (the rename, and the transfer's refusal to pay
oneself), and stay global.

**8. `recycle` tops up first, then deletes, then sweeps.** One run, in this order:

1. Count, before anything changes.
2. **The wrong database:** users, and not one pool row (records included). Nothing is written, the
   sweeps included, and the run reports it (exit 13).
3. **Top up** to `min(TargetFree, max(0, MaxClaimsPerDay - claims in the last 24 h))` fresh free
   copies. First, because building writes only new rows, which nothing a visitor did can stop, and
   free copies are what visitors wait for.
4. **Delete each claimed copy whose time is over** (decision 9), oldest first.
5. **Delete each free copy older than `MaxFreeAgeHours`**: its history ends that long ago. **Only
   once the top-up has reached its target**, the lowered one under the ceiling: the fresh free
   copies step 1 counted and the copies step 3 built are at least that target. A copy too old to
   count is still one a visitor can be given, so a run whose top-up came up short deletes none of
   them, and the next run that reaches its target deletes them all. All or none, not as many as
   were built: the copies a top-up builds stand in for claimed copies too, and which one stands in
   for which cannot be told. Older as of the instant of step 1, the instant the top-up counted:
   those are the copies it counted as missing, so the target it reached was reached without them;
   one that grew too old while the top-up ran is the next run's. Until a review found it, a run
   deleted them whatever its top-up built: with every insert refused, a pool of three free copies
   that had grown old together was left with none (on LocalDB, before the fix:
   `was=3 seeded=0 staleFree=3`). It is taken with the statement a claim uses
   (`UPDATE … WHERE Id = @id AND ClaimedAt IS NULL`), and deleted in the same transaction only if
   that statement changed the row, so of a visitor and the run only one can have it, and a run
   that dies half way leaves it free.
6. **Sweep** the expired idempotency records and grants of anyone, as the API's own clean-ups do,
   with the same Warning for a record of an operation that never stored its answer. Last, and
   outside every per-copy `try`: a sweep that fails ends the run as failed, after the copies.
7. Count again; print one line; set the exit code (decision 12).

Each copy has its own transaction and its own `try`: a copy whose delete throws is counted, logged
by its id and SQL Server's error number, left as it was, and tried again by the next run, not by
this one. A run that is stopped blames no copy. Every statement of a run gets 120 s instead of the
shared 30 s, because a statement that times out is not sent again.

**9. A claimed copy is deleted when its time is over and no session is running in it.** Its time is
over five minutes after `ClaimedAt + CopyLifetimeHours`. A grant of one of its users that is not
revoked and did not end more than five minutes ago keeps it, and that is read inside the
transaction that deletes it, not when the copy was chosen. The five minutes are the claim's
margin again: every access token a grant mints ends with it, but a request accepted the moment
before has its deadline still to run. **The backstop:** 48 hours past its lifetime a copy is deleted with a
live grant all the same, and counted apart (`hardStop`). A grant lives at most 24 hours from
sign-in (`Jwt:RefreshTokenLifetimeMinutes`, 60 by default), so a grant still live two days after a
copy's end says sign-in went on being accepted after it. The next renewal of that session presents
a grant the database no longer holds, is answered 401, and writes a `RefreshTokenUnknown` row with
no actor (ADR-0057).

**10. How a copy is deleted: six statements in one transaction, around the ledger's guard.**

1. `StepUpAuthorizations` of the copy's users: no foreign key reaches them.
2. `IdempotencyRecords` of the copy's users, the same way.
3. `Transactions` on every account of those users, closed accounts included, **in one statement**:
   both halves of a transfer leave together, and the restricting key is checked when the statement
   ends.
4. `Accounts` of those users, with the soft-delete filter ignored: a closed account left behind
   would refuse the next statement.
5. `AspNetUsers` whose `DemoCopyId` is the copy. The database cascades their grants, notices, role
   rows, claims, logins and tokens. Two grants of one user that name each other leave in that one
   statement, so the link between them stops nothing.
6. A claimed copy: `DeletedAt` set and `ClientKey` removed, only if the row is not a record yet. A
   free copy: the pool row is deleted.

Every statement is keyed on the copy: a user that belongs to no copy matches none of them. They are
set-based (`ExecuteDeleteAsync`, `ExecuteUpdateAsync`): nothing is loaded and nothing is tracked,
so `EnforceTransactionImmutability` does not see them. **That is deliberate.** A demo copy's ledger
is invented data that belongs to nobody, and it is the only ledger the recycler can reach. ADR-0044
D6 named "making ledger rows deletable" as one of three redesigns and proposed none; this is not
that redesign, and D6 now says so in place. The delete lives in the Seeder, which no request
reaches: the API holds no statement that deletes a user or a ledger row (ADR-0037 rejected one for
the auth service).

`DemoCopyRecycler.TablesOfACopy` names every table that holds a copy's rows and how its rows leave.
A table it does not name either stops the delete of every copy that wrote to it or keeps its rows
for ever, so `DemoCopyBoundaryTests` walks the model and fails on one.

**11. What stays: every audit row, and a record of the copy that is not proof.** Nothing here adds,
changes or removes an audit row, so the chain verifies after a delete as it did before. The rows a
visitor's actions wrote stay, and their `ActorUserId` names no user any more. The pool row of a
claimed copy stays as the record that the actor those rows name was a demo copy: `OwnerUserId` is
that actor, `ClaimedAt` and `DeletedAt` say when, and the client key is gone. **It is an operator's
record, not proof.** It is written by the same hand that deleted the rows, outside the chain, and
anyone who can write to the database could write one, or remove one. ADR-0044's "the compliant act
and the attack are the same act" still holds: the record tells an honest reader which dangling
actor was a demo copy, and tells nothing to a reader who has to assume the database was written by
someone else. A free copy leaves no record: nobody signed in to it, so no audit row names it.

**What goes with the copy: the evidence pack of its transfers.** `AzureBank.AuditVerifier evidence`
assembles a pack from the ledger row a transaction number names (ADR-0042, ADR-0044 D8). Once a
copy is deleted it answers `NOT ASSEMBLED: no transaction is numbered …` and exits 4 for every
transfer of the copy, as it answers a number nobody issued, while the audit row that names the
transfer (`MoneyTransferred`, or `MoneyTransferredInternally`), and in its `Detail` the
authorisation that paid for it, stays in the chain.

**12. A run ends with a code and one line.** `PoolExitCodes` decides the code from the run's
counts:

| Code | Name | When |
|---|---|---|
| 0 | `PoolOk` | Nothing below applies. A run that found no copy, only the records of deleted copies or no row at all, is 0: it is read as a first fill |
| 10 | `PoolLow` | The run found fewer free copies than `LowMark`, however old, and at least one |
| 11 | `PoolEmpty` | The run found no free copy, however old, and at least one copy that is not a record: visitors may have been turned away |
| 12 | `TopUpIncomplete` | A copy could not be built, and the pool ended below its target |
| 13 | `ForeignUsers` | A user that belongs to no copy exists |
| 14 | `DeleteFailed` | At least one copy's delete threw |
| 15 | `ClaimCeiling` | The day's claims held the top-up below `TargetFree` |

When several apply the run exits with the first of 13, 14, 12, 15, 11, 10: what needs a look before
what was only short. 10, 11 and 15 mean "done, and the pool was short"; 12, 13 and 14 need a look.
"Low" and "empty" count the free copies however old (the line's `was`): a copy too old to count
towards the target is still free until the run that replaces it deletes it. Counted fresh, a pool
nobody claimed from exited 11 whenever its copies grew too old together, about every two days,
with nobody turned away.

A top-up run on its own reports only 12, and 13 when it wrote nothing because the database held
users and not one pool row: a pool that was low when it started is why it was run, and a user
outside every copy beside a pool is on its line and is `recycle`'s 13. The line carries every count
whatever the code, and never an id or an address:

`pool: free=50 was=23 claimed=7 claims24h=12 clientsAtCap=0 seeded=38 deleted(expired=4 hardStop=0 staleFree=11 failed=0) swept(idempotency=120 grants=31) tombstones=412 foreignUsers=0 ceiling=no result=PoolOk`

(The numbers are an example of the shape, not a measurement.) What each count means, and the SQL
that looks behind it, is in [`docs/runbooks/demo-pool.md`](../runbooks/demo-pool.md).

**13. Two commands run the pool, and `seed` and `reset` stay out of it.**

| Command | Does | Exits |
|---|---|---|
| `seed-pool [copies]` | The top-up of decision 8 alone and without its daily ceiling, to N fresh free copies (1 to 500, the range of `TargetFree`), or to `TargetFree` without N. The first fill, and a refill by hand | 0, 12 or 13 |
| `recycle` | Decision 8, whole. The job a deployment schedules; safe at any interval | 0, or a signal from 10 to 15 |

- **The code reaches the process.** Each handler sets the run's code on its invocation, as
  `migrate`, `seed` and `reset` do (ADR-0060): System.CommandLine returns that value, and a value
  put anywhere else is lost. The line is logged at Information when the code is 0 and at Warning
  when it is a signal. A run that is stopped prints no line, says so and exits 1; a failure that is
  no copy's prints a `failed:` line and exits 1.
- **Refused before anything is opened, exit 2,** as the Seeder's other commands refuse: a
  connection string that is missing, unreadable or names no database; a PIN pepper or a demo
  setting their validators refuse, both in one line when both fail; a value that cannot be read as
  its setting's type, named by its key and never by the value; and the demo off.
- **On an Azure SQL name they run**, where `seed` and `reset` are refused: the demo's database is
  one. Why that is safe, and that the job signs in as the app's database user and not the
  migration's where a deployment has the two, is ADR-0060's note on its decision 5.
- **On the Azure deployment no job runs them yet**
  ([ADR-0061](0061-the-demo-is-deployed-to-azure-container-apps-with-no-database-password.md)),
  and the change that adds one amends that record. ~~A job that is not manual is refused by the
  resource group's Deny policy until `Schedule` is in `allowedJobTriggers` (`infra/main.bicep`;
  ADR-0061's decision 2).~~ *(struck 2026-10-05,
  [ADR-0064](0064-the-azure-deployment-runs-the-demo-from-a-scheduled-pool-job.md), decision 4:
  not through that parameter, which holds for every job and would let the migrate job be
  scheduled too. The policy has a second one, `scheduledJobs`, and a job whose name is in it
  may run on a schedule: `main.bicep` names `azurebank-pool`.)* The PIN pepper becomes a secret
  of the job too, where ADR-0061's
  decision 9 keeps it in the app. The job carries the identity `azurebank-app` and a connection
  string like the one only the `api` container references today, where `infra/README.md`'s
  read-back expects each database identity on exactly one resource. And the deployment identity
  holds its role on the app and the migration job and nowhere else (ADR-0061's decision 8). The
  API there sets no `Security__PinPepperKeyId`,
  so the key id the job holds is 1. An execution that exited 7 read `Failed` in ADR-0061's trial,
  so the signals 10 to 15 may show there as failed executions; that was not tried.
  *(2026-10-05, [ADR-0064](0064-the-azure-deployment-runs-the-demo-from-a-scheduled-pool-job.md):
  that change is written, and on Azure nothing of it has run. `infra/main.bicep` holds the job,
  `azurebank-pool`, behind a switch that is off by default: `recycle` every four hours, the
  identity `azurebank-app`, and two secrets of its own, a copy of the app's connection string and
  of its pepper. The deployment identity's role is assigned a third time, on that job. The
  read-backs with each identity on one resource are left as they were measured, and the
  runbook's third session says what they are expected to give with the demo on. A start by hand
  ends by the run's exit code and not by the execution's status, since how Azure shows the
  signals is still not tried. No job runs the commands on Azure until that session has.)*
- **On Azure the app goes live on an empty database with its registration open**
  (`infra/README.md`, "What is not here"). ~~A user who registers before the first `seed-pool`
  makes it exit 13 on the demo's own database and write nothing, as on any database with users
  and no pool row, until a person removes that user.~~ *(struck 2026-10-05,
  [ADR-0064](0064-the-azure-deployment-runs-the-demo-from-a-scheduled-pool-job.md): on the
  database a first deployment leaves, nobody can register before the first fill. Measured that
  day on a local stack whose database only `migrate` had touched, 17 migrations and no row: one
  registration with a body that passes the rules was answered 500 and left 0 users and 0 roles,
  and the API's console said why, `Role USER does not exist.` After `seed` had run on that
  database the same body was answered 201. The registration gives the new user a role inside
  its one transaction, and `migrate` creates none: the roles come with the pool's first run,
  which creates them before its first copy. So the user that makes a run exit 13 is one who
  registers after that run, through an app whose flags are off. The exit 13 on a database with
  users and no pool row stands as it was measured. Not measured on Azure.)* ~~Whether
  registration closes before the first fill is for the change that adds the job.~~ *(struck
  2026-10-05, ADR-0064: it does. One run of the template turns the flag on in both containers
  and builds the job, the job after the app, and the runbook starts the first fill only once
  the app has been read as the demo. Not run on Azure.)*
- **`seed` and `reset` refuse demo mode** (exit 2, nothing opened): `seed`'s four users have a
  password and a PIN in this repository, and `reset` drops the database. **Both also refuse a
  database that holds any pool row**, a record included, whatever the flag says: a job whose
  environment lost the flag is still pointed at the demo's database. That refusal costs one count
  before anything is written; `reset` asks after its prompt and before its drop, and reads first
  whether the pool's migration was applied, so a database that does not exist, or one migrated
  before the pool's table, is reset as before.
- **A stop reaches Identity.** Identity's managers take no token; the two the Seeder registers
  read the scope's `RunCancellation`, and the builder sets it before the roles and the copies'
  users are created. It was not set before the commands existed: a run stopped at its first
  statement to the roles table sent that statement and two more with no token.
- **Locally**, `compose.demo.yaml` over `compose.yaml` runs `seed-pool` in place of `seed`, and
  `recycle` under the profile `pool`. One volume holds one kind of database: `seed` and `reset`
  refuse the pool's (exit 2), and `seed-pool` exits 13 on the fixed demo's, writing nothing.
  *(Amended 2026-10-04, ADR-0063: it also runs the API and the BFF with `Demo__Enabled=true`,
  and asks for a ninth variable, `DEMO_CLIENT_KEY_SECRET`, which every command that loads the
  file needs, the pool's two and `down` among them. Until then the app under it ran with the
  demo off: its registration was open, and a user registered there made every `recycle` exit
  13.)*

## What still crosses copies

The copies share one database and one deployment, so these are shared, and stay so:

- **The pool.** One visitor's claims can leave none for the next; the caps that bound this belong
  to the claim.
- **The limits keyed on an address.** The BFF's global limit and its `auth` policy count requests
  per client address, so visitors behind one address share them.
- **A rename to a handle another copy holds** answers 409 `AZURE_TAG_TAKEN`: handles are unique in
  the whole table, and the rename asks the whole table. It confirms that some user holds that
  handle, and nothing else about them.
- **The database and the audit chain.** Every copy's writes share the database's capacity, and
  every audited write of every copy queues on the chain's single tail (`Audit:TailTimeoutSeconds`).
- **A caller whose own row is gone.** Once its copy is deleted, a caller still holding a valid
  access token has no copy to read and is resolved as a user outside every copy: its lookup can
  return the masked name of a user outside every copy, until that token expires
  (`Jwt:ExpirationMinutes`). It is left as it is: the lookup reads no setting, and four older tests
  call it with a caller id that has no row and expect it to find a user.

## Rejected

- **Handles unique per copy**, so every copy could use `jane`. A resolver that forgot the copy
  would then pay a user of another copy. With random suffixes a resolver that forgets the copy
  still finds nobody it should not.
- **Selecting what to delete by age, name or address.** The column is the only key: a name pattern
  or an age would one day match a user who is not a copy's.
- **Deleting the record of a claimed copy with its users.** Then nothing would say which dangling
  actor in the audit trail was a demo copy.
- **Flagging a copy deleted instead of deleting it.** The rows would stay, and the database would
  fill with them.
- **Deleting row by row through the change tracker.** The ledger's guard refuses it, and a copy a
  visitor filled would be one statement per row.
- **Deleting a copy's audit rows.** It breaks the chain, and a partial purge reads as tampering
  (ADR-0044 D6).
- **A command that deletes every copy, in use or not, for a PIN-pepper rotation.** The hasher's
  ring already verifies old hashes and re-hashes them on use. A rotation goes in ADR-0011's order:
  the new pepper added to every ring first, the API's included, then activated, and the old one
  removed when no stored PIN hash carries its key id any more. The runbook has the order and the
  count.

## Consequences

**Positive**

- No free copy can be signed in to, no copy's password is in this repository, and every visitor's
  demo is their own.
- A copy that cannot be deleted costs one copy, never the run or the pool.
- A table added later that holds a copy's rows fails a test before it can make copies undeletable.

**Negative**

- The audit rows of every deleted copy stay for ever, with an actor that names no user. They are
  what `recycle` cannot return when storage runs short. On Azure the database is Basic, 2 GB at
  most, and none of ADR-0061's ~~four~~ alerts watches its size. *(2026-10-03: three alerts
  since the one on the log volume was removed that day, ADR-0061's decision 10; none watches
  the database's size, as before.)*
- The record of a deleted copy is the operator's word (decision 11).
- The evidence pack of a deleted copy's transfer is gone, and the verb answers for it as for a
  number nobody issued (decision 11).
- A record is not a copy: a pool whose every copy was deleted is read as a first fill, so its next
  run exits 0, not 11, although visitors may have been turned away since the run before.
- A copy that failed to build is not on the summary line: the summary counts it (`BuildFailed`),
  the line does not print it, and exit 12 says one failed only when the pool ended short.
- A run whose top-up came up short keeps every free copy too old to count: until a run reaches its
  target, a visitor can be given a copy whose history ends more than `MaxFreeAgeHours` ago.
- Under the ceiling those copies go once the lowered target is reached, so a run can end with
  fewer free copies than it found (`was`): the day's claims hold the pool there, and exit 15 says
  so.
- A failure that is no copy's (a count, the roles the top-up creates first, a sweep) ends the run
  with no summary and no code of the pool's.
- With `Demo:CopyLifetimeHours` below 24, a copy can be deleted, and its client key removed, inside
  the 24 hours its claim is counted over: that claim then stops counting towards its client's cap.
- `seed` and `reset` read the database before they refuse the demo's: exit 2 there comes after a
  connection was opened, where every other 2 of the tool comes before (ADR-0060, decision 4's
  note).
- A compose volume that one of `compose.yaml` and `compose.demo.yaml` filled stops the other's
  one-shot, and with it the API, until `docker compose down -v`. Under `compose.demo.yaml` a later
  `up` builds a new set of copies once the old ones are 44 hours old, and only `recycle` deletes
  the old ones.
- Two runs at once each top up from their own count, so together they can build up to twice the
  target, and twice what the day's claims leave room for. Nothing is lost or deleted twice, and the
  extra copies go when they grow too old; a schedule whose runs do not overlap avoids it. No lock
  serialises the runs.

**Neutral**

- `seed` builds the fixed demo's 26 rows from `DemoLedger`;
  `SeededDemoDataSqlServerTests.TheSeededLedgerIsTheDemosHistoryRowForRow` holds them row for row.

## Validation

The tests are in `backend/tests/AzureBank.Tests`.

- `DemoPoolRecycleSqlServerTests.ACopyAVisitorUsed_IsDeletedWhole_ItsAuditRowsStay_AndTheChainStillVerifies`:
  a copy used through the API (two transfers, a closed account that had movements, consumed and
  pending authorisations, idempotency records, a changed PIN and its notice) is deleted with five
  DELETE statements and its record; no row of its users is left; the audit rows are as many as
  before, their actor is the record's `OwnerUserId` and resolves to no user, and the chain verifies.
- Its controls run the recycler's own statements with one safeguard taken out:
  `DeletingTheOwnersRowsAlone_IsRefusedByTheLedgersOwnForeignKey` (547 on
  `FK_Transactions_Transactions_RelatedTransactionId`),
  `LeavingTheSoftDeleteFilterOn_IsRefusedOnTheClosedAccount` and
  `WithoutATryAroundEachCopy_TheSameFailureEndsTheRun`.
- The other decisions of `recycle`, among them:
  `ACopyWithALiveGrant_IsSkipped_AndDeletedOnceTheGrantIsRevoked`,
  `PastTheBackstop_ACopyIsDeleted_EvenWithALiveGrant`, `AStaleFreeCopyAVisitorClaimsFirst_IsSkipped`,
  `OnADatabaseWithUsersAndNoPool_RecycleExitsThirteen_AndWritesNothing` and
  `BesideAPool_AnOrdinaryUserLosesNothing_AndAnExpiredCopyIsStillDeleted`; each exit code is
  produced on purpose by a test of its own, and
  `APoolLeftWithOnlyRecords_IsReadAsAFirstFill_AndExitsZero` pins the exit 0 above.
- What a review of this record found, each with its test:
  `APoolWhoseFreeCopiesAllGrewTooOld_IsRebuilt_AndExitsZero_NotEleven` (it exited 11),
  `AFreeCopyThatGrowsTooOldWhileTheTopUpRuns_IsLeftForTheNextRun` (the same run deleted it and the
  pool stayed one short), `AGrantThatEndedLessThanFiveMinutesAgo_StillKeepsTheCopy` (deleted a
  minute after its last session ended),
  `WhenNoCopyCanBeBuilt_TheFreeCopiesTooOldToCount_AreKept` and
  `WhenTheTopUpComesUpShortAfterBuildingSome_NoFreeCopyTooOldToCountIsDeleted` (a run deleted
  every free copy too old to count whatever its top-up built), with
  `UnderTheCeiling_TheFreeCopiesTooOldToCountAreDeleted_OnceTheLoweredTargetIsReached` for the
  lowered target, in `DemoPoolRecycleSqlServerTests`; and in
  `DemoPoolCommandSqlServerTests`, `Reset_OnADatabaseThatHoldsThePool_IsRefused_AndDropsNothing`
  (with the flag off, `reset` dropped the pool) and
  `BesideAPool_AUserOutsideEveryCopy_DoesNotStopSeedPool_AndRecycleReportsIt` (`seed-pool` exited
  13, and the API that waits for it under `compose.demo.yaml` never started).
- `DemoPoolRecycleSqlServerTests.ADeletedCopysTransfer_HasNoEvidencePack_ThoughTheAuditRowThatNamesItStays`:
  the pack is assembled for the copy's transfer while the copy exists, and after the delete the
  verb exits 4 with `NOT ASSEMBLED`, while the `MoneyTransferred` row is unchanged.
- `DemoPoolSeedSqlServerTests`: 37 rows per copy, no password hash, a second run adds none, a
  collision redraws the whole copy and leaves no half copy, and a free copy's owner cannot be signed
  in to with any password.
- `DemoIsolationSqlServerTests`: a handle in another copy is answered byte for byte as an unknown
  one by the lookup, the mint and the transfer; two users outside every copy still find and pay
  each other.
- `DemoCopyBoundaryTests`: every comparison of a handle in `backend/src` written as `AzureTag ==`,
  `== x.AzureTag` or an `Equals` call on a handle is classified, and the two that hand another user
  to the caller compare the copy. It is a text scan: a comparison written another way, such as
  `!=` or a `ToLower()` before the `==`, passes it unseen. Every table that holds a copy's rows is
  one the recycler names, and the ones it leaves to the database really cascade; every foreign key
  into a table the delete empties, or into the pool's own, comes from a table the recycler names or
  one that leaves with it, followed through cascades (its control, a model with two tables the
  recycler does not know, is found only by that walk); the pool's code never uses `System.Random`.
- `DemoCopySchemaSqlServerTests`: the columns, the two constraints, the filtered indexes, and the
  migration taken back.
- `DemoPoolCommandSqlServerTests`: each command returns its run's code and logs one line, at
  Warning for a signal; `seed` refuses a database holding a free copy, and one holding only a
  record, while on one with no pool row it seeds; a run stopped at Identity's first statement is
  stopped there, for each command; and a login that holds `db_datareader` and `db_datawriter` and
  nothing else runs both through a whole cycle, while it is refused `CREATE TABLE` (262).
- `Unit/Tools/SeederCommandTests`: every refusal of the tool holds for the two commands, with
  nothing opened, and the demo off refuses them; `seed` and `reset` refuse demo mode; on an Azure
  SQL name the two commands reach for the server; a cancelled run prints no line; the count
  `seed-pool` takes is 1 to 500; a pepper and a demo setting that both fail are one refusal, and a
  value that cannot be read is one that names its key and never the value.
- `SeederProcessTests`: the real process exits 2 for either command with the demo off, and for a
  flag that is neither true nor false, and `recycle` that finds fewer free copies than the low mark
  exits 10, the process's own code.

Each guard of decision 13 was then broken again, one at a time, and a test above failed: 20
removals, 20 failures, among them the code each command passes on and the token the builder hands
to Identity. So did each branch of `PoolExitCodes` that gives a code from 10 to 15: 7 removals, 7
failures, the process test among those for 10. The fixes the review brought were broken the same
way: 12 removals, 12 failures. A 13th, a check that the database exists before `reset` reads its
migration history, failed no test: EF makes that check itself before it reads the history, so the
check was removed. The condition step 5 of decision 8 waits on was broken the same way: removed,
the two tests that keep the old copies failed; compared with `TargetFree` instead of the lowered
target, the ceiling's test failed alone. Each file was put back byte for byte, checked by its
SHA-256.

**Measured on 2026-10-03** on the compose stack, in three runs, each under a project name of its
own (SQL Server 2022 CU27, the tools image built from this change, Production). A row holds one
run unless it says otherwise; the last run came after the review's fixes. Times are read from the
container's own timestamps, between its first line and its pool line, unless a row says otherwise:

| What was run | What happened |
|---|---|
| `migrate`, then `seed-pool 5` with `Demo__Enabled=true` | Exit 0 both. 5 pool rows, 15 users, 15 role rows, 20 accounts, 130 ledger rows: 185, counted per table; no user with a password |
| `seed` with the flag on; with it off, on the same database; `reset --confirm` with it on | Exit 2 each, with its refusal: demo mode; "the database holds the demo pool's rows (5)"; demo mode. No fixed user was written |
| `migrate` and `seed`, the flag off, on a second database | Exit 0 both: 4 users, 5 accounts, 26 ledger rows |
| `seed` and `reset --confirm` on a `*.database.windows.net` name | Exit 2 each, the Azure SQL refusal; the containers ran 0.47 s and 0.41 s (second run) |
| `seed-pool` and `recycle` on that name, the flag on, no retry | Exit 1 each: not refused; they reached for the server, whose name does not resolve |
| `recycle` with `TargetFree` and `LowMark` at 6, the pool holding 5 | Exit 10, also as the container's exit code (`docker inspect`): `was=5 seeded=1 result=PoolLow` |
| every copy marked claimed by SQL, then `recycle` | Exit 11, also as the container's exit code: `was=0 claimed=6`, and the pool topped up to 6 |
| three copies back-dated by SQL, one free and 50 h old, one claimed 30 h ago, one claimed 30 h ago with a live grant inserted; then `recycle` (all three runs) | Exit 0. The first gone with its row; the second a record, with no user and no client key; the third left with its three users; the pool back at its target. In the last run, with five free copies before: `was=3 seeded=3 expired=1 staleFree=1`, the stale copy counted in `was` |
| every free copy back-dated 45 h by SQL, nothing claimed, then `recycle` (last run) | Exit 0, `was=5 seeded=5 staleFree=5`. Before the review's fix the same pool, run on LocalDB at +48 h, exited 11 with `was=0` |
| `seed-pool 100` on a pool of 5 or 6 (all three runs) | Exit 0, 94, 95 and 95 copies. About 93 to 103 ms a copy: 9.75 s and 9.56 s for the two runs of 95, and 4.63 s for 50 copies from the roles to the line in a first fill (the run of 94 was timed by whole seconds only: 9 s). `sp_spaceused` reserved +2,760, +3,016 and +2,824 KB: 29 to 32 KB a copy |
| a claimed copy given 474 more ledger rows by SQL, 500 in all, then `recycle` (first two runs) | Exit 0, the copy deleted. Its six statements took 90 and 93 ms by EF's own timings, 64 and 58 of them the ledger's |
| a login with `db_datareader` and `db_datawriter` only, on a server whose roles existed: `seed-pool 102`; `recycle` with one copy expired and one stale (first run) | Exit 0; exit 0 with `expired=1 staleFree=1`. The same login's `CREATE TABLE` refused with 262, and its first fill with the roles, were measured on LocalDB (`DemoPoolCommandSqlServerTests`) |
| the same login without `db_datawriter`: `seed-pool 103` (first run) | Exit 12, three copies refused with 229 on `DemoCopies` |
| `docker stop` sent about 4 s into building copies: to `seed-pool 500`, and to `recycle` with a target of 400 (last run) | Exit 1 and the command's own cancellation line each, 0.15 s after the stop was sent; 37 and 36 copies built by then, and no copy without its three users |
| `reset --confirm` with the flag off: on the pool's database; on a database with no pool row; on one that does not exist (last run) | Exit 2, "the database holds the demo pool's rows (102)", and the database's id and creation time unchanged; exit 0, dropped and created again; exit 0, created, with no retry logged |
| `recycle` with `Demo__Enabled` set to a word; with no pepper and `TargetFree` at 0 (last run, no network) | Exit 2 each: "Demo:Enabled holds a value that cannot be read as Boolean", the word printed nowhere; one line naming both failures |
| a user outside every copy added by SQL beside the pool, then `seed-pool 103`, then `recycle` (last run) | Exit 0 with `seeded=3 foreignUsers=1`; exit 13, the user left in place |
| from an empty volume, the `seed` service of `compose.demo.yaml`; then its `recycle` service | Exit 0, 50 copies, 10.4 s from `migrate`'s start to the end of `seed-pool` (second run); then exit 0 with nothing to do |

**Not measured here:** anything on Azure SQL, a managed identity's token among it; ~~a copy used
through the front door and then deleted by a run of the command (no claim exists yet: the SQL
Server test above uses the API in process);~~ `recycle` stopped while it deletes.
*(Struck 2026-10-04, ADR-0063: measured that day on the compose stack, twice. A copy claimed
through the BFF and used there (transfers, a changed PIN, a renamed handle, a drained account, an
account opened and renamed, a locked sign-in and a locked PIN) was deleted by a run of `recycle`
once its claim was moved 24 h 6 min back by SQL: exit 0, `expired=1`, no row left for its three
users in the tables `TablesOfACopy` names (seven of them counted in the first run, all eleven in
the second), its record there with no client key, its owner's 9 audit rows still 9, and `verify`
answered `CHAIN INTACT`. ADR-0063's Validation has the line and the counts.)*

## What would change this

- A key to users, accounts or ledger rows, or to any table the delete empties, added by a later
  migration: the recycler names its table (the model walks fail until it does).
- Runs that overlap where the demo is deployed, which the line shows as more free copies than the
  target: a lock that lets one pool run at a time.
- A need to tell the job's deletes apart from the app's, or to revoke the job alone: a database
  user of its own with the app's two roles (ADR-0060, decision 5's note).
- Building copies too slow for the job that runs `recycle`: free copies re-dated in place instead
  of rebuilt, which is a second way around the ledger's guard and needs its own line here.
- A new query by handle in `backend/src`: classified, and if it hands a user to the caller, it
  compares the copy. The scan finds it only in the forms it reads (Validation).
- A `hardStop` above 0: sign-in was accepted past a copy's end, and that is fixed where sign-in is
  decided, not here.
- A pool command that needs a right the app's database user does not hold: the job gets a user of
  its own with that right, never the migration's.

## Related

- [ADR-0044](0044-the-audit-trail-is-append-only-and-chained.md) D6: why audit rows are never
  purged, and the note this record put there.
- [ADR-0042](0042-a-transfer-authorisation-is-bound-and-spent-once.md): the authorisations a copy's
  delete takes with it.
- [ADR-0037](0037-atomic-registration.md): why the API has no statement that deletes a user.
- [ADR-0057](0057-the-bffs-refresh-token-is-one-reusable-grant-per-session.md): the grant a
  session renews with, which decides when a copy is in use.
- [ADR-0011](0011-pin-hash-pepper.md): the pepper ring the PIN hashes rotate on.
- [ADR-0060](0060-migrations-run-as-a-one-shot-container-before-the-app.md): the tools image, the
  Seeder's exit codes, and the Azure SQL rule the pool's commands are the exception to.
- [ADR-0063](0063-a-visitor-claims-a-prepared-copy-instead-of-registering.md): the claim, its
  caps, the sign-in gate and closed registration, decided on 2026-10-04; the notes above that
  carry its number.
- `backend/tools/AzureBank.Seeder/README.md`: the commands' contract.
- [`docs/runbooks/demo-pool.md`](../runbooks/demo-pool.md): the exit codes, the counts and the SQL
  behind them.
