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

Fifty decisions is more than anyone reads cold. These four carry the architecture; the rest is
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
[0039](0039-bff-session-cache-is-a-fallback.md) the BFF session cache is a fallback, never the answer ·
[0054](0054-the-bff-serves-the-built-spa-under-a-csp-measured-against-it.md) the BFF serves the
built SPA under a CSP measured against it

**Money** — [0009](0009-idempotency-monetary-operations.md) idempotent monetary operations (server) ·
[0022](0022-client-money-mutation-protocol.md) client money-mutation protocol ·
[0024](0024-no-client-facing-optimistic-concurrency.md) no client-facing optimistic concurrency ·
[0028](0028-data-router-for-blocking-browser-back.md) a data router, bought for one hook ·
[0035](0035-transaction-number-check-symbol.md) a check symbol on the transaction number ·
[0036](0036-account-number-collision-recovery.md) recovering from an account-number collision ·
[0046](0046-one-money-cap-for-every-move-and-the-client-promises-what-the-contract-publishes.md) one
money cap for every move, and the client promises what the contract publishes ·
[0050](0050-a-utc-day-bounds-a-users-external-transfers-and-the-mint-says-so-before-the-pin.md) a
UTC day bounds a user's external transfers, and the mint says so before the PIN

**Interface** — [0027](0027-dark-mode-through-css-custom-properties.md) dark mode through CSS custom properties ·
[0033](0033-root-error-boundary.md) a root error boundary, so a render error is not a blank page

**Authentication and account safety** — [0003](0003-argon2id-password-hashing.md) Argon2id, built for PINs only ·
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
[0041](0041-the-api-verifies-the-transfer-pin.md) the API verifies the transfer PIN, not the BFF ·
[0049](0049-closing-an-account-is-authorised-like-a-transfer.md) closing an account is authorised
like a transfer

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
[0053](0053-the-committed-contract-is-what-the-api-generates.md) the spec is what the API generates

**Audit trail and owed notices** — [0044](0044-the-audit-trail-is-append-only-and-chained.md) the
audit trail is append-only and chained ·
[0045](0045-the-enrolment-notice-rides-the-enrolment-and-stops-at-a-pickup-directory.md) the
enrolment notice rides the enrolment, and stops at a pickup directory ·
[0047](0047-a-pin-change-owes-the-same-notice-an-enrolment-does.md) a PIN change owes the same notice ·
[0048](0048-the-api-is-the-runner-that-delivers-owed-notices.md) the API is the runner that delivers
owed notices ·
[0051](0051-the-relay-runs-as-an-azure-function-against-azurite.md) the relay runs as an Azure
Function, rehearsed locally against Azurite ·
[0052](0052-a-notice-names-the-audit-row-it-belongs-to.md) a notice names the audit row it belongs to

**Operations** — [0016](0016-observability-three-pillars.md) observability, three pillars ·
[0017](0017-pii-redaction-codeql-barrier.md) PII-safe telemetry and the log-forging barrier

**Build and tooling** — [0004](0004-central-package-management.md) central package management ·
[0006](0006-mapperly-object-mapping.md) Mapperly object mapping ·
[0025](0025-originals-reference-mine.md) the originals are a reference mine

