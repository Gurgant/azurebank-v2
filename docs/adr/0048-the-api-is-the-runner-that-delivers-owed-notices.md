# ADR-0048: The API is the runner that delivers owed notices

**Status:** Accepted · **Date:** 2026-09-04 · Reverses ADR-0045's D3 ("rendered on demand by the
operator tool, never by the API") for the reason that ADR's own deferral record ratified on the same
day, and fires the "relay" trigger both name. Adds a lease to the notice row, one hosted loop to the
API, and moves the rendering and the last hop into Infrastructure so both runners share them.
Changes no endpoint, no status code and no client. The last hop is still a pickup directory:
nothing here sends, and the ADR says so in the same breath as before.

## Context

ADR-0045 recorded the obligation and stopped at a verb: the API writes an owed-notice row in the
same save as the enrolment, and an operator renders it into a pickup directory when they choose to.
D3 declined a hosted loop, for the anchor's reason — nothing in this deployment runs between
sessions — and its deferral record listed what a relay would need. On 2026-09-04 that record was
ratified in the other direction: **the API is the runner first**, with an in-process claim protocol
that a later Azure Function will share, and a flag that names which runner is live.

Measured before any of this was written, on the running stack (BFF `:5000` → API `:7215`, main
`d79beb1`, the `AzureBankT15` LocalDB store):

```
18:42:09Z  enrol a PIN                    -> 200, one SubscriberNotices row, DeliveredAt NULL
18:42:54Z  the same row, API+BFF running  -> DeliveredAt NULL, DeliveryReceipt NULL
           files in the API that read SubscriberNotices: AuthService only, and it writes
           API log lines mentioning a notice or a relay since start: 0
```

A notice was owed for as long as nobody ran a verb. The threat model the notice exists for — a
stolen session enrolling a PIN behind the password, or changing one behind the PIN alone (ADR-0047)
— is measured in minutes, and "when the operator remembers" is not a bound on minutes.

## Decision

**D1 — One hosted loop in the API, `NoticeRelayService`, and it says what it is.** The API's first
correctness-bearing loop, after two hygiene sweeps. It follows their shape exactly —
`PeriodicTimer`, first look one full period after start, a catch-all per sweep logged at Error,
cancellation absorbed as shutdown — and it is registered where they are, beside its options in
`AddApplicationServices`, so a second host inherits it. It is ALWAYS registered and reads
`Notices:Runner` once at start; unless that names this process it logs ~~that no runner is live~~
the flag's value and that THIS process delivers nothing *(struck 2026-09-09: nothing ever emitted
"no runner is live". The line is `"Notice relay: runner is {Runner}; this process delivers nothing
(Notices:Runner)"` — the flag's value and this process's own abstention, both of which it has. It
cannot speak for another host, because nothing here probes a process; that is why every other reader
in this system waits on the LEASE instead.)* and
returns — ~~at Warning for `Function`, which nothing implements yet, so an operator who set it is
told that notices stay owed~~ *(struck 2026-09-08: at Information for every value, ADR-0051 D4;
correction below)*. That flag, not the lease, is what keeps two KINDS of runner from both
sending; two hosts of this API with the flag set both run the loop, and the lease keeps them off
each other's rows.

_Correction (2026-09-08, ADR-0051, the Function runner) — **the Warning is gone, and the sentence
that justified it was the thing that expired.** `AzureBank.Functions.NoticeRelay` is that runner
now, so `Notices:Runner=Function` is a correct configuration in which the API is simply not the one
delivering — the same fact `None` states, and at the same level. A Warning for a correct configuration teaches an operator to ignore warnings.
The test pins both halves: the level, and that the old claim does not survive the level change._

