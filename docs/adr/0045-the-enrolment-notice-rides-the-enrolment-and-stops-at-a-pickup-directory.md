# ADR-0045: The enrolment notice rides the enrolment, and stops at a pickup directory

**Status:** Accepted · **Date:** 2026-09-03 · Answers the half of NIST SP 800-63B-4 §4.1.2.1 that
T8 (`8f0abba`, the password at enrolment) named and left: the subscriber has to be TOLD that an
authenticator was added, over a channel the session that added it does not control. Records exactly
which clause that closes and which it does not. Supersedes nothing; strikes one paragraph of
ADR-0040 and narrows one sentence each in ADR-0013 and `docs/architecture/overview.md`.

## Context

A transfer PIN is the authenticator that unlocks every money movement. Since T8, enrolling one costs
the account password, so a stolen session cannot mint the credential on its own — the CONTROL. What
T8 did not ship, and said so in its commit body, is the DETECTION the same standard requires in the
next sentence. NIST SP 800-63B-4 §4.1.2.1, in full:

> When an authenticator is added, the CSP SHALL notify the subscriber via a mechanism independent of
> the transaction binding the new authenticator, as described in Sec. 4.6.

The repository cited this at five public sites with the volume letter and the revision number
transposed — a document that does not exist under that name — and cut the quote before "as
described in Sec. 4.6", which is where the requirements this ADR has to answer for actually live.
(The withdrawn spelling is not repeated here: a folder scan now refuses it.)

§4.6, Account Notifications: notices go to the notification addresses stored in the subscriber
account; the CSP SHALL support at least two; for accounts that underwent identity proofing at least
one SHALL have been validated during proofing; notices SHALL be sent to every non-postal address;
and the notice SHALL provide clear instructions, including contact information, in case the
recipient repudiates the event. §4.1.2.2 adds that instructions for a binding mishap MAY be given in
the authenticated session — *in addition to* the §4.6 notice, never instead of it.

**What the codebase holds.** One self-asserted email per account, never validated:
`RegisterAsync` writes `EmailConfirmed = true` beside the comment "Skip email verification for MVP".
No phone, no postal address, no second address of any kind. No endpoint changes the email. No mail
transport, queue, push or webhook anywhere in `backend/src` — the BCL's `System.Net.Mail` is present
and unreferenced. No PIN reset or revocation flow: forgetting the PIN is unrecoverable (T7 §7). The
`PinEnrolled` audit row is written in the same save as the enrolment (ADR-0044 D1) and a log line
goes to the operator, and the code's own comment said that line was deliberately NOT the notice.

**What must not close this**, from the backlog entry that carried it: the operator's log or row —
they reach the operator, not the subscriber; and any in-app notice — it reaches whoever holds the
session, which in the threat model is the attacker. What DOES close it, the backlog did not say.

**The threat model, stated once.** An attacker with a stolen session AND the account password (T8's
attacker) enrols a PIN on an account that has none. The address held on the account was written by
the legitimate owner at registration and cannot be changed through any endpoint; in this model it
is the owner's, and that is the sentence that justifies addressing a notice to an unvalidated
address at all. It is a choice, written here as one — not a consequence of §4.6, whose validation
clause binds only identity-proofed accounts, of which this system has none.

## Decision