Every decision listed is **Accepted** and shipped — there is no Proposed tier, and a status never
moves when a record turns out to be wrong: the correction lives in the ADR itself, struck in place
with a dated note against the clause it corrects, under the rule in
[`engineering-practices.md`](../engineering-practices.md#correcting-a-document). The table below
indexes those notes and nothing else — a row without a matching note in the ADR is the defect to
fix — and a pointer an ADR carries with no date is listed as *undated*. The lifecycle statuses at
the end of this page are the template's; only Accepted has ever been used.

| ADR | Changed by | What moved |
|---|---|---|
| [0003](0003-argon2id-password-hashing.md) | 2026-09-11 (no ADR or PR named) | Built for PINs, never for passwords: every password hash is Identity's PBKDF2 (V3, 100,000 iterations); "all password hashes use Argon2id" struck as never met. |
| [0007](0007-fluentvalidation.md) | 2026-08-04, three edits (no ADR or PR named) | "DTOs remain clean" did not happen (DataAnnotations *and* validators — two layers); auto-integration not taken; the model-state envelope that answers first is described; one example response replaced outright rather than struck — the chimera that never existed on the wire. |
| [0008](0008-step-up-authentication.md) | 2026-08-12 (sketches; no PR named) · 2026-08-13, ADR-0041 · 2026-08-18 (re-measured live) · 2026-09-04, ADR-0041 (naming `76b737c` and `d74603c`) · 2026-09-06, ADR-0049 | Four of five C# blocks diverge from the source, three of them naming a type never built; "Level 2" is two mechanisms; the Protected Operations table is the decision, not the state — transfers left the BFF gate, three gated paths became one (`/full-number`), every `/api` request reads the level, and *Delete account · Level 2* gained its mechanism on ADR-0042's rail. |
| [0009](0009-idempotency-monetary-operations.md) | *undated* — ADR-0022 · *undated* — ADR-0018 | The client half of the protocol is specified in ADR-0022; the loopback dev CORS policy and the exposed replay header are gone with ADR-0018. |
| [0010](0010-pin-attempt-limiting.md) | 2026-08-06 (no PR named) · 2026-09-15, ADR-0040 | The BFF `SecurityOptions` declarations the Context cites are deleted; the live values are the API's `ValidationRules`, unchanged. ADR-0040 found a PIN replaced with no proof of the current one — nothing guessed, so this limiting never engaged — and closed it. |
| [0013](0013-registration-user-enumeration.md) | 2026-09-03, ADR-0045 | "No email infrastructure" narrowed to "no relay"; the conclusion and the deferral of Option 3 stand. |
| [0014](0014-recipient-lookup-enumeration.md) | 2026-09-15 (residual, measured; no ADR named) | The anti-harvest limit is the BFF's and the API has none: 21 lookups sent straight to the API answered 200 ×21 where the BFF answered 429 at the 21st. |
| [0015](0015-decouple-username-renameable-handle.md) | 2026-08-10, ADR-0039 | The stale-handle residual closed (the rename is BFF-owned and `/me` reads through), its two reasons for staying open were wrong, and the concurrent-rename clause is no longer permanent. |
| [0017](0017-pii-redaction-codeql-barrier.md) | 2026-09-11 and 2026-09-14, `d9a593b` (the log-identifier rule, then its review) | `HmacRedactor`'s "key in a public repo" reason struck; "no amounts" was false from the day it was written (the deposit and withdrawal lines, now gone); the "what it does not see" gap struck — the request log names the route pattern. |
| [0019](0019-spa-bff-integration.md) | 2026-09-05 (no PR named; `e4973da` for the 2026-08-17 half) · *undated* — ADR-0023 | The 401 exempt list is `IN_FLOW_401_CODES` and holds ADR-0042's three authorisation codes; Decision 6's hand-written BFF types are `z.infer` of runtime schemas. |
| [0020](0020-account-number-reveal.md) | 2026-08-13, review of PR #105 · *undated* — ADR-0038 | PSD2 art. 4(32) was overstated (it reaches PISP/AISP activity only); the dual-mode caveat's through-the-BFF bearer bypass was measured and closed. |
| [0021](0021-refresh-token-rotation-bff-remint.md) | 2026-08-04 (amendment; no PR named) · 2026-08-08, ADR-0034 | A failing family revoke can no longer turn the uniform 401 into a 500; what to do when it fails is decided — neither an inline retry nor a durable work item. |
| [0022](0022-client-money-mutation-protocol.md) | 2026-08-12 (no PR named) · 2026-08-16 (ADR-0041, ADR-0042) · 2026-09-04, ADR-0041 · 2026-09-15 (no ADR named) | `stepup-interceptor.test.tsx` now asserts the body half of the replay; BODY mode's reason was stale (the transfer PIN moved into the body, then into a header); the reveal is the only level-2 surface, not the third; the byte-identical replay is held by the test and exercised by no live caller. |
| [0023](0023-runtime-response-validation.md) | 2026-09-10, ADR-0053 · 2026-09-15, ADR-0043 (a pointer) | "Guarded by three layers" said more than two of them did; the document-versus-code half is now a backend test; ADR-0043 corrects the generation pipeline this validation consumes. |
| [0027](0027-dark-mode-through-css-custom-properties.md) | 2026-07-30 (same day; no PR named) | The brand ramp could not carry both of its jobs on a dark ground (white at 3.22:1); a `brandFill` role added. |
| [0028](0028-data-router-for-blocking-browser-back.md) | 2026-08-04, ADR-0033 | The uncovered-chrome consequence closed: `AppErrorBoundary` wraps the whole tree in `App.tsx`, above `Provider` and `ThemeProvider`. |
| [0029](0029-contract-conformance-gate.md) | 2026-08-10 (amendment; no PR named) · 2026-09-04, ADR-0041 · 2026-09-15, ADR-0043 (a pointer) | `test:contract:mock` ran in no CI job, so the title was half true; "the only thing the money handlers consult" is now true of the reveal alone; ADR-0043 corrects the generation pipeline this gate consumes. |
| [0030](0030-real-backend-integration-layer.md) | 2026-09-04, ADR-0041 | The reveal no longer shares the transfer's code path; it is the only level-2 route, and a transfer never 403s. |
| [0031](0031-e2e-playwright.md) | 2026-09-04, ADR-0041 · 2026-09-11 and 2026-09-15, ADR-0054 (`f1b3509`) | Only `/full-number` is gated; CI runs the suite against the BFF-served build under its CSP with `E2E_BASE_URL`; "not wired into CI" struck. |
| [0036](0036-account-number-collision-recovery.md) | *undated* — ADR-0037 | The non-atomic-registration residual closed; the collision retry is kept. |
| [0040](0040-changing-a-credential-requires-the-current-one.md) | 2026-09-03 (T8 on 2026-08-15, `8f0abba`) · 2026-09-04 (ADR-0041, ADR-0042) | First-PIN enrolment now costs the password; the direct-API bypass remains only for `/full-number`; the "separate decision" on the API gate was taken the next day. |
| [0041](0041-the-api-verifies-the-transfer-pin.md) | 2026-08-16, ADR-0042 · 2026-08-17 · 2026-08-19, twice (no PR named) · *undated* — the review of its own PR · 2026-09-06, ADR-0049 · 2026-09-15 (residual, measured) | "Action filter" is middleware; the session gate went deny-by-default, then unconditional for every proxied `/api` request; the PSD2 justification was overstated; closures are authorised on ADR-0042's rail; reopen trigger (a) was tested and did not fire; the reveal's level-2 gate is the BFF's alone — a bearer presented to the API reads the number with no PIN. |
| [0042](0042-a-transfer-authorisation-is-bound-and-spent-once.md) | 2026-09-06, ADR-0049 · 2026-09-07, ADR-0050 | The rail carries its first non-money operation (`consumedByTransactionId` nullable, the payload applied not bumped); the external mint answers a non-PIN 422 ahead of the PIN. |
| [0043](0043-the-document-declares-the-error-body.md) | 2026-09-10, ADR-0053 · 2026-09-11 (ADR-0050's members; no PR named) | Half the gap closed — the document is what the code generates and a stale commit now fails; the extension members are declared on the inline 422 schemas. |
| [0044](0044-the-audit-trail-is-append-only-and-chained.md) | 2026-08-19, #231 · 2026-08-20 · 2026-08-22 · 2026-08-25 · 2026-08-26 · 2026-08-27 · 2026-08-29 (D6) · 2026-08-30 (D7) — the ADR's own, no PR named · 2026-09-03, ADR-0045 · 2026-09-04, ADR-0047 · 2026-09-06, ADR-0049 · 2026-09-07, ADR-0050 · 2026-09-11 (the three mints; no PR named) · 2026-09-15 (the rule's new home) | Retention reasoning rewritten and erasure narrowed to undischarged; tail truncation measured and the "tripwire" claim corrected; the marker is no longer a literal; the anchoring sentence named one control where there are two, complementary; the tamper-detection claim bounded to a key's epoch; verifier verbs 3→5; events 14→15 and the mints now audit wrong and locked PINs; two log-only instances added; the correction rule it cites moved to `engineering-practices.md`. |
| [0045](0045-the-enrolment-notice-rides-the-enrolment-and-stops-at-a-pickup-directory.md) | 2026-09-04, ADR-0048 · 2026-09-04, ADR-0047 · 2026-09-09, twice (ADR-0052's citation; the live lease) | D3 "never by the API" reversed — the API is the runner and the verb claims under a lease; D8 reversed — a PIN change owes a notice; the "no foreign key" decision is the `AddSubscriberNotices` migration's, said here because readers arrive here; "a live runner holds" became held under another runner's live lease. |
| [0046](0046-one-money-cap-for-every-move-and-the-client-promises-what-the-contract-publishes.md) | 2026-09-04 (the sweep; no ADR named) · 2026-09-07, ADR-0050 · 2026-09-10, ADR-0053 | D4's two unmeasured sentences measured (no amounts); D6's dead `DailyTransferLimit` deleted; D7's "neither starts nor forecloses" struck for external transfers per user; the contract-check loop closed by a backend test, D5's guard not redundant. |
| [0047](0047-a-pin-change-owes-the-same-notice-an-enrolment-does.md) | 2026-09-09, ADR-0052 · 2026-09-10 (no PR named) | The evidence join is exact for notices that name a row, `(ActorUserId, Event)` the fallback; the "no foreign key" citation moved to the migration; the forcing-function test stayed green. |
| [0048](0048-the-api-is-the-runner-that-delivers-owed-notices.md) | 2026-09-08, ADR-0051 · 2026-09-09 (two same-day self-corrections; no PR named) | The Function runner shipped: Warning → Information, the sweep moved to Infrastructure, four runner-guarded validators not "every"; "leased by a live runner" became held under another runner's live lease, and "no runner is live" was never emitted; the pasted API log line re-derived from the emitter. |
| [0049](0049-closing-an-account-is-authorised-like-a-transfer.md) | 2026-09-06 (the record's own day: pre-review, then the frontend follow-up `3c30122`) · 2026-09-07, ADR-0050 · 2026-09-11, ADR-0044 | The deposit race has its own SQL Server proof; both targets handle the mint; the `TimeProvider` deferral was wrong on its facts; a wrong or locked PIN at the mint is audited. |
| [0050](0050-a-utc-day-bounds-a-users-external-transfers-and-the-mint-says-so-before-the-pin.md) | 2026-09-07 (review round 1) · 2026-09-08 (the frontend mirror PR) · 2026-09-11 (a separate small PR) | The application lock takes a `@LockTimeout`; the four members are declared in the document and carried by the client; `available`/`requested` declared inline; the internal mint's bare 422 names `PIN_REQUIRED`. |
| [0051](0051-the-relay-runs-as-an-azure-function-against-azurite.md) | 2026-09-08 (pre-review) · 2026-09-09 (review; no PR named) | "Every rule runner-guarded" → four; a missing `Notices:Schedule` no longer fails quiet — `ValidateTheScheduleItIsBoundTo()` runs unconditionally; `UseMonitor` cleared so the timer cannot fire at start. |
| [0053](0053-the-committed-contract-is-what-the-api-generates.md) | 2026-09-11 (no PR named) | The comma-decimal-locale defect withdrawn as false, measured through the real route; the "either defect fixed" trigger narrowed to the XML generator. |

The next free number is **0055**.

<details>
<summary>Full list in numeric order</summary>

| ID | Title | Status | Date |
|----|-------|--------|------|
| [ADR-0000](0000-template.md) | ADR Template | Template | - |
| [ADR-0001](0001-bff-pattern.md) | BFF Pattern | Accepted | 2026-01-12 |
| [ADR-0002](0002-yarp-proxy.md) | YARP Reverse Proxy | Accepted | 2026-01-12 |
| [ADR-0003](0003-argon2id-password-hashing.md) | Argon2id hashing — built for PINs; passwords use Identity's PBKDF2 (see its correction) | Accepted | 2026-01-12 |
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
| [ADR-0046](0046-one-money-cap-for-every-move-and-the-client-promises-what-the-contract-publishes.md) | One money cap for every move, and the client promises what the contract publishes | Accepted | 2026-09-03 |
| [ADR-0047](0047-a-pin-change-owes-the-same-notice-an-enrolment-does.md) | A PIN change owes the same notice an enrolment does | Accepted | 2026-09-04 |
| [ADR-0048](0048-the-api-is-the-runner-that-delivers-owed-notices.md) | The API is the runner that delivers owed notices | Accepted | 2026-09-04 |
| [ADR-0049](0049-closing-an-account-is-authorised-like-a-transfer.md) | Closing an account is authorised like a transfer | Accepted | 2026-09-06 |
| [ADR-0050](0050-a-utc-day-bounds-a-users-external-transfers-and-the-mint-says-so-before-the-pin.md) | A UTC day bounds a user's external transfers, and the mint says so before the PIN | Accepted | 2026-09-07 |
| [ADR-0051](0051-the-relay-runs-as-an-azure-function-against-azurite.md) | The relay runs as an Azure Function, rehearsed locally against Azurite | Accepted | 2026-09-08 |
| [ADR-0052](0052-a-notice-names-the-audit-row-it-belongs-to.md) | A notice names the audit row it belongs to | Accepted | 2026-09-09 |
| [ADR-0053](0053-the-committed-contract-is-what-the-api-generates.md) | The committed contract is what the API generates | Accepted | 2026-09-10 |
| [ADR-0054](0054-the-bff-serves-the-built-spa-under-a-csp-measured-against-it.md) | The BFF serves the built SPA under a CSP measured against it | Accepted | 2026-09-11 |

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
