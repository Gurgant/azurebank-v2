# ADR-0048: The API is the runner that delivers owed notices

**Status:** Accepted · **Date:** 2026-09-04 · **Amended:** 2026-09-08 (ADR-0051), 2026-09-09 (D1,
D5, D6), 2026-09-10 (ADR-0052) · **Amends:** ADR-0045 (D3, reversed)

## Context

ADR-0045 records the obligation and stops at a verb: the API writes an owed-notice row in the
same save as the enrolment, and an operator renders it into a pickup directory with the tool's
`notify` verb. A notice therefore stays owed for as long as nobody runs the verb, while the threat
it exists for, a stolen session enrolling a PIN behind the password or changing one behind the
PIN alone (ADR-0047), is measured in minutes. ADR-0045 D3 declined a hosted loop; this decision
reverses it. The last hop stays a pickup directory: nothing here sends.

## Decision

- **D1 — One hosted loop in the API, `NoticeRelayService`.** It has the shape of the two hygiene
  sweeps: a `PeriodicTimer`, the first look one full period after start, a catch-all per sweep
  logged at Error, cancellation absorbed as shutdown. It is always registered, so a second host
  inherits it, and reads `Notices:Runner` once at start: unless the flag names this process it
  logs the flag's value and that this process delivers nothing (Information, ADR-0051 D4) and
  returns. The flag, not the lease, keeps two kinds of runner from both sending; two hosts of the
  API with the flag set both run the loop, the lease keeps them off each other's rows, and D3's
  at-least-once is all they are promised. The sweep is `NoticeSweep` in Infrastructure, shared
  with the Function (ADR-0051 D1).
- **D2 — The claim is a lease on the row, taken in one statement, and shared.** Two nullable
  columns, `LeasedUntil` and `LeasedBy`, are paired by `CK_SubscriberNotices_Lease`. A claim stamps
  up to `Notices:BatchSize` of the oldest owed rows whose lease is null or lapsed with the
  runner's name and a lease end in one set-based UPDATE, because the database then serialises two
  claims and the second finds nothing free among them. The batch caps a runner's live work, rows
  it still holds from a failed delivery included, so a runner that keeps failing never holds more
  than one lease can deliver. Held rows are renewed to the new sweep's lease end before anything
  is delivered. The runner re-reads what it holds by name, never by the instant it wrote, because
  a store that rounded a timestamp could make it claim N and read back none. `DeliveredAt` stays
  the concurrency token and the mark clears the lease. A sweep that outlives its own lease stops
  delivering and says so. The clocks compared are the runners' own, so a lease must exceed the
  period plus the skew a deployment tolerates. The protocol is `NoticeClaim` in Infrastructure.
- **D3 — At-least-once, and the lease does not make it once.** A runner that hands a message to
  the transport and dies before marking is succeeded when its lease lapses. With a sending
  transport that row goes out twice: exactly-once needs an idempotency key the transport or the
  provider honours, or de-duplication at the recipient, and until one exists a duplicate is an
  accepted outcome. With the pickup directory the file is such a key: the second attempt is
  refused by the exclusive create (ADR-0045 D4), nothing goes out twice, and the row stays owed
  beside the file it produced (held, retried and refused every lease, logged at Warning, exit 6
  from the verb) until an operator applies the runbook: the row is the truth, so move the file and
  run again, or mark the row.
- **D4 — One unit of work, shared.** The per-row steps (address, evidence, render, transport,
  mark) are `NoticeDeliveryRun` in Infrastructure, and the verb and the relay both call it, so the
  runners cannot drift on what "delivered" costs. Neither runner ever sees the address: the run
  returns outcomes, and a transport failure is reported by exception type only, because an I/O
  message can echo the path and a relay's refusal can echo the recipient.
- **D5 — The verb claims too, and steps aside for what another runner holds.** `notify` takes the
  same lease under its own name (`verb/{host}/{pid}/{8 hex}`, two minutes) and delivers only what
  it holds, because a verb that merely read free rows would leave a window between its read and
  its write in which the relay claims the same rows. Rows another runner holds under a live lease
  are counted and named, and taken only once that lease has lapsed. Exit 2 has two readings and
  the line says which: nothing owed, or everything owed is held under another runner's live
  lease, which proves the lease is unexpired and nothing about the process.
