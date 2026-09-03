# Architecture Decision Records

This directory contains Architecture Decision Records (ADRs) for the AzureBank project.

## What is an ADR?

An ADR is a document that captures an important architectural decision made along with its context and consequences.

## When something earns an ADR

An ADR requires a decision that is **binding on future work**, **not already recorded** in another
ADR, and **not recoverable by reading** the code, the types, the tests or the CI gates. If a
competent reader could recover the rule by opening the repository, writing it down here creates a
second copy that will eventually go stale and start lying — which is worse than never having
written it.

Preferences, task lists, status reports and deferrals are not ADRs. The best candidates are the
ones with nothing to read: a **rejection** (no code exists for the thing you decided not to build),
a constraint on something **outside the repository**, or a hard-won **negative finding** whose only
alternative record is running the same experiment again.

## If you read four, read these

Forty-four decisions is more than anyone reads cold. These four carry the architecture; the rest is
detail hanging off them.

| | Why this one |
|---|---|
| **[ADR-0001](0001-bff-pattern.md) — BFF pattern** | The decision everything else inherits: the browser never holds a token, so the whole auth story follows from here. |
| **[ADR-0009](0009-idempotency-monetary-operations.md) — Idempotent monetary operations** | Where this stops being a CRUD app. A keyed HMAC over raw request bytes, a five-state protocol, and a deliberate correctness-over-availability trade. |
| **[ADR-0019](0019-spa-bff-integration.md) — SPA/BFF integration** | Cookie auth and one error channel: the contract the entire frontend is written against. |
| **[ADR-0022](0022-client-money-mutation-protocol.md) — Client money-mutation protocol** | The client half of ADR-0009 — which outcomes keep an idempotency key and which spend it. Every cell in that table is a double-spend if it is wrong. |

## By theme

**Platform and topology** — [0001](0001-bff-pattern.md) BFF pattern ·
[0002](0002-yarp-proxy.md) YARP reverse proxy ·
[0018](0018-bff-origin-hardening.md) BFF origin hardening ·
[0019](0019-spa-bff-integration.md) SPA/BFF integration ·
[0039](0039-bff-session-cache-is-a-fallback.md) the BFF session cache is a fallback, never the answer

**Money** — [0009](0009-idempotency-monetary-operations.md) idempotent monetary operations (server) ·
[0022](0022-client-money-mutation-protocol.md) client money-mutation protocol ·
[0024](0024-no-client-facing-optimistic-concurrency.md) no client-facing optimistic concurrency ·
[0028](0028-data-router-for-blocking-browser-back.md) a data router, bought for one hook ·
[0035](0035-transaction-number-check-symbol.md) a check symbol on the transaction number ·
[0036](0036-account-number-collision-recovery.md) recovering from an account-number collision

**Interface** — [0027](0027-dark-mode-through-css-custom-properties.md) dark mode through CSS custom properties ·
[0033](0033-root-error-boundary.md) a root error boundary, so a render error is not a blank page

**Authentication and account safety** — [0003](0003-argon2id-password-hashing.md) Argon2id password hashing ·
[0008](0008-step-up-authentication.md) step-up authentication ·
[0010](0010-pin-attempt-limiting.md) PIN attempt-limiting ·
[0011](0011-pin-hash-pepper.md) PIN-hash pepper ·
[0012](0012-login-attempt-limiting.md) login attempt-limiting ·
[0021](0021-refresh-token-rotation-bff-remint.md) refresh-token rotation with reuse detection ·
[0026](0026-absolute-session-cap-reauthentication.md) the absolute session cap is re-authenticated, never extended ·
[0034](0034-failed-family-revoke-recovery.md) recovery for a family revoke that fails ·
[0037](0037-atomic-registration.md) registration is all-or-nothing ·
[0038](0038-bff-session-is-the-only-credential.md) the session is the only credential the BFF accepts ·
[0040](0040-changing-a-credential-requires-the-current-one.md) changing a credential requires proving the current one ·
[0041](0041-the-api-verifies-the-transfer-pin.md) the API verifies the transfer PIN, not the BFF

**Not leaking who exists** — [0013](0013-registration-user-enumeration.md) registration enumeration ·
[0014](0014-recipient-lookup-enumeration.md) recipient lookup, exact-match and harvest-resistant ·
[0015](0015-decouple-username-renameable-handle.md) decoupling the username from a renameable handle ·
[0020](0020-account-number-reveal.md) on-demand account-number reveal

