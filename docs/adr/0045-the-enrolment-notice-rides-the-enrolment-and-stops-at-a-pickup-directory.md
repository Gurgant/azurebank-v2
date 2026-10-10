# ADR-0045: The enrolment notice rides the enrolment, and stops at a pickup directory

**Status:** Accepted · **Date:** 2026-09-03 · **Amended:** 2026-09-04 (D3 by ADR-0048, D8 by
ADR-0047) · **Amends:** ADR-0013, ADR-0040

## Context

A transfer PIN authorises transfers and withdrawals, and enrolling one costs the account password,
so a stolen session alone cannot enrol one. NIST SP 800-63B-4 also asks for detection: "When an
authenticator is added, the CSP SHALL notify the subscriber via a mechanism independent of the
transaction binding the new authenticator, as described in Sec. 4.6." §4.6 asks for at least two
notification addresses and for instructions, with a contact, in case the recipient repudiates it.
The account holds one self-asserted, unvalidated email that no endpoint changes, and nothing in the
system can send mail or reset a PIN. Neither the audit row nor an in-app notice is that notice: the
first reaches the operator, the second the session, which the attacker holds with the password.

## Decision

- **D1 — The fact that a notice is owed is a row, and it rides the enrolment.**
  `AuthService.SetPinAsync` adds a `SubscriberNotice` after the `PinEnrolled` audit row and before
  `UserManager.UpdateAsync`, so it rides the audit row's transaction (ADR-0044 D1): a row added
  after `UpdateAsync` is discarded with the scope. Either insert failing leaves no PIN, no audit
  row, no notice (`WhenTheNoticeCannotBeWritten_TheEnrolmentIsRefused_AndNoAuditRowClaimsIt`,
  `SetPinAsync_WhenEnrolling_TracksTheNoticeBeforeUpdateAsync`).
- **D2 — No address in the row.** The recipient is joined from the account at rendering, so nothing
  personal lives in a table that grows with the account, and erasure cascades with the user.
- **D3 — Rendered on demand by the operator tool.** `notify <directory> --contact "<text>"`, the
  fifth verb of `AzureBank.AuditVerifier`, renders every row still owed, hands it to a transport and
  marks it delivered. "Never by the API" is withdrawn, see ADR-0048: a hosted runner delivers the
  same rows under a lease, and the verb stays for a store with no runner or a runner that is down.
- **D4 — The last hop is a pickup directory.** The transport writes one RFC 5322 message per notice,
  with `FileMode.CreateNew` so that a second run cannot truncate the first run's copy: `From:` is
  the mailbox `no-reply` on `azurebank.invalid` (RFC 2606: it resolves nowhere and impersonates
  nobody), `To:` the account's email, `Message-ID` the notice id, CRLF, no BOM. The verb refuses a
  directory inside a git working tree, because a spool of addresses is one commit away from being
  published (`ADirectoryInsideAGitRepository_IsRefusedBeforeTheStoreIsTouched`).
- **D5 — What the notice says, and what it never says.** It gives the service name and the event's
  date (SP 800-63A-4 §3.10), what was set and what it authorises, and how to repudiate it: the
  contact the operator supplied and the notice id (§4.6). It asks for nothing, carries no link and
  takes no reply (FFIEC 2021 §10; ASVS 2.2.3), and says "addressed to", never "sent". It never
  carries the PIN, the password, an account number, a name, or advice to sign out, because the
  attacker proved the password and signs back in (`TheNoticeCarriesNoSecretNoLinkAndNotTheAddress`).
- **D6 — The address goes to exactly one place.** Only the transport receives the address, for the
  `To:` header: the renderer gets the row, the contact and the clock, so it cannot leak what it
  never sees, and the console names a failure by exception type, because a message can echo a path
  or a recipient (`AWriteFailure_LeavesTheNoticeOwed_AndNamesTheExceptionTypeNotTheAddress`).
- **D7 — Delivery is recorded on the row, not in the chain.** `DeliveredAt` is a concurrency token,
  so two runs cannot both mark a notice delivered (`TwoRunsCannotBothMarkOneNoticeDelivered`). No
  chained row is written, because a chained NOTIFIED would be true only of a directory: green, and
  false.
- **D8 — What does not owe a notice.** A failed attempt, because it would mail the account's owner
  on every attacker probe and open an enumeration side channel (ADR-0013); the wrong-password path
  already counts toward the login lockout. A PIN change: withdrawn, see ADR-0047.