**D1 — The fact that a notice is owed is a row, and it rides the enrolment.** A `SubscriberNotice`
(`Id`, `UserId`, `Event`, `OccurredAt`, `DeliveredAt`, `DeliveryReceipt`) is Added in
`AuthService.SetPinAsync` on the line after the `PinEnrolled` audit row and before
`UserManager.UpdateAsync`, so it rides the one owned transaction the SaveChanges funnel opens for
the audit row. Proven on SQL Server through the real endpoint in both directions: a failed audit
insert leaves no PIN, no audit row and no notice; a failed NOTICE insert leaves no PIN and no audit
row either. An enrolment whose notice cannot be recorded does not happen. The row was placed where
it is because a row placed after `UpdateAsync` is discarded with the scope — the way the audit row
itself was once lost while every mock stayed green (ADR-0044's record of it), and a unit test now
reads the change tracker at the moment `UpdateAsync` is called.

**D2 — No address in the row.** The recipient is joined from the account when the notice is
rendered. Nothing personal lives in a table that grows for the life of the account; erasure happens
by cascade with the user; there is no retention question to answer. A snapshot taken at enrolment
was considered and declined: it would not defend against an attacker who changed the address BEFORE
enrolling — only notifying the OLD address of the change does that — and no change-email endpoint
exists today. The day one does, this paragraph is the trigger to revisit.

**D3 — Rendered on demand by the operator tool, never by the API.** `notify <directory> --contact
"<text>"` is the fifth verb of `AzureBank.AuditVerifier`: it reads every row still owed, renders
each, hands it to a transport, and marks the row delivered under a concurrency token. A mode of the
tool, not a scheduled job — the anchor's decision for the anchor's reason: nothing in this
deployment runs between sessions, so a control that needs a runner names the operator, and says in
the same breath that a control depending on somebody choosing to run it does not constrain that
person. The API gains no hosted service, timer or pump; both of its existing sweeps are hygiene, and
this would have been its first correctness-bearing loop.

**D4 — The last hop is a pickup directory, and the ADR says so rather than the file.** The
transport writes one RFC 5322 message per notice — `From: no-reply@azurebank.invalid` (RFC 2606: it
resolves nowhere and impersonates nobody), `To:` the account's email, a `Message-ID` that is the
notice id, CRLF, no BOM — with `FileMode.CreateNew`, export's idiom, so a second run cannot truncate
the first run's copy. The verb refuses a directory inside a git working tree, because a spool of
addresses is one commit away from being published. Mail servers have collected from directories like
this for thirty years and a mail client opens the file as the message it is; what is missing is the
thing that moves it, and `docs/deferred/relaying-the-enrolment-notice.md` records what that would be
and why it is not here.

**D5 — What the notice says, and what it never says.** The service name and the date of the event
(SP 800-63A-4 §3.10); that a 6-digit transfer PIN was set for the first time and what it authorises;
that the account password was proved in the same request — the fact the audit row also records;
"if this was you, nothing further is needed"; repudiation instructions with the contact the operator
supplied and the notice id as the reference to quote (§4.6); that the message asks for nothing,
carries no link and cannot be replied to (FFIEC 2021 §10; ASVS 2.2.3); that it was ADDRESSED to the
email held on the account — never "sent". Never: the PIN, the password, an account number, the
holder's name, a URL, a "press here to invalidate" (no invalidation path exists), the client's IP or
agent (the enrolment row captures neither), or "sign out to end every session" — the attacker proved
the password and signs back in, and a sentence that made the reader feel safe would be the most
harmful line in the message.

**D6 — The address goes to exactly one place.** The renderer receives the row, the contact and the
clock — no address, no user, no PIN, no hash — so it cannot leak what it never sees. The transport
receives the address, for the `To:` header. The console never prints it, in a receipt, in a file
name or in a failure line: failures name the exception's TYPE, because an I/O message can echo the
path it was writing and a relay's refusal can echo the recipient (ADR-0017's rule for logs, applied
to a terminal).

**D7 — Delivery is recorded on the row, not in the chain.** `DeliveredAt` plus the receipt, fenced
by the concurrency token so two runs cannot both claim one notice. Not a chained audit row written
by the tool: the tool has no audit service, a raw insert would bypass the builder and add a
row-writing event the source-only guards cannot count, and a chained NOTIFIED that was true only of
a directory is the green-and-false shape this project treats as the worst one. When a relay exists
and "delivered" means something, this is the decision to reopen.

**D8 — What does NOT owe a notice.** A PIN CHANGE: NIST says "added", ASVS 4.0.3 2.5.5 says "changed
or replaced", and the change path costs the current PIN, writes no audit row today, and is the
repeatable surface — a notice there needs the suppression rule T7 §7 asked about, which does not
exist; a separate decision. A FAILED attempt: it would mail the real owner on every attacker probe
and open an enumeration side channel of the ADR-0013 shape; the wrong-password path already counts
toward the login lockout.

**D9 — No exit code of its own, and no chain walk.** `notify` never says 1: a verb that could report
CHAIN BROKEN while writing mail would make a tampered table read as a delivery problem, and `verify`
and `evidence` are the verbs for that question. A missing audit row behind a notice is reported as a
finding and the notice is rendered anyway — the account holder is not punished for the gap. 0 all
written, 2 nothing waiting, 3 unreadable, 4 command line, 5 interrupted, and 6 — a reuse of
`AnchorCommand.NotRecorded`, "there was work to do and it could not be recorded" — for at least one
notice still owed after the run, at the cost of one qualifying sentence in the existing copies of
the exit-code list rather than a fifth copy of it.

## What this closes, clause by clause