**Contract and correctness** — [0007](0007-fluentvalidation.md) FluentValidation ·
[0023](0023-runtime-response-validation.md) runtime response validation ·
[0005](0005-scalar-api-documentation.md) Scalar API documentation ·
[0029](0029-contract-conformance-gate.md) one suite, two backends ·
[0030](0030-real-backend-integration-layer.md) the app's data layer against the real backend ·
[0031](0031-e2e-playwright.md) the app in a real browser ·
[0032](0032-real-stack-layers-in-ci.md) the real-stack layers in CI ·
[0043](0043-the-document-declares-the-error-body.md) the document declares the error body ·
[0044](0044-the-audit-trail-is-append-only-and-chained.md) the audit trail is append-only and chained

**Operations** — [0016](0016-observability-three-pillars.md) observability, three pillars ·
[0017](0017-pii-redaction-codeql-barrier.md) PII-safe telemetry and the log-forging barrier

**Build and tooling** — [0004](0004-central-package-management.md) central package management ·
[0006](0006-mapperly-object-mapping.md) Mapperly object mapping ·
[0025](0025-originals-reference-mine.md) the originals are a reference mine

All forty-four **decisions** are Accepted and shipped, which is why there is no Proposed tier — but
"shipped" is a claim about the decision, not about every line of illustration around it. **Four of
ADR-0008's five C# blocks diverge from the source** — three of them name a type that was never
built at all (`enum AuthLevel`, and `RequireAuthLevelAttribute` in two blocks), the fourth shows an
annotation the entity does not carry. Found and annotated 2026-08-12. It is the one place in this
directory where the code samples describe a design that was not adopted, and it is worth knowing
before copying a sketch out of an ADR.

Where a later record changes an earlier one, the earlier keeps an inline
supersession note at the affected clause rather than being rewritten: ADR-0019's Decision 6 points
at ADR-0023, ADR-0009 points at ADR-0022 for its client half, and ADR-0021's amendment points at
ADR-0034 for the residual it left open, ADR-0036 points at ADR-0037 for the one it left open, and
ADR-0020's dual-mode caveat points at ADR-0038 for the half of it that turned out not to be
accepted so much as unnoticed, and ADR-0015's residual points at ADR-0039 both for its closure and
for the two reasons it had given for leaving it open, which were wrong; ADR-0040 closes a hole that
left both ADR-0008's step-up gate and ADR-0010's attempt-limiting inoperative; and ADR-0041 moves
transfers off that gate entirely, which makes ADR-0022's step-up-replay verification step true of
no live caller — recorded there rather than quietly left to rot. ADR-0008's own
corrections follow the same rule — eight inline notes, nothing deleted — and one of them records an
open hole rather than a supersession: **account deletion is listed as level 2 and is gated nowhere
at that level** (the endpoint is still `[Authorize]`; what is missing is the step-up).
ADR-0043 corrects the generation pipeline that both ADR-0029 and ADR-0023 consume, and is filed
as a correction rather than a new policy: the decisions were already made, the document simply
did not say them. ADR-0045 strikes the residual ADR-0040 left open (closed by T8) and narrows the
"no email infrastructure" sentence in ADR-0013 to "no relay", without changing its conclusion.

HOW A CORRECTION IS RECORDED, written down because it had never been stated and a review round went
on it. **The note goes immediately against what it corrects.** That is the load-bearing half: a note
in a section further down is one that the reader of the wrong sentence never reaches, and the reader
of the note has to go hunting for what it refers to. **And nothing is deleted — a superseded clause
is struck in place with `~~…~~` and the note follows it inline**, which is what ADR-0044 already
does at its #231 revisit and ADR-0007 at its envelope note. Struck rather than merely annotated,
because a wrong sentence left looking current is read as current: that is not a hypothetical here,
it is why `docs/runbooks/audit-chain-unavailable.md` went on repeating a claim ADR-0044 had already
withdrawn.

