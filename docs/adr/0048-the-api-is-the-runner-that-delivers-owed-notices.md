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
`d79beb1`, a throwaway store):

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
`Notices:Runner` once at start; unless that names this process it logs that no runner is live and
returns. That flag, not the lease, is what keeps two runners from both sending.

**D2 — The claim is a lease on the row, taken in one statement.** Two nullable columns,
`LeasedUntil` and `LeasedBy`, paired by `CK_SubscriberNotices_Lease` the way the delivery pair
already is. A sweep stamps every owed row whose lease is null or expired with its name and a lease
end in ONE set-based UPDATE, so the database serialises two runners and the second finds nothing
free; it then re-reads the rows it stamped and delivers them. Proved on SQL Server: two runners
sweeping six fresh rows at the same moment delivered six files, each row marked once, none owed; a
row under a live lease held by a third name is left alone; a row whose lease has lapsed is taken.
`DeliveredAt` stays the concurrency token, and the mark clears the lease.

**D3 — At-least-once, and the lease does not make it once.** A runner that hands a message to the
transport and dies before marking is succeeded when its lease lapses, and that row goes out again.
Exactly-once is not the runner's to give: it needs an idempotency key the transport or the provider
honours, or de-duplication at the recipient. Until one exists a duplicate is an accepted outcome,
pinned by a test that fails a delivery, sees the row still leased, lapses the lease by hand and
watches the same row go out. The verb already named this shape for its own duplicates; the relay
inherits the honesty.

**D4 — One unit of work, shared.** The verb's per-row steps — address, evidence, render, transport,
mark — are `NoticeDeliveryRun` in Infrastructure, and the verb and the relay both call it. The verb
keeps every word it printed before; the relay logs. Neither ever sees the address: the run returns
outcomes, and a transport failure is reported by exception TYPE only, because an I/O message can
echo the path and a relay's refusal can echo the recipient. A test reads every log line of a sweep
that delivered one notice and refused another for an injected `Bcc:` in its address, and finds
neither address in any of them.

**D5 — The verb steps aside for a live lease, and says so.** `notify` no longer renders a row a
runner holds; it counts them and prints that they are leased by a live runner, and takes them only
once the lease has lapsed. An operator running the verb beside a live relay therefore does not
produce the duplicate the lease exists to prevent.

**D6 — Options, off by default, refused when partial.** The `Notices` section: `Runner`
(`None|Api|Function`, default `None`), `PickupDirectory`, `Contact`, `PeriodSeconds` (15),
`LeaseSeconds` (120). `None` is the default on purpose: a pickup directory is a spool of addresses
at rest and must sit outside any git tree, so no default path can ship. When `Runner` is `Api` the
directory must exist and pass the verb's own git-tree guard (now shared from Infrastructure), the
contact is mandatory content of every notice (NIST SP 800-63B-4 §4.6), and the lease must exceed the
period, or a slow sweep is overtaken and its rows go out twice. A partial set stops the host, with a
message that names the key and the fix, which is the only thing an operator sees. Nothing in the
section is a secret and none joins the six.

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

**A queue instead of a lease.** A queue would carry a copy of the obligation, and the row is the
obligation: written in the enrolment's own save so it is never lost and never survives a rollback
(ADR-0045 D1). A lease on the row keeps that property; a queue would have to be reconciled with it.

**Claiming row by row under the concurrency token.** Possible, and it is what the verb's mark does;
but a sweep that claimed N rows in N round trips would spend N chances to interleave with another
runner. One set-based statement is one chance, and the database takes it.

**Deleting the verb.** It is the operator's tool for a store where nothing runs, and for the day a
runner is down; it now coexists with the runner (D5) rather than competing with it.

## Consequences

Measured after, on the same stack with `Notices__Runner=Api`, a 5-second period and a 60-second
lease, then again with `Notices__Runner=None` as the control:

```
19:05:59Z  enrol                -> 200; the row is owed, no lease
19:06:03Z  Date: in the file    -> delivered on the first sweep, 4 s after the enrolment
19:07:10Z  enrol (second user)  -> 200
19:07:15Z  the row              -> delivered, file present, lease cleared: 5 s, one period
           API log              -> "live as GURGANT/32384/bcc2330c, every 5s, lease 60s, into …"
                                   "delivered notice 01a06dd1… (PinEnrolled) as 01a06dd1….eml"
           the address in the API log: 0 occurrences
           notify beside the live relay -> exit 2, "no notice is owed": it had taken them all
19:08:02Z  enrol, Runner=None   -> 200; the row is still owed 16 s later, no lease
           API log              -> "runner is None; this process delivers nothing (Notices:Runner)"
```

An owed notice reaches the pickup directory within one period of being recorded, for as long as the
API runs with the flag set; the verb remains for every other case. The last hop is unchanged: a file
on this machine, seen by nobody, which is what the deferral record's preconditions — a provider
credential, addresses the project may write to, a second contact and a remedy behind it — still
stand between and "delivered".

ADR-0045 D3 is reversed and struck in place. D7 — delivery recorded on the row, not in the chain —
stands: it was the decision to reopen when "delivered" means something, and with a pickup directory
it still does not; the lease joins the mark on the row rather than moving either into the chain.

The API-side mail limit (`SubscriberNoticeLimitTests`) holds unchanged: the relay uses the pickup
transport, which uses no mail library. Its because-string now names the relay beside the verb.

The `NO AUDIT ROW` finding's limit (ADR-0047) is inherited by the relay unchanged, and logged at
Warning rather than printed.

## What would change this

- **A sending transport.** A second `INoticeTransport` behind a provider credential — the seventh
  secret — is the change; the runner, the claim and the options are ready for it, and D3's duplicate
  becomes a mail the recipient sees twice, which is when an idempotency key stops being optional.
- **The Azure Function.** The backlog's next relay item: the same claim protocol in a Function,
  developed against Azurite, and `Notices:Runner=Function` telling this loop to step aside.
- **A second host of the API.** Two instances with the flag set both run the loop; the lease keeps
  them off each other's rows, and D3's at-least-once is the whole of what they are promised.