- **D6 — Options, off by default, refused when partial.** The `Notices` section: `Runner`
  (`None|Api|Function`, default `None`), `PickupDirectory`, `Contact`, `PeriodSeconds` (15),
  `LeaseSeconds` (120), `BatchSize` (100), and `Schedule`, the Function's cadence, which the API
  never reads (ADR-0051). `None` is the default because a pickup directory is a spool of addresses
  at rest and must sit outside any git tree, so no default path can ship. When `Runner` is `Api`
  the directory must exist and pass the verb's git-tree guard, the contact is mandatory content of
  every notice (NIST SP 800-63B-4 §4.6), and the lease must be at least twice the period, so that
  a sweep and the next claim do not overlap in the normal case: a bound on overlap, not on
  duplicates (D3). The ranges on the period, the lease and the batch apply whatever the runner. A
  partial set stops the host with a message that names the key and the fix. Nothing in the section
  is a secret. Each host asks them about itself, and only the API asks the lease rule (ADR-0051 D3).
- **D7 — Logging, and no `SecurityEvent`.** Information per delivered notice (reference, kind,
  receipt); Warning for an unusable address, an unrenderable kind, a transport failure and a
  missing audit row; Error for a sweep that failed as a whole; the address in no line (D4,
  ADR-0017). No new `SecurityEvents` constant, because an owed notice is not a security event, a
  delivered one is a receipt, and a constant would move every event-count guard for a state that
  is neither; and no audit row: delivery is recorded on the row (ADR-0045 D7).

## Alternatives declined

- Rejected: a second deployable first, because the protocol it shares was not yet proved on SQL
  Server; ADR-0051 builds it afterwards.
- Rejected: a queue instead of a lease, because a queue would carry a copy of the obligation, and
  the row is the obligation, written in the enrolment's own save (ADR-0045 D1). A lease on the row
  keeps that property; a queue would have to be reconciled with it.
- Rejected: claiming row by row under the concurrency token, because N round trips are N chances
  to interleave with another runner, and one set-based statement is one.
- Rejected: deleting the verb, because it serves a store where nothing runs, or a runner is down.
- Rejected: an idempotent pickup transport that treats a file already at the notice's path as the
  delivery, because it reverses ADR-0045 D4 (never overwrite; the row is the truth).

## Consequences

- A notice is delivered without a person: with an empty backlog the file appears within one
  period. The period bounds how often the relay looks, not when a notice lands: a backlog, a slow
  transport, a lapsed lease or a row refused and retried each push the next file later.
- A backlog is drained a batch at a time, so a second runner finds free rows beside the first; the
  verb claims batches in a loop until nothing is free or its own lease lapses.
- The loop announces itself once, with the name a reader matches against `LeasedBy`:
  `Notice relay: live as api/GURGANT/22280/2be449cb, every 5s, lease 120s, batch 100, into …`.
- The relay inherits the `NO AUDIT ROW` limit as it stands, at Warning: since ADR-0052 the check is
  exact for a notice that names its audit row, and the pair it compares, `(ActorUserId, Event)`,
  cannot see a re-point within one kind.
- Not covered: the last hop is a file in a pickup directory, seen by nobody. A provider credential,
  addresses the project may write to, a second contact and a remedy stand before "delivered".

## What would change this

- **A sending transport**: a second `INoticeTransport` behind a provider credential, one more
  secret; D3's duplicate becomes a mail seen twice, and an idempotency key stops being optional.
- **The Azure Function**: the same claim protocol in a Function, developed against Azurite, and
  `Notices:Runner=Function` telling this loop to step aside. Built: ADR-0051.

## Verified by

- `NoticeRelaySqlServerTests`: the claim, the lease and the refused second file on SQL Server.
- `NoticeRelayServiceTests` (sweep, loop, log lines), `NoticeRelayStartupTests` (D6 at the root).
- `NotifyCommandTests` (D5) and `SubscriberNoticeLimitTests` (no mail library in any runner).

## Related

ADR-0017, ADR-0045, ADR-0047, ADR-0051, ADR-0052.