**D2 — The claim is a lease on the row, taken in one statement, and shared.** Two nullable
columns, `LeasedUntil` and `LeasedBy`, paired by `CK_SubscriberNotices_Lease` the way the delivery
pair already is. A claim stamps up to `Notices:BatchSize` of the oldest owed rows whose lease is
null or lapsed with the runner's name and a lease end in ONE set-based UPDATE, so the database
serialises two claims and the second finds nothing free among them; the batch caps a runner's LIVE
work, rows it still holds from a failed delivery included, so a runner that keeps failing never
holds more than one lease can deliver; and those held rows are RENEWED to the new sweep's lease
end before anything is delivered, so nothing this sweep writes can lapse before the sweep's own
check says so. The runner then re-reads what it holds BY NAME — never by the instant it wrote, so a
store that rounded a timestamp could not make it claim N and read back none — and delivers that.
The protocol is `NoticeClaim` in Infrastructure, and the verb uses it too (D5). Proved on SQL
Server: a claim held open in an uncommitted transaction blocks a second runner's sweep — it had not
returned 1.5 s later — and once the first commits the second claims zero; two runners sweeping six
fresh rows back to back claim exactly six between them and deliver six files, none twice; a row
under a live lease held by a third name is left alone; a row whose lease has lapsed is taken; the
store refuses a lease with either half missing. `DeliveredAt` stays the concurrency token, and the
mark clears the lease. A sweep that outlives its own lease stops delivering and says so: the rows it
has not reached are free to the next claim, and the lease is validated to be at least twice the
period so that is the exception, not the rule. The clocks compared are the runners' own: two hosts
skewed by more than a lease would each see the other's live lease as lapsed, so a lease must exceed
the period plus the skew a deployment tolerates.

_Noted 2026-09-08 (ADR-0051): **the SWEEP joined the protocol in Infrastructure, and the API's own
tests are the evidence it only moved.** This paragraph shares `NoticeClaim`; D4 shares the per-row
unit; the sweep between them — renew, read holdings, capacity, claim, re-read, the per-row lease
check, the outcome arms — was `internal` in `AzureBank.Api` and is `NoticeSweep` in Infrastructure
now. 56 of the 57 notice tests passed through the extracted sweep with no edit; the single red was
D1's level change above._

**D3 — At-least-once, and the lease does not make it once.** A runner that hands a message to the
transport and dies before marking is succeeded when its lease lapses. What happens then depends on
the transport. With a sending one, that row goes out twice: exactly-once is not the runner's to
give, it needs an idempotency key the transport or the provider honours, or de-duplication at the
recipient, and until one exists a duplicate is an accepted outcome. With the pickup directory that
ships, the file IS such a key: the second attempt into the same directory is refused by the
exclusive create (ADR-0045 D4), nothing goes out twice, and the observable state is a row that
stays owed beside the file it produced — held, retried and refused every lease, logged at Warning
each time, exit 6 from the verb — until an operator applies the runbook (the row is the truth:
move the file and run again, or mark the row). Both shapes are pinned: a recording transport shows
the retry and the lapse; SQL Server and the real transport show the refusal and the row left owed.
The verb already named this shape for its own duplicates; the relay inherits the honesty.

**D4 — One unit of work, shared.** The verb's per-row steps — address, evidence, render, transport,
mark — are `NoticeDeliveryRun` in Infrastructure, and the verb and the relay both call it. The verb
keeps every word it printed before; the relay logs. Neither ever sees the address: the run returns
outcomes, and a transport failure is reported by exception TYPE only, because an I/O message can
echo the path and a relay's refusal can echo the recipient. A test reads every log line of a sweep
that delivered one notice and refused another for an injected `Bcc:` in its address, and finds
neither address in any of them.

**D5 — The verb claims too, and steps aside for what another runner holds.** `notify` takes the
same lease the relay takes, under its own name (`verb/{host}/{pid}/{8 hex}`, two minutes), and
delivers only what it holds; rows another runner holds under a live lease are counted and named,
and taken only once that lease has lapsed. A verb that merely read free rows and delivered them
would leave a window between its read and its write in which the relay's claim takes the same
rows; claiming closes it. The verb's exit 2 now has two readings, and the line says which: nothing
owed, or everything owed ~~leased by a live runner~~ held under another runner's LIVE LEASE
*(struck 2026-09-09: the count comes from `LeasedUntil > now`, which proves the lease is unexpired
and nothing about the process. A runner that claimed and died is indistinguishable here from one
mid-delivery — the distinction the rest of this ADR keeps, at D3 and at "under a live lease" three
times above, and this one sentence dropped.)*.