| Clause | Status | Why |
|---|---|---|
| §4.1 — record the date and time of authenticator life-cycle events | **Met** (before this ADR) | `AuditEvents.OccurredAt` on the `PinEnrolled` row, same transaction as the enrolment; now also proven with an injected insert failure on the enrolment path |
| §4.1.2.1 — notify via a mechanism INDEPENDENT of the binding transaction | **Met** | The row is committed with the enrolment on a path the session cannot read; it is rendered on another process and connection; the session cannot change the address it is addressed to or suppress the run |
| §4.1.2.1 — "notify the subscriber … as described in Sec. 4.6" | **Not met** | Nothing is sent. The message reaches a directory on this machine |
| §4.6 — to the notification addresses stored in the account | **Partly**: one address, self-asserted | The one address is used; there is no second |
| §4.6 — support at least two notification addresses | **Not met** | The data model holds one |
| §4.6 — at least one validated during identity proofing | **Does not bind** | No identity proofing exists; the clause is scoped to proofed accounts |
| §4.6 — sent to every non-postal address | **Not met** | See "not sent" |
| §4.6 — clear repudiation instructions with contact information | **Met mechanically, limited operationally** | The verb refuses to render without `--contact`; what the contact can do is `docs/runbooks/pin-enrolment-repudiated.md`, which ends with "no password reset exists" |
| §4.1.2.2 — in-session mishap instructions, in addition | **Not built** | Would be an adjunct; no invalidation path exists to point at, and the frontend is not touched (a promise the code cannot keep) |

**Applicability, said once.** NIST SP 800-63 binds US federal credential service providers; ASVS is
community guidance; EBA guidelines are "should"; PSD2 and the RTS on SCA are law for payment service
providers and do NOT require an enrolment notice — they are cited only for the property a report
channel has to have (free, always available, leaves the user proof). This project answers to the
NIST clause by choice, because it is the clearest statement of the property, and this table is what
"by choice" costs in honesty.

## Alternatives declined, with the strongest case for each

- **An in-process relay: the API is the runner.** The strongest counter-argument, and the one the
  security reviewer preferred: a notice can only become owed while the API is up, so a
  `BackgroundService` draining the table is never "unattended" in the sense the project's rule
  forbids, and it bounds the attacker's window to seconds instead of "until somebody runs a verb".
  Declined: it would be the first correctness-bearing loop in the API, it is scheduled rather than
  on demand, and the anchor set the precedent the other way. If that argument is ratified, the
  row/verb split still holds: the relay becomes a later change behind the same table and the same
  transport seam.
- **Send inside the request.** After `UpdateAsync` it can be lost between the commit and the call
  with no record that it was owed; inside the save it holds the audit tail lock across I/O. Either
  inverts ADR-0044 D1. The row is the fix for both.
- **Build nothing, record a negative finding.** Honest about §4.6, and cheapest — but the subscriber
  is told nothing, and its premise (that an unvalidated address is "nowhere true to send to") reads
  a clause scoped to proofed accounts as if it bound this one. The row costs one `Add`.
- **A snapshot of the address in the row** (D2), **a chained NOTIFIED audit row** (D7), **a new
  `SecurityEvents` constant for "notice not delivered"** (it would move every event-count guard for
  a state that is not a security event), **a committed placeholder contact** (a notice a reader
  opens as if it were real), **an SMTP option surface with nothing behind it** (validators for an
  environment that does not exist). Each declined for the reason in its parenthesis.

## Where it stops

The independence is real; the delivery is not. A file this machine wrote to this machine's disk has
been seen by nobody, and the notice waits until the operator runs `notify`, which may be never — so
in the threat model the attacker's window is the whole interval. Delivery is at-least-once: the file
is written, then the row is marked, and an interruption between the two leaves a file whose row is
still owed; the next run into the same directory is refused by `CreateNew`, and into a different one
writes a second copy with the same `Message-ID`. The directory is personal data at rest, unencrypted
and unpurged — the verb's refusal of a repository path and the runbook's delete-after sentence are
the mitigations. And the remedy behind the contact is incomplete: an operator can null the PIN by
hand, but no password reset exists, so the attacker who proved the password still holds it.

## What is wired

- `SubscriberNotice` (entity, configuration with the delivery CHECK, filtered index on the owed
  rows, cascade to the user), the `SubscriberNotices` set, the `AddSubscriberNotices` migration.
- The `Add` in `AuthService.SetPinAsync`, and the corrected comments beside it and in
  `SecurityEvents.PinEnrolled`.
- `NotifyCommand`, `NoticeRenderer`, `INoticeTransport` and `PickupDirectoryTransport` in the
  operator tool; the verb wired in `Program.cs`; the transport registered in the tool's root.