THE ONE EXCEPTION IS TEXT AN OPERATOR READS UNDER PRESSURE — runbooks, printed verdicts, error
strings. There the wrong wording is removed rather than struck, because nobody scrolls past a struck
line during an incident and a `~~` renders as noise in a terminal. Everything else that describes
the system AS IT IS rather than as it was decided — `docs/deferred/`, code comments, XML docs — is
simply corrected in place, with a line saying what it used to claim.

NOTE WHAT THIS RULE DOES NOT ASK ANYBODY TO DECIDE. An earlier draft of it split corrections by kind
— a decision that was right when made versus a statement of fact that was never true — and that is a
real distinction, but it puts a judgement in the middle of a rule, and a rule with a judgement in it
is one that gets re-argued every time somebody new reads the file. This directory has already learnt
that about MD040, four times across #94, #96, #127 and #129. Adjacency needs no classification. The
two corrections ADR-0044 made before this rule existed (`Corrected 2026-08-25`, `CORRECTED
2026-08-20`) are already adjacent and quote the superseded wording in full, so nothing is lost by
leaving them as they are; from here the wording is struck in place instead, which costs less and
reads better.

The next free number is **0046**.

<details>
<summary>Full list in numeric order</summary>

| ID | Title | Status | Date |
|----|-------|--------|------|
| [ADR-0000](0000-template.md) | ADR Template | Template | - |
| [ADR-0001](0001-bff-pattern.md) | BFF Pattern | Accepted | 2026-01-12 |
| [ADR-0002](0002-yarp-proxy.md) | YARP Reverse Proxy | Accepted | 2026-01-12 |
| [ADR-0003](0003-argon2id-password-hashing.md) | Argon2id Password Hashing | Accepted | 2026-01-12 |
| [ADR-0004](0004-central-package-management.md) | Central Package Management | Accepted | 2026-01-10 |
| [ADR-0005](0005-scalar-api-documentation.md) | Scalar API Documentation | Accepted | 2026-01-10 |
| [ADR-0006](0006-mapperly-object-mapping.md) | Mapperly Object Mapping | Accepted | 2026-01-11 |
| [ADR-0007](0007-fluentvalidation.md) | FluentValidation | Accepted | 2026-01-11 |
| [ADR-0008](0008-step-up-authentication.md) | Step-Up Authentication | Accepted | 2026-01-15 |
| [ADR-0009](0009-idempotency-monetary-operations.md) | Idempotent Monetary Operations | Accepted | 2026-07-13 |
| [ADR-0010](0010-pin-attempt-limiting.md) | PIN Attempt-Limiting (Lockout) | Accepted | 2026-07-14 |
| [ADR-0011](0011-pin-hash-pepper.md) | PIN-Hash Pepper (Keyed Hashing) | Accepted | 2026-07-15 |
| [ADR-0012](0012-login-attempt-limiting.md) | Password/Login Attempt-Limiting (Lockout) | Accepted | 2026-07-15 |
| [ADR-0013](0013-registration-user-enumeration.md) | Registration User-Enumeration (Bounded Acceptance) | Accepted | 2026-07-15 |
| [ADR-0014](0014-recipient-lookup-enumeration.md) | Recipient Lookup (Exact-Match, Harvest-Resistant) | Accepted | 2026-07-17 |
| [ADR-0015](0015-decouple-username-renameable-handle.md) | Decouple UserName from AzureTag (Renameable Handle) | Accepted | 2026-07-17 |
| [ADR-0016](0016-observability-three-pillars.md) | Observability: OpenTelemetry Three Pillars + Grafana LGTM | Accepted | 2026-07-20 |
| [ADR-0017](0017-pii-redaction-codeql-barrier.md) | PII-Safe Telemetry + CodeQL Log-Forging Barrier | Accepted | 2026-07-20 |
| [ADR-0018](0018-bff-origin-hardening.md) | BFF Origin Hardening (`__Host-` Cookie, Fetch-Metadata, No CORS) | Accepted | 2026-07-20 |
| [ADR-0019](0019-spa-bff-integration.md) | SPA/BFF Integration Architecture (Cookie Auth, One Error Channel) | Accepted | 2026-07-20 |
| [ADR-0020](0020-account-number-reveal.md) | On-Demand Account-Number Reveal (Masked-by-Default + PIN-Gated) | Accepted | 2026-07-21 |
| [ADR-0021](0021-refresh-token-rotation-bff-remint.md) | Refresh-Token Rotation with Reuse-Detection (+ BFF Silent Re-Mint) | Accepted | 2026-07-22 |
| [ADR-0022](0022-client-money-mutation-protocol.md) | Client-side money-mutation protocol | Accepted | 2026-07-25 |
| [ADR-0023](0023-runtime-response-validation.md) | Runtime response validation | Accepted | 2026-07-25 |
| [ADR-0024](0024-no-client-facing-optimistic-concurrency.md) | No client-facing optimistic concurrency | Accepted | 2026-07-25 |
| [ADR-0025](0025-originals-reference-mine.md) | The originals are a reference mine, not a code source | Accepted | 2026-07-25 |
| [ADR-0026](0026-absolute-session-cap-reauthentication.md) | The absolute session cap is re-authenticated, never extended | Accepted | 2026-07-30 |
| [ADR-0027](0027-dark-mode-through-css-custom-properties.md) | Dark mode through CSS custom properties, decided before the first paint | Accepted | 2026-07-30 |
| [ADR-0028](0028-data-router-for-blocking-browser-back.md) | A data router, bought for one hook — blocking browser Back on a live idempotency key | Accepted | 2026-07-31 |
| [ADR-0029](0029-contract-conformance-gate.md) | One suite, two backends — making mock drift fail the build | Accepted | 2026-07-31 |
| [ADR-0030](0030-real-backend-integration-layer.md) | Running the app's own data layer against the real backend | Accepted | 2026-08-04 |
| [ADR-0031](0031-e2e-playwright.md) | The app in a real browser, against the real stack | Accepted | 2026-08-04 |
| [ADR-0032](0032-real-stack-layers-in-ci.md) | Running the real-backend layers in CI, on a stack the job owns | Accepted | 2026-08-04 |
| [ADR-0033](0033-root-error-boundary.md) | A root error boundary, so a render error is not a blank page | Accepted | 2026-08-04 |
| [ADR-0034](0034-failed-family-revoke-recovery.md) | Recovery for a family revoke that fails | Accepted | 2026-08-08 |
| [ADR-0035](0035-transaction-number-check-symbol.md) | A check symbol on the transaction number | Accepted | 2026-08-09 |
| [ADR-0036](0036-account-number-collision-recovery.md) | Recovering from an account-number collision | Accepted | 2026-08-09 |
| [ADR-0037](0037-atomic-registration.md) | Registration is all-or-nothing | Accepted | 2026-08-09 |
| [ADR-0038](0038-bff-session-is-the-only-credential.md) | The session is the only credential the BFF will accept | Accepted | 2026-08-10 |
| [ADR-0039](0039-bff-session-cache-is-a-fallback.md) | The BFF session cache is a fallback, never the answer | Accepted | 2026-08-10 |
| [ADR-0040](0040-changing-a-credential-requires-the-current-one.md) | Changing a credential requires proving the current one | Accepted | 2026-08-12 |
| [ADR-0041](0041-the-api-verifies-the-transfer-pin.md) | The API verifies the transfer PIN, not the BFF | Accepted | 2026-08-13 |
| [ADR-0042](0042-a-transfer-authorisation-is-bound-and-spent-once.md) | A transfer authorisation is bound to its amount and payee, and spent once | Accepted | 2026-08-16 |
| [ADR-0043](0043-the-document-declares-the-error-body.md) | The published document declares the error body the API actually sends | Accepted | 2026-08-18 |
| [ADR-0044](0044-the-audit-trail-is-append-only-and-chained.md) | Security events go to an append-only, hash-chained table | Accepted | 2026-08-19 |
| [ADR-0045](0045-the-enrolment-notice-rides-the-enrolment-and-stops-at-a-pickup-directory.md) | The enrolment notice rides the enrolment, and stops at a pickup directory | Accepted | 2026-09-03 |

</details>
## Creating a New ADR

1. Copy `0000-template.md` to a new file with the next sequence number
2. Fill in all sections
3. Update this index
4. Submit as part of your PR

## ADR Lifecycle

- **Proposed**: Under discussion
- **Accepted**: Decision made and implemented
- **Deprecated**: No longer applies
- **Superseded**: Replaced by another ADR

## References

- [ADR GitHub Organization](https://adr.github.io/)
- [MADR Template](https://adr.github.io/madr/)