**D6 — Options, off by default, refused when partial.** The `Notices` section: `Runner`
(`None|Api|Function`, default `None`), `PickupDirectory`, `Contact`, `PeriodSeconds` (15),
`LeaseSeconds` (120), `BatchSize` (100) — and, added to this list on 2026-09-09, `Schedule`, which
ADR-0051 put in the section as the Function's cadence and which no host of THIS ADR's ever reads.
The key shipped and the enumeration did not move, which is the drift the notes below exist to catch.
`None` is the default on purpose: a pickup directory is a
spool of addresses at rest and must sit outside any git tree, so no default path can ship. When
`Runner` is `Api` the directory must exist and pass the verb's own git-tree guard (now shared from
Infrastructure), the contact is mandatory content of every notice (NIST SP 800-63B-4 §4.6), and the
lease must be at least twice the period, so that a sweep and the next claim do not overlap in the
normal case — a bound on overlap, not on duplicates: a delivery already in flight when a lease
lapses still completes, and D3 is what says so. The ranges on the period and the lease apply
whatever the runner: an out-of-range value is a misconfiguration even when nothing runs. A partial
set stops the host, with a message that names the key and the fix, which is the only thing an
operator sees. Nothing in the section is a secret and none joins the six.

_Noted 2026-09-08 (ADR-0051 D3): **"refused when partial" was true of the API and of nothing else.**
~~Every rule this paragraph describes was written `o.Runner != NoticeRunner.Api || …` in the API's
composition root, so a section naming any OTHER runner was not checked at all~~ *(struck 2026-09-09:
"every" and "at all" are both wrong, and the paragraph this note sits against had already given the
counterexample — the ranges apply whatever the runner. There are FOUR runner-guarded validators in
that root and a separate, unconditional `ValidateDataAnnotations()` carrying THREE `[Range]` rules:
the period, the lease and the batch size. `main`'s own comment beside those validators says it, and
`main` carries a green test saying it — `APeriodBelowTheRange_IsRefused_WhateverTheRunner`. MEASURED
on the real API binary rather than read: `Notices__Runner=Function` with `Notices__PeriodSeconds=4`
refuses to start, `OptionsValidationException … 'PeriodSeconds'`, which is the exact configuration
this sentence called unchecked.)* **FOUR of the rules it describes** were written
`o.Runner != NoticeRunner.Api || …` in the API's composition root — the contact, the directory's
existence, the git-tree guard, and the lease against the period — so a section naming any OTHER
runner was not checked against THOSE FOUR: with `Notices:Runner=Function`, `main` accepts a missing
contact, a pickup directory that does not exist, and one INSIDE A GIT REPOSITORY, in silence.
Correct while the API was the only host that could
deliver. THREE of the four — the contact, the directory's existence, and the git-tree guard — are
`ValidateAsRunner(NoticeRunner)` in Infrastructure now, asked by each host about itself; a host the
flag does not name still starts, because refusing over a directory it will never write to would take
one runner down for the other's misconfiguration. The FOURTH is not shared: `LeaseSeconds` against
`PeriodSeconds` belongs to a host whose cadence IS `PeriodSeconds`, which is the API and nothing
else, so it is `ValidateThePeriodItSleepsFor` and only the API adds it. The Function ticks on a
trigger expression and never reads `PeriodSeconds`; it compares the lease against the interval
between its OWN consecutive ticks and warns per tick instead (ADR-0051 D5)._

**D7 — Logging, and no `SecurityEvent`.** Information per delivered notice — reference, kind,
receipt; Warning for an unusable address, an unrenderable kind, a transport failure and a missing
audit row; Error for a sweep that failed as a whole. No new `SecurityEvents` constant, for
ADR-0045's reason: an owed notice is not a security event and a delivered one is a receipt, and a
constant would move every event-count guard for a state that is neither.

## Alternatives declined

**A second deployable now.** The ratification put the Function AFTER this: same claim protocol,
rehearsed locally against Azurite, selected by the same flag. Building it first would have meant a
second host, a second configuration surface and a queue, before the protocol they would share had
been proved on the store it runs against.