- Tests: `SubscriberNoticePersistenceTests` (the table, through the real host);
  `SubscriberNoticeSqlServerTests` (both D1 directions, the token, the CHECK);
  `AuthServiceTests.SetPinAsync_WhenEnrolling_TracksTheNoticeBeforeUpdateAsync` (the order);
  `NotifyCommandTests` (rendering, the address's one place, every non-delivery answer, the real
  file); `RealCompositionRootRefusalTests` asks all five verbs; `SubscriberNoticeLimitTests` pins
  the limit — nothing on the API side can reference a mail library (watched refusing a real
  `SmtpClient` before it shipped) and the withdrawn citation does not return.
- The runbook's verb list and **The enrolment notice** section; `pin-enrolment-repudiated.md`;
  `docs/deferred/relaying-the-enrolment-notice.md`; `docs/notices/` with a sample produced by the
  real verb; two entries in `docs/engineering-traps.md`; row 9 of the gap table in
  `docs/audit-trail-against-real-practice.md`.
- Untouched, on purpose: the frontend, the BFF, the OpenAPI document, every recipe for the six
  secrets — no option and no secret was added.

## Measured on the running API

2026-09-03, the API started in Development against a throwaway LocalDB database (`AzureBankT13`)
migrated to `AddSubscriberNotices`, with `Audit:ChainKey` and `Audit:AnchorKey` supplied through the
environment for the run — this machine's user-secrets predate the anchor key, which is the recipe
drift the README, the practices page and the `.example` file already carry and this ADR does not
fix. An empty database holds no roles and the seeder had not run, so the one role registration
needs was inserted by hand first; and the user is a fresh registration, because every seeded user
already has a PIN.

```
POST /api/auth/register                                   -> 201
POST /api/auth/pin  {"pin":"424242","password":"…"}       -> 200  {"message":"PIN set successfully"}

SELECT Id, UserId, Event, OccurredAt, DeliveredAt, DeliveryReceipt FROM SubscriberNotices
01A064A9-8060-78EA-97AB-2ABA54D4CC37  01A064A9-7D10-7E53-BC89-C604CD5DB58F  PinEnrolled
  2026-09-03 00:27:05  NULL  NULL

notify C:\…\azurebank-notices-t13 --contact "security@your-bank.example, +00 000 0000"
NOTIFIED 1 of 1 waiting notices into C:\Users\Drako\AppData\Local\Temp\azurebank-notices-t13
  Each file is a complete message addressed to the email held on the account, and it has
  reached this machine's disk and nobody else: nothing here sends. Point a relay at the
  directory or move the files yourself, and delete the spool afterwards.
  01a064a9806078ea97ab2aba54d4cc37.eml <- notice 01a064a9806078ea97ab2aba54d4cc37, PinEnrolled at 2026-09-03 00:27:05Z
exit 0

notify (the same command again)
NOTHING TO NOTIFY: no notice is owed.
  Every recorded notice has been rendered, or none was ever recorded. Not a success
  and not a failure: nothing was waiting.
exit 2

SELECT DeliveredAt, DeliveryReceipt FROM SubscriberNotices
2026-09-03 00:27:09  01a064a9806078ea97ab2aba54d4cc37.eml

01a064a9806078ea97ab2aba54d4cc37.eml: 1181 bytes; first three bytes 46 72 6f ("Fro", no BOM);
29 of 29 lines end in CRLF
```

The file is `docs/notices/pin-enrolled.sample.eml`, byte for byte. Then every statement in
`docs/runbooks/pin-enrolment-repudiated.md`, run once against the same store with that reference:
the notice row and its one `PinEnrolled` audit row (sequence 1, `{"passwordProved":true}`) came
back; the PIN update touched one row and left `PinHash` NULL; the token update revoked one refresh
token.

## Consequences

A PIN enrolment now writes three rows in one transaction instead of two, and a database that has not
applied the migration refuses every enrolment — the PIN is not set, which is D1 protecting the
record rather than a fault. The operator tool reads personal data for the first time
(`Users.Email`), so the authorisation to run it now also gates addresses. The transport seam is one
interface with one implementation, which is what a relay would replace. And the repository has a
sentence it did not have: the subscriber is not reached, and here is exactly why.

## What would change this

- **A relay** — the deferred document's trigger. The seam is `INoticeTransport`; the row, the verb
  and the notice text do not move.
- **A change-email endpoint** — D2's trigger: the old address must then be notified of the change,
  and the join-at-delivery decision is reopened.
- **A PIN reset or revocation flow** — completes the repudiation path the contact currently cannot.
- **A second notification address** — the first §4.6 clause this system could meet by data model
  alone.
- **A PIN-change notice** — D8, with the suppression rule it needs.