- **D9 — No exit code of its own, and no chain walk.** `notify` reuses exit 6 for a notice still
  owed and never exits 1, because a verb that could report CHAIN BROKEN while writing mail would
  make a tampered table read as a delivery problem. A notice whose audit row is missing is a finding
  and is rendered anyway (`ANoticeWithoutItsAuditRow_IsStillDelivered_AndTheFindingIsPrinted`).

What this closes, clause by clause. NIST SP 800-63 binds US federal providers: here it is a choice.

| Clause | Status | Why |
|---|---|---|
| §4.1 — record the date and time of authenticator life-cycle events | **Met** | `AuditEvents.OccurredAt` on the `PinEnrolled` row, same transaction as the enrolment |
| §4.1.2.1 — "notify the subscriber … as described in Sec. 4.6" | **Not met** | Nothing is sent. The message reaches a directory on this machine |
| the same clause — "via a mechanism independent of the transaction" | **Not met**: nothing is sent | What is built is the independence: the row commits with the enrolment on a path the session cannot read and is rendered outside the request, and the session cannot change the address or suppress the run |
| §4.6 — to the notification addresses stored in the account | **Partly**: one address, self-asserted | The one address is used; there is no second |
| §4.6 — at least two notification addresses, and sent to every non-postal one | **Not met** | The data model holds one, and nothing is sent |
| §4.6 — at least one validated during identity proofing | **Does not bind** | No identity proofing exists; the clause is scoped to proofed accounts |
| §4.6 — clear repudiation instructions with contact information | **Met mechanically, limited operationally** | The verb refuses to render without `--contact`; what the contact can do is `docs/runbooks/pin-enrolment-repudiated.md`, which ends with "no password reset exists" |
| §4.1.2.2 — in-session mishap instructions, in addition | **Not built** | Would be an adjunct; no invalidation path exists to point at, and the frontend is not touched (a promise the code cannot keep) |

## Rejected

- Rejected: sending in the request, because it can be lost after the save or holds the audit lock.
- Rejected: building nothing but a finding, because the account holder is then told nothing.
- Rejected: an address snapshot in the row, because an address changed before enrolling defeats it.
- Rejected: a `SecurityEvents` name for an undelivered notice, because it is not a security event.
- Rejected: a committed placeholder contact, because a reader opens that notice as if it were real.
- Rejected: SMTP options with nothing behind them, because they validate an environment not there.

## Consequences

- An enrolment writes three rows in one transaction, and a database without the
  `AddSubscriberNotices` migration refuses every enrolment.
- The operator tool reads `Users.Email`, so the authorisation to run it also gates addresses.
- Not covered, and this is where it stops: nothing is sent. The independence is real and the
  delivery is not: a file this machine wrote to its own disk has been seen by nobody
  (`docs/deferred/relaying-the-enrolment-notice.md`).
- Not covered: exactly-once delivery. The file is staged, moved into place and then the row is
  marked, so an interruption leaves a complete file whose row is still owed; the row is the truth.
- Not covered: the spool is personal data at rest, unencrypted and unpurged. CodeQL's
  `cs/cleartext-storage-of-sensitive-information` is dismissed: the address is the `To:` header.
- Not covered: the remedy. By `docs/runbooks/pin-enrolment-repudiated.md` an operator nulls the PIN,
  but no password reset exists, so an attacker who proved the password still holds it.
- Not covered: the address is unvalidated. It is taken as the account holder's, by choice, because
  it was written at registration and no endpoint changes it.

## Revisit when

- A sending transport behind `INoticeTransport`: D7 reopens, since "delivered" then means something.
- An endpoint changes the email: the old address must be told, and D2 is reopened.
- A PIN reset or a second address exists: the repudiation path or one more §4.6 clause can be met.

## Verified by

- `SubscriberNoticeSqlServerTests`, `SubscriberNoticePersistenceTests`, `NotifyCommandTests`.
- `NothingOnTheApiSideCanSendMail_AndThisPinsTheLimit`, `RealCompositionRootRefusalTests`.
- `TheWithdrawnCitation_DoesNotReturn` (how NIST is cited); `docs/notices/pin-enrolled.sample.eml`.

## Related

ADR-0013, ADR-0017, ADR-0040, ADR-0044, ADR-0047, ADR-0048, ADR-0051, ADR-0052.