_Noted 2026-09-08: **taken, in this order, and the order was the point.** ADR-0051 builds it on the
protocol this ADR proved on SQL Server first, so the Function inherits `NoticeClaim`,
`NoticeDeliveryRun` and now `NoticeSweep` rather than re-deriving them. It brought no queue and no
second configuration surface worth the name: one `Notices` section, read by both hosts, with one key
of its own (`Notices:Schedule`, because a trigger's schedule must be bindable before any code runs)._

**A queue instead of a lease.** A queue would carry a copy of the obligation, and the row is the
obligation: written in the enrolment's own save so it is never lost and never survives a rollback
(ADR-0045 D1). A lease on the row keeps that property; a queue would have to be reconciled with it.

_Noted 2026-09-08 (ADR-0051 D2): **this paragraph is what closed the "timer- or queue-triggered"
question the backlog left open.** A queue TRIGGER needs a producer, and the only honest producer is
the row, so a queue trigger would be this declined design wearing a trigger. The Function is
timer-triggered, and what Azurite provides is the HOST's own storage — a blob singleton lease that
elects one host among instances of one app, and NOT the timer's schedule state: ADR-0051 D2 pins
`UseMonitor = false` on the trigger, so no ScheduleMonitor is attached and none is persisted. That
singleton is a
second one at a different layer; it knows nothing about this relay or the verb and does not replace
the row lease._

**Claiming row by row under the concurrency token.** Possible, and it is what the verb's mark does;
but a sweep that claimed N rows in N round trips would spend N chances to interleave with another
runner. One set-based statement is one chance, and the database takes it.

**Deleting the verb.** It is the operator's tool for a store where nothing runs, and for the day a
runner is down; it now coexists with the runner (D5) rather than competing with it.

**Making the pickup transport idempotent** — treating a file already at the notice's path as the
delivery and marking the row. It would close D3's stuck-owed state, and it reverses ADR-0045 D4
(never overwrite; the row is the truth; the orphan is the operator's to clear) inside a change that
does not ratify that reversal. Named here so the next relay item can take it deliberately.

_Noted 2026-09-08 (ADR-0051): **not taken.** This was named "so the next relay item can take it
deliberately", and the next relay item declined it deliberately: ADR-0051 changes which PROCESS
delivers, and reversing ADR-0045 D4 inside it would have hidden a data-model reversal inside a
hosting change. The stuck-owed state D3 describes is unchanged and still the runbook's._

## Consequences

Measured after, on the same stack with `Notices__Runner=Api`, a 5-second period and a 60-second
lease, then again with `Notices__Runner=None` as the control:

```
19:05:59Z  enrol                -> 200; the row is owed, no lease
19:06:03Z  Date: in the file    -> delivered within one period, 4 s after the enrolment
19:07:10Z  enrol (second user)  -> 200
   +5 s    the row              -> delivered, file present, lease cleared: one period
           API log              -> "live as api/GURGANT/32384/bcc2330c, every 5s, lease 60s,
                                   batch …, into …" (see the correction below)
                                   "delivered notice 01a06dd1… (PinEnrolled) as 01a06dd1….eml"
           the address in the API log: 0 occurrences
           notify beside the live relay (before the second enrolment) -> exit 2, "no notice is
           owed": the relay had taken every row; the "leased" reading of exit 2 was not observed
           on the stack, and is pinned by tests instead
19:08:02Z  enrol, Runner=None   -> 200; the row is still owed 16 s later, no lease
           API log              -> "runner is None; this process delivers nothing (Notices:Runner)"
```

_Correction (2026-09-09) — **the `API log` line above was abridged when it was pasted, and in the one
place that matters now.** The emitter is `"Notice relay: live as {RunnerName}, every
{PeriodSeconds}s, lease {LeaseSeconds}s, batch {BatchSize}, into {Directory}"`, and `{RunnerName}`
comes from `NoticeClaim.RunnerNameFor`, which builds `{kind}/{host}/{pid}/{8 hex}` — so a real line
opens `api/GURGANT/…` and names a batch. ⚠️ **Not drift: `git log -S` puts the transcript, the kind
prefix and the `batch` field in ONE commit** (`9cc6c4e`, this ADR's own), so the line never matched
the code and stayed unmatched for FOUR DAYS because nothing re-derived it — `9cc6c4e` is
2026-09-05 and the correction is 2026-09-09. *(This note said "four months" until later the same
day. The repository's first commit is 2026-07-12, so four months was not merely wrong but
impossible: a number written for its ring rather than derived, which is the defect this very note
is about.)* The `api/` half is what matters now, because ADR-0051 D7 makes the kind the thing a
person matches `LeasedBy` against — ~~and this was the only place in the repository showing what an
API runner name looks like~~ *(struck the same day: false. `NoticeRelayService`'s own XML doc has
carried `api/{host}/{pid}/{8 hex}` since this ADR's commit and is on `main`, and three test literals
spell out `api/HOST/1/…`. What was singular about this line is that it is the only place a reader
sees a WHOLE rendered API log line, which is a smaller claim and the true one.)* Now OBSERVED
rather than transcribed, and pinned by
`TheLineThatANNOUNCESTheLoop_CarriesTheKindPrefixedNameAndEveryNumberItClaims`:_

```
Notice relay: live as api/GURGANT/22280/2be449cb, every 5s, lease 120s, batch 100, into …
```

_(That assertion's helper SETS `LeaseSeconds = 120` and `BatchSize = 100` as its own literals —
they equal the type's defaults, but nothing there exercises the default path, so a mutant changing
either default leaves it green. The `60s` above is what the live run used. The live run's batch was
not recorded, which is why it is elided rather than filled in.)_

(The transcript's own 24-second file check reported NO for the first notice: it tested a path built
from an id captured with a trailing newline. The `Date:` header and the row are the evidence.)

The period bounds how often the relay LOOKS, not when a notice lands: a sweep delivers the rows it
claimed one after another, so a backlog, a slow transport, a lapsed lease or a row refused and
retried each push the next file later than one period. Measured with an empty backlog the file
appeared within one period; that is the floor, not a promise. A claim is bounded to a batch
(`Notices:BatchSize`, 100) so a backlog is drained a sweep at a time and a second runner finds free
rows beside the first; the verb claims the same batches in a loop until nothing is free or its own
lease lapses. Getting the file from the directory to the holder is still an operator's step, or a
collector's. The verb remains for every other case. The last hop is unchanged: a file
on this machine, seen by nobody, and the deferral record's preconditions — a provider credential,
addresses the project may write to, a second contact and a remedy behind it — still stand between
that file and "delivered".

ADR-0045 D3 is reversed and struck in place. D7 — delivery recorded on the row, not in the chain —
stands: it was the decision to reopen when "delivered" means something, and with a pickup directory
it still does not; the lease joins the mark on the row rather than moving either into the chain.

The API-side mail limit (`SubscriberNoticeLimitTests`) holds unchanged: the relay uses the pickup
transport, which uses no mail library. Its because-string now names the relay beside the verb.

The `NO AUDIT ROW` finding's limit (ADR-0047) is inherited by the relay — whatever that limit is at
the time — and logged at Warning rather than printed. ⚠️ **It is no longer the limit ADR-0047
recorded.** ADR-0052 made the check EXACT for a notice that names its audit row, and the relay took
that change for free precisely because it shares `NoticeDeliveryRun` with the verb (D1 above): there
is one arm, not two. What the relay inherits is therefore the CURRENT limit — today, that the pair
`(ActorUserId, Event)` cannot see a re-point within one kind — and this line says "inherited", not
"unchanged", so that the next change to it does not have to come back and edit this sentence.

## What would change this

- **A sending transport.** A second `INoticeTransport` behind a provider credential — the seventh
  secret — is the change; the runner, the claim and the options are ready for it, and D3's duplicate
  becomes a mail the recipient sees twice, which is when an idempotency key stops being optional.
- **The Azure Function.** ~~The backlog's next relay item:~~ the same claim protocol in a Function,
  developed against Azurite, and `Notices:Runner=Function` telling this loop to step aside
  *(struck 2026-09-08: shipped as ADR-0051; correction below)*.
  _Correction (2026-09-08) — it came out as written: one `NoticeSweep` in Infrastructure rather than
  a second implementation, a timer trigger (this ADR's own declined queue is why), and the flag
  telling this loop to step aside at Information rather than Warning. What it did NOT inherit is the
  fourth configuration rule; see D6's note._
- **A second host of the API.** Two instances with the flag set both run the loop; the lease keeps
  them off each other's rows, and D3's at-least-once is the whole of what they are promised.
