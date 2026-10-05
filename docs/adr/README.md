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

Sixty-four decisions is more than anyone reads cold. These four carry the architecture; the rest
is detail hanging off them. *(It said fifty until 2026-09-24, fifty-six until 2026-09-28,
fifty-seven until 2026-09-29, fifty-eight until 2026-10-01, fifty-nine until ADR-0060, the same
day, sixty until 2026-10-02, sixty-one until ADR-0062, 2026-10-03, sixty-two until ADR-0063,
2026-10-04, and sixty-three until ADR-0064, 2026-10-05.)*

| | Why this one |
|---|---|
| **[ADR-0001](0001-bff-pattern.md) — BFF pattern** | The decision everything else inherits: the JWT never reaches the browser, so the whole auth story follows from here. *(It said "the browser never holds a token" until 2026-09-25; the browser does hold an HttpOnly session cookie, and a one-shot PIN authorisation's id between the PIN and the operation it authorises.)* |
| **[ADR-0009](0009-idempotency-monetary-operations.md) — Idempotent monetary operations** | Where this stops being a CRUD app. A keyed HMAC over raw request bytes, a five-state protocol, and a deliberate correctness-over-availability trade. |
| **[ADR-0019](0019-spa-bff-integration.md) — SPA/BFF integration** | Cookie auth and one error channel: the contract the entire frontend is written against. |
| **[ADR-0022](0022-client-money-mutation-protocol.md) — Client money-mutation protocol** | The client half of ADR-0009 — which outcomes keep an idempotency key and which spend it. Every cell in that table is a double-spend if it is wrong. |

## By theme

**Platform and topology** — where each part runs, and who may call whom.

- **[ADR-0001](0001-bff-pattern.md) BFF pattern** — one of the four above
- [ADR-0002](0002-yarp-proxy.md) YARP reverse proxy
- [ADR-0018](0018-bff-origin-hardening.md) BFF origin hardening
- **[ADR-0019](0019-spa-bff-integration.md) SPA/BFF integration** — one of the four above
- [ADR-0039](0039-bff-session-cache-is-a-fallback.md) the BFF session cache is a fallback, never the answer
- [ADR-0054](0054-the-bff-serves-the-built-spa-under-a-csp-measured-against-it.md) the BFF serves the built SPA under a CSP measured against it
- [ADR-0055](0055-the-api-serves-one-client-the-bff.md) the API serves one client, the BFF
- [ADR-0058](0058-the-api-gives-up-cleanly-when-the-database-is-down.md) the API gives up cleanly when the database is down: a 503 before the BFF stops waiting, and never in the middle of a commit
- [ADR-0060](0060-migrations-run-as-a-one-shot-container-before-the-app.md) migrations run as a one-shot container before the app: `migrate` waits for the database, never creates one on Azure SQL, and refuses a database ahead of the build
- [ADR-0061](0061-the-demo-is-deployed-to-azure-container-apps-with-no-database-password.md) the demo is deployed to Azure Container Apps with no database password: two managed identities sign in, a guarded SQL file creates their users, the logs are private and capped, and the deployment identity cannot list a secret

**Money** — how a money move is applied once, bounded and numbered.

- **[ADR-0009](0009-idempotency-monetary-operations.md) idempotent monetary operations (server)** — one of the four above
- **[ADR-0022](0022-client-money-mutation-protocol.md) client money-mutation protocol** — one of the four above
- [ADR-0024](0024-no-client-facing-optimistic-concurrency.md) no client-facing optimistic concurrency
- [ADR-0028](0028-data-router-for-blocking-browser-back.md) a data router, bought for one hook
- [ADR-0035](0035-transaction-number-check-symbol.md) a check symbol on the transaction number
- [ADR-0036](0036-account-number-collision-recovery.md) recovering from an account-number collision
- [ADR-0046](0046-one-money-cap-for-every-move-and-the-client-promises-what-the-contract-publishes.md) one money cap for every move, and the client promises what the contract publishes
- [ADR-0050](0050-a-utc-day-bounds-a-users-external-transfers-and-the-mint-says-so-before-the-pin.md) a UTC day bounds a user's external transfers, and the mint says so before the PIN

**Interface** — what the user sees when the theme changes, a page fails, or the service is slow or down.

- [ADR-0027](0027-dark-mode-through-css-custom-properties.md) dark mode through CSS custom properties
- [ADR-0033](0033-root-error-boundary.md) a root error boundary, so a render error is not a blank page
- [ADR-0059](0059-the-spa-tells-the-visitor-when-the-service-is-slow-or-down.md) the SPA tells the visitor when the service is slow or down: every wait bounded, a read retried once after the wait its answer names, and words that say whether anything changed

**Authentication and account safety** — passwords, sessions, the PIN, and the one-shot authorisation it buys.

- [ADR-0003](0003-argon2id-password-hashing.md) Argon2id, built for PINs only
- [ADR-0008](0008-step-up-authentication.md) step-up authentication
- [ADR-0010](0010-pin-attempt-limiting.md) PIN attempt-limiting
- [ADR-0011](0011-pin-hash-pepper.md) PIN-hash pepper
- [ADR-0012](0012-login-attempt-limiting.md) login attempt-limiting
- [ADR-0021](0021-refresh-token-rotation-bff-remint.md) refresh-token rotation with reuse detection — the rotation superseded by ADR-0057
- [ADR-0026](0026-absolute-session-cap-reauthentication.md) the absolute session cap is re-authenticated, never extended
- [ADR-0034](0034-failed-family-revoke-recovery.md) recovery for a family revoke that fails — superseded by ADR-0057
- [ADR-0037](0037-atomic-registration.md) registration is all-or-nothing
- [ADR-0038](0038-bff-session-is-the-only-credential.md) the session is the only credential the BFF accepts
- [ADR-0040](0040-changing-a-credential-requires-the-current-one.md) changing a credential requires proving the current one
- [ADR-0041](0041-the-api-verifies-the-transfer-pin.md) the API verifies the transfer PIN, not the BFF
- [ADR-0042](0042-a-transfer-authorisation-is-bound-and-spent-once.md) a transfer authorisation is bound to its amount and payee, and spent once
- [ADR-0049](0049-closing-an-account-is-authorised-like-a-transfer.md) closing an account is authorised like a transfer
- [ADR-0056](0056-a-withdrawal-is-authorised-like-a-transfer.md) a withdrawal is authorised like a transfer, and the funds answer comes first
- [ADR-0057](0057-the-bffs-refresh-token-is-one-reusable-grant-per-session.md) the BFF's refresh token is one reusable grant per session

**Not leaking who exists** — what a stranger can learn about other users.

- [ADR-0013](0013-registration-user-enumeration.md) registration enumeration
- [ADR-0014](0014-recipient-lookup-enumeration.md) recipient lookup, exact-match and harvest-resistant
- [ADR-0015](0015-decouple-username-renameable-handle.md) decoupling the username from a renameable handle
- [ADR-0020](0020-account-number-reveal.md) on-demand account-number reveal

**Contract and correctness** — how the frontend, the backend and the published contract are held together.

- [ADR-0007](0007-fluentvalidation.md) FluentValidation
- [ADR-0023](0023-runtime-response-validation.md) runtime response validation
- [ADR-0005](0005-scalar-api-documentation.md) Scalar API documentation
- [ADR-0029](0029-contract-conformance-gate.md) one suite, two backends
- [ADR-0030](0030-real-backend-integration-layer.md) the app's data layer against the real backend
- [ADR-0031](0031-e2e-playwright.md) the app in a real browser
- [ADR-0032](0032-real-stack-layers-in-ci.md) the real-stack layers in CI
- [ADR-0043](0043-the-document-declares-the-error-body.md) the document declares the error body
- [ADR-0053](0053-the-committed-contract-is-what-the-api-generates.md) the spec is what the API generates

**Audit trail and owed notices** — what is recorded, how the record is checked, and what a user is told.

- [ADR-0044](0044-the-audit-trail-is-append-only-and-chained.md) the audit trail is append-only and chained
- [ADR-0045](0045-the-enrolment-notice-rides-the-enrolment-and-stops-at-a-pickup-directory.md) the enrolment notice rides the enrolment, and stops at a pickup directory
- [ADR-0047](0047-a-pin-change-owes-the-same-notice-an-enrolment-does.md) a PIN change owes the same notice
- [ADR-0048](0048-the-api-is-the-runner-that-delivers-owed-notices.md) the API is the runner that delivers owed notices
- [ADR-0051](0051-the-relay-runs-as-an-azure-function-against-azurite.md) the relay runs as an Azure Function, rehearsed locally against Azurite
- [ADR-0052](0052-a-notice-names-the-audit-row-it-belongs-to.md) a notice names the audit row it belongs to

**The public demo** — how each visitor gets a demo of their own, and how it ends.

- [ADR-0062](0062-demo-visitors-get-private-copies-from-a-prepared-pool.md) demo visitors get private copies from a prepared pool: free copies have no password, a handle resolves only inside its copy, and a copy whose time is over is deleted whole while its audit rows stay
- [ADR-0063](0063-a-visitor-claims-a-prepared-copy-instead-of-registering.md) a visitor claims a prepared copy instead of registering: one conditional statement decides who has a copy, sign-in lets in only the owner of a claimed copy whose time is not over, registration is closed, and a client, a copy and a day each have a cap
- [ADR-0064](0064-the-azure-deployment-runs-the-demo-from-a-scheduled-pool-job.md) the Azure deployment runs the demo from a scheduled pool job: one switch of the template turns it on, a job the policy allows by name refills the pool every four hours as the app's database identity, and a deployment reads from the app whether the demo is on

**Operations** — telemetry, and keeping personal data out of it.

- [ADR-0016](0016-observability-three-pillars.md) observability, three pillars
- [ADR-0017](0017-pii-redaction-codeql-barrier.md) PII-safe telemetry and the log-forging barrier

**Build and tooling** — packages, mapping, and what the earlier code is for.

- [ADR-0004](0004-central-package-management.md) central package management
- [ADR-0006](0006-mapperly-object-mapping.md) Mapperly object mapping
- [ADR-0025](0025-originals-reference-mine.md) the originals are a reference mine

Every decision listed is **Accepted** and shipped — there is no Proposed tier, and a status never
moves when a record turns out to be wrong: the correction lives in the ADR itself, struck in place
with a dated note against the clause it corrects, under the rule in
[`engineering-practices.md`](../engineering-practices.md#correcting-a-document). The table below
indexes those notes and nothing else — a row without a matching note in the ADR is the defect to
fix — and a pointer an ADR carries with no date is listed as *undated*. The lifecycle statuses at
the end of this page are the template's; only Accepted has ever been used. *(2026-09-28: "shipped"
had two exceptions while PR-1 was being written, two parts of ADR-0057 decided before they were
built: its §5.3 session stamp and the renewal-rate detector of its §6. Both were built the same
day, each as its own commit of PR-1.)*
*(2026-10-02: ADR-0061 is shipped as templates and scripts; none of them has run on Azure yet,
and the record says so. 2026-10-03: it also says what a throwaway trial measured there by hand.
Later that day the first deployment's step 6 ran the users file, whose first check refused a view
of Microsoft's in the new database; the record says what was found and how that check was
narrowed. It now also has the deployment's steps 1 to 5, which ran before step 6, and step 6's
second stop, found before the next run: the permission list would have refused Azure's own grant
to `public` on that view, which it now keeps. The second session ran the same day, steps 12 to
21: the demo is deployed, and the record says what each step showed. One decision changed with
it, the tenth: three alerts, not four, because the metric the fourth one read reported nothing
for an hour in which the log workspace ingested 446 rows.)*
*(2026-10-05: ADR-0064 is shipped as templates, scripts, tests and a runbook's third session.
None of it has run on Azure, and the record says so.)*

<details>
<summary>What changed in each decision after it was accepted — 50 records</summary>

| ADR | Changed by | What moved |
|---|---|---|
| [0003](0003-argon2id-password-hashing.md) | 2026-09-11 (no ADR or PR named) · 2026-09-17 (no ADR or PR named) | Built for PINs, never for passwords: every password hash is Identity's PBKDF2 (V3, 100,000 iterations); "all password hashes use Argon2id" struck as never met; the password methods the correction called test-only are deleted with their tests, so the Decision's 64 MB and 3 iterations are struck for the PIN profile's 19 MB and 2. |
| [0007](0007-fluentvalidation.md) | 2026-08-04, four edits (no ADR or PR named) | "DTOs remain clean" did not happen (DataAnnotations *and* validators — two layers); auto-integration not taken; the model-state envelope that answers first is described; one example response replaced outright rather than struck — the chimera that never existed on the wire. |
| [0008](0008-step-up-authentication.md) | 2026-08-12 (sketches and the Validation corrections, ADR-0040; no PR named) · 2026-08-13, ADR-0041 · 2026-08-18 (re-measured live) · 2026-09-04, ADR-0041 (naming `76b737c` and `d74603c`) · 2026-09-06, ADR-0049 · 2026-09-15 (the rule's new home) · 2026-09-17 (no ADR or PR named) · 2026-09-28, ADR-0057 | All five C# blocks diverge from the source, four of them naming a type never built; "Level 2" is two mechanisms; changing a PIN costs the current one (ADR-0040) — until it did, a session could replace the PIN, leaving this gate and ADR-0010's attempt-limiting both inoperative; the correction rule it quotes moved to `engineering-practices.md`; the Protected Operations table is the decision, not the state — transfers left the BFF gate, three gated paths became one (`/full-number`), every `/api` request reads the level, and *Delete account · Level 2* gained its mechanism on ADR-0042's rail. PINs are Argon2id but passwords never were: "same as passwords" struck. "Only the BFF may rotate refresh tokens" became "renew with a grant": the grant no longer rotates, and the proxied revoke and logout are short-circuited too. |
| [0009](0009-idempotency-monetary-operations.md) | *undated* — ADR-0018 · *undated* — ADR-0022 · 2026-09-21, ADR-0056 · 2026-09-24 (no ADR or PR named) · 2026-09-30, ADR-0058 · 2026-10-01, 2026-10-03 and 2026-10-05 (no ADR or PR named) | The loopback dev CORS policy and the exposed replay-header dependency are gone with ADR-0018; the client half of the protocol is specified in ADR-0022. The keyed-digest reason — "the withdraw body contains a 6-digit PIN" — lost its only instance when the withdrawal joined the step-up rail; the digest stays keyed on the surviving reason: every monetary body is low-entropy and mostly known. A money request's 503 may say `applied: false`, and only when it holds the claim it made and started no commit; storing an answer and releasing a claim get 3 s each, and an empty success is never stored for replay. `ProcessingStaleAfter` went from 10 minutes to 2. The 409 `IDEMPOTENCY_RESULT_UNKNOWN` carries `applied: true` when the request that answers read the key's record from the database as committed, and no `applied` when a retried attempt finds the record gone or claimed again with another body, where the detail distinguishes the missing row from another request's record under the key (corrected 2026-10-03); the note says what the flag stands on, which tests hold it, and what is still withheld: the transaction's id, and the first two minutes, answered `IN_FLIGHT`. An oversized body is read and discarded before the 413, up to 1 MiB and for five seconds at most, so that the connection is not aborted under the BFF's proxy (2026-09-24). The body-limit sentence is struck (2026-10-05): the size attribute on the four endpoints was applied by routing when the endpoint was matched, before authentication, not by an MVC filter after the middleware had hashed the body; the endpoints now declare the limit as routing metadata only, the warning the filter's second attempt logged is gone, and "closing an authenticated hash-amplification DoS" is struck with it. |
| [0010](0010-pin-attempt-limiting.md) | 2026-08-06 (no PR named) · 2026-09-15, ADR-0040 · *undated* — ADR-0012 | The BFF `SecurityOptions` declarations the Context cites are deleted; the live values are the API's `ValidationRules`, unchanged. ADR-0040 found a PIN could be replaced with no proof of the current one — nothing guessed, so this limiting never engaged — and closed it; the password/login lockout follow-up is resolved in ADR-0012. |
| [0011](0011-pin-hash-pepper.md) | 2026-09-17 (no ADR or PR named) · 2026-10-01, ADR-0060 · 2026-10-03, ADR-0062 · 2026-10-03 (no ADR or PR named) | The password profile Context set aside, and the Validation test that it is never peppered, are deleted; the account-password path stays Identity's and out of scope. The Seeder validates the pepper at the start of `seed` and `reset`, not in `Program.cs` ahead of the command line, and at the start of the demo pool's `seed-pool` and `recycle`, whose job holds the API's pepper and key id. A `Security:PreviousPinPeppers` key that is not a whole number >= 1, has surrounding whitespace, shares its id with another key, or does not hold exactly one value is refused at the API's start and at the start of the same four Seeder commands, naming the key and never its value; the binder silently drops a key it cannot convert. A value on the section itself, under no key id, is refused too. |
| [0013](0013-registration-user-enumeration.md) | 2026-09-03, ADR-0045 · 2026-09-17, ADR-0037 (naming #94) | "No email infrastructure" narrowed to "no relay"; the conclusion and the deferral of Option 3 stand. Atomic registration's "remains a tracked follow-up" struck as delivered by ADR-0037; the neutral 409 still holds inside its transaction. |
| [0014](0014-recipient-lookup-enumeration.md) | 2026-09-15 (residual, measured; no ADR named) · 2026-10-04, ADR-0063 | The anti-harvest limit is the BFF's and the API has none: 21 lookups sent straight to the API answered 200 ×21 where the BFF answered 429 at the 21st. On the public demo registration is closed, so the throwaway account the limit counts by is a claimed copy, of which one client address is given `Demo:Claim:MaxPerClientPerDay` in a day; a copy's owner finds only its own copy's users. |
| [0015](0015-decouple-username-renameable-handle.md) | 2026-08-10 (no ADR or PR named) · 2026-08-10, ADR-0039 · *undated* — ADR-0039 | The stale-handle residual split: the token claim stays open and harmless; the session half closed (the rename is BFF-owned and `/me` reads through); the concurrent-rename clause is no longer permanent, and its two reasons for rejecting a per-session lock were wrong. |
| [0016](0016-observability-three-pillars.md) | 2026-09-25 (no ADR or PR named) | Health probes leave the request log as well as the traces, except a probe that failed. |
| [0017](0017-pii-redaction-codeql-barrier.md) | 2026-09-11 and 2026-09-14 (the log-identifier rule, then its review; no PR named) | `HmacRedactor`'s "key in a public repo" reason struck; "no amounts" was false from the day it was written (the deposit and withdrawal lines, now gone); the "what it does not see" gap struck — the request log names the route pattern. |
| [0019](0019-spa-bff-integration.md) | 2026-09-05 (no PR named; `e4973da` for the 2026-08-17 half) · *undated* — ADR-0023 | The 401 exempt list is `IN_FLOW_401_CODES` and holds ADR-0042's three authorisation codes; Decision 6's hand-written BFF types are `z.infer` of runtime schemas. |
| [0020](0020-account-number-reveal.md) | 2026-08-13, review of PR #105 · *undated* — ADR-0038 | PSD2 art. 4(32) was overstated (it reaches PISP/AISP activity only); the dual-mode caveat's through-the-BFF bearer bypass was measured and closed. |
| [0021](0021-refresh-token-rotation-bff-remint.md) | 2026-08-04 (amendment; no PR named) · 2026-08-08, ADR-0034 · 2026-09-25 (no ADR or PR named) · 2026-09-28, ADR-0057 · 2026-09-30, ADR-0058 | A failing family revoke can no longer turn the uniform 401 into a 500; what to do when it fails is decided — neither an inline retry nor a durable work item. The sweep interval and EF's retry count are settings, with the values they had. Superseded in part by ADR-0057: rotation, reuse detection's family revoke, the grace window, and the BFF's failure policy and logout propagation are struck, and the refresh token lives 60 minutes from sign-in, never extended. Duende's "reuses by default" corrected to a recommendation, the draft-26 citation to RFC 10017 §6.1.2.2, which says only that tying the lifetimes "makes sense" and allows renewing on an observed expiry; the `FamilyId` residual is closed. The global-401 handler has no reuse revocation left to cover, and logout, which calls no API now, is struck from the paths that discover a dead grant. EF's retry count is 4 unless set, not 3. |
| [0022](0022-client-money-mutation-protocol.md) | 2026-08-12 (no PR named) · 2026-08-16 (ADR-0041, ADR-0042) · 2026-09-04, ADR-0041 · 2026-09-15, ADR-0041 · 2026-10-01, ADR-0059 · 2026-10-01 (no ADR or PR named) | `stepup-interceptor.test.tsx` now asserts the body half of the replay; BODY mode's reason was stale (the transfer PIN moved into the body, then a header authorisation replaced it); the reveal is the only level-2 surface, not the third; the body-and-key half of the byte-identical replay is held by the test and exercised by no live caller. Decision 6 holds only while no key is held: an edit with a key retained latches the check instead, in withdraw since 2026-09-23 and in deposit since ADR-0059. Decision 4's "has refetched recent transactions" is struck as never done: the four money mutations invalidate their tags on `RESULT_UNKNOWN` instead, and with `applied: true` the flow says the payment went through and `resetIntent` does nothing. A 409 on a money send that names no code latches the check. The residual's "no server endpoint answers" is struck for a record read as committed. |
| [0023](0023-runtime-response-validation.md) | 2026-09-10, ADR-0053 · 2026-09-15, ADR-0043 (a note under Decision 2) · 2026-09-17 (no ADR or PR named) | "Guarded by three layers" said more than two of them did; the document-versus-code half is now a backend test; the Zod was generated faithfully from a document wrong about errors, which ADR-0043 corrected; the 2026-09-10 correction's "Schemathesis runs only when started by hand" is struck, since it gates every pull request. |
| [0026](0026-absolute-session-cap-reauthentication.md) | 2026-09-28, ADR-0057 | Item 4 reversed: the API revokes one grant now, so re-authentication revokes the old session's grant at the API, and nothing else; the order gains the steps after the new cookie. |
| [0027](0027-dark-mode-through-css-custom-properties.md) | 2026-07-30 (same day; no PR named) | The brand ramp could not carry both of its jobs on a dark ground (white at 3.22:1); a `brandFill` role added. |
| [0028](0028-data-router-for-blocking-browser-back.md) | 2026-08-04, ADR-0033 | The uncovered-chrome consequence closed: `AppErrorBoundary` wraps the whole tree in `App.tsx`, above `Provider` and `ThemeProvider`. |
| [0029](0029-contract-conformance-gate.md) | 2026-08-10 (amendment; no PR named) · 2026-09-04, ADR-0041 · 2026-09-15, ADR-0043 (a note under Decision 1) · *undated* — the hole closed (no ADR or PR named) | `test:contract:mock` ran in no CI job, so the title was half true; "the only thing the money handlers consult" is now true of the reveal alone; ADR-0043 added the document itself to the suite as a third party; the live-session hole this ADR was written admitting is closed. |
| [0030](0030-real-backend-integration-layer.md) | 2026-09-04, ADR-0041 | The reveal no longer shares the transfer's code path; it is the only level-2 route, and a transfer never 403s. |
| [0031](0031-e2e-playwright.md) | 2026-09-04, ADR-0041 · 2026-09-11 and 2026-09-15, ADR-0054 | Only `/full-number` is gated; CI runs the suite against the BFF-served build under its CSP with `E2E_BASE_URL`; "not wired into CI" struck. |
| [0032](0032-real-stack-layers-in-ci.md) | 2026-09-15, ADR-0053 | Gating Schemathesis and Bruno, left to a separate decision, was half taken: Schemathesis is the `conformance` job in `ci.yml` and blocks a merge; Bruno stays manual. |
| [0034](0034-failed-family-revoke-recovery.md) | 2026-09-25 (no ADR or PR named) · 2026-09-28, ADR-0057 · 2026-09-30, ADR-0058 | EF's retry count and cap, and the sweep interval, are settings with the values they had; raising the retry budget lengthens how long the reuse path can hold a connection. The decision is superseded: the tripwire that replaced reuse detection revokes nothing, so no family revoke is left to fail. The measured record of which SQL errors EF retries stays; of the tests pinning the detection, two were deleted and the cancellation one rewritten for the tripwire's row, so the consequence that tests enforce the claim is struck. EF's budget is 4 retries under a 10 s cap unless set, not 3 under 30 s; the measured list of what EF retries does not move with it. |
| [0036](0036-account-number-collision-recovery.md) | *undated* — ADR-0037 · 2026-09-25 (no ADR or PR named) · 2026-09-30, ADR-0058 | The non-atomic-registration residual closed; the collision retry is kept. EF's retry count is a setting, 3 unless set until ADR-0058 made it 4. |
| [0037](0037-atomic-registration.md) | 2026-09-17 (no ADR or PR named) | The transaction holds Identity's PBKDF2 password hash, not Argon2id; the cost it describes stands. |
| [0039](0039-bff-session-cache-is-a-fallback.md) | 2026-09-30, ADR-0058 | The rename's client does set a `Timeout`, `BackendApi:TimeoutSeconds`, 55 s since ADR-0058 where it was `HttpClient`'s 100 s; the reason the per-session lock is not taken stands. |
| [0040](0040-changing-a-credential-requires-the-current-one.md) | 2026-09-03 (T8 on 2026-08-15, `8f0abba`) · 2026-09-04 (ADR-0041, ADR-0042) · 2026-09-11 (no ADR or PR named) | First-PIN enrolment now costs the password; the direct-API bypass remains only for `/full-number`; the "separate decision" on the API gate was taken the next day; `PinSetupPage` is no longer the frontend's only caller — Settings' Change PIN dialog, on the change path, sends the current PIN. |
| [0041](0041-the-api-verifies-the-transfer-pin.md) | 2026-08-16, ADR-0042 · 2026-08-17 · 2026-08-19, twice (no PR named) · *undated* — the review of its own PR · 2026-09-06, ADR-0049 · 2026-09-15 (residual, measured) | "Action filter" is middleware; the session gate went deny-by-default, then unconditional for every proxied `/api` request; the PSD2 justification was overstated; closures are authorised on ADR-0042's rail; reopen trigger (a) was tested and did not fire; the reveal's level-2 gate is the BFF's alone — a bearer presented to the API reads the number with no PIN. |
| [0042](0042-a-transfer-authorisation-is-bound-and-spent-once.md) | 2026-09-06, ADR-0049 · 2026-09-07, ADR-0050 · 2026-09-14, ADR-0044 (its second-factor note) · 2026-10-03, ADR-0062 | The rail carries its first non-money operation (`consumedByTransactionId` nullable, the payload applied not bumped); the external mint answers a non-PIN 422 ahead of the PIN; the audit row a consumed authorisation buys now names it, under the chain's hash. One statement deletes from the table: a demo copy's authorisations leave with the copy, and with its ledger the evidence pack of its transfers; nothing sweeps it by age. |
| [0043](0043-the-document-declares-the-error-body.md) | 2026-09-10, ADR-0053 · 2026-09-11 (the extension members, ADR-0050's and `INSUFFICIENT_FUNDS`'s; no PR named) · 2026-09-15, twice (ADR-0053 D6's note) · *undated* — the 2026-09-10 note corrected in its own PR | Half the gap closed, not the half this ADR was about — the document is what the code generates and a stale commit now fails; the extension members are declared on the inline 422 schemas; Schemathesis now gates per PR — both places that said it only reports are struck — and its first run found two shapes the document never declared; the 2026-09-10 note was corrected before merge, dropping its claim that nothing could catch this ADR's defects. |
| [0044](0044-the-audit-trail-is-append-only-and-chained.md) | 2026-08-19, #231 · 2026-08-20 · 2026-08-22 · 2026-08-23 · 2026-08-24 · 2026-08-25 · 2026-08-26 · 2026-08-27 · 2026-08-28 · 2026-08-29 (D6; the money refusals) · 2026-08-30 (D7) · 2026-09-02 (D8) — the ADR's own, no PR named · 2026-09-03, ADR-0045 · 2026-09-04, ADR-0047 · 2026-09-06, ADR-0049 · 2026-09-07, ADR-0050 · 2026-09-11 (the three mints and the anchor key; no PR named) · 2026-09-14 (`AuditDetails`; no PR named) · 2026-09-15 (the rule's new home) · 2026-09-16 (`SetPrimaryAccountAsync`; no PR named) · *undated* (D4's list; no ADR or PR named) · *undated* (the anchor record, its `export`, the anchor's tail key; no ADR or PR named) · 2026-09-21, ADR-0056 · 2026-09-28, ADR-0057 (PR-1) · 2026-09-29 and 2026-09-30, ADR-0058 · 2026-10-03, ADR-0062 | Retention reasoning rewritten and erasure narrowed to undischarged; tail truncation measured and the "tripwire" claim corrected; `tools/AzureBank.AuditVerifier` lets an operator verify the chain; readiness also tests updateability and INSERT permission; the marker is no longer a literal; the anchoring sentence named one control where there are two, complementary; an anchor record and its `export` exist without closing the end of the table, and an anchor now names its tail's key; `verify` prints the uncovered window, and deleting anchor records is loud only in the interior; the tamper-detection claim bounded to a key's epoch; `Audit:AnchorKey` has no ring, so rotating it ends the anchor chain; D8's `evidence` closed "no operator view"; verifier verbs 3→5; D4's log-only BFF list gained `RawAuthEntryBlocked`; two money-refusal events now write rows, business-validation refusals still out; events 14→15 and the mints now audit wrong and locked PINs; two log-only instances added; the transfer rows and `AccountDeleted` name the authorisation they consumed, which the evidence pack checks; the correction rule it cites moved to `engineering-practices.md`; "nothing else in `backend/src` takes an application lock" struck for `SetPrimaryAccountAsync`, which adds no lock order. The runbook's `NO AUTHORISATION APPLIES` no longer lists a withdrawal — that verdict against one is a finding now; `RecordRefusalAsync` goes 9 → 8, two sites out of the withdrawal and one in. D1's one exception, the reuse path, is closed: the tripwire records and revokes nothing, so a tripwire whose row cannot be written answers 500, as the unknown-grant refusal does. `RefreshTokenReuseRevokeFailed` is no longer raised — events logged 17 → 16, rows 15 → 14, `RecordRefusalAsync` sites 8 → 7 — and `RefreshTokenReuse` means the tripwire from that date. The same PR adds `RefreshRenewalRateHigh`, log-only: events logged 16 → 17, log-only 10 → 11, rows unchanged. The tail bound's refusal, a −2, answers 503 `SERVICE_UNAVAILABLE` rather than 500, and so does a tripwire's or an unknown grant's row whose write cannot reach the database; a write the database refuses is still the 500. An audited save whose commit fails asks the database whether its audit row landed before it retries: the retry used to commit an empty transaction and answer success, a deposit's 201 with no money moved. D6: a demo copy's ledger rows are deleted, with its users, by the Seeder's recycler, in set-based statements around the ledger's guard; that is not D6's third redesign, no audit row is deleted, and the deletion D6 calls refused still is for every other ledger row. "The compliant act and the attack are the same act" still holds: the copy's pool row records which dangling actor was a demo copy, as the operator's word, not proof. |
| [0045](0045-the-enrolment-notice-rides-the-enrolment-and-stops-at-a-pickup-directory.md) | 2026-09-04, ADR-0048 · 2026-09-04, ADR-0047 · 2026-09-09, twice (ADR-0047's foreign-key citation, ADR-0052; the live lease) | D3 "never by the API" reversed — the API is the runner and the verb claims under a lease; D8 reversed — a PIN change owes a notice; the "no foreign key" decision is the `AddSubscriberNotices` migration's, said here because readers arrive here; "a live runner holds" became held under another runner's live lease. |
| [0046](0046-one-money-cap-for-every-move-and-the-client-promises-what-the-contract-publishes.md) | 2026-09-04 (the sweep; no ADR named) · 2026-09-07, ADR-0050 · 2026-09-10, ADR-0053 · 2026-09-21, ADR-0056 | D4's two unmeasured sentences measured (no amounts); D6's dead `DailyTransferLimit` deleted; D7's "neither starts nor forecloses" struck for external transfers per user; the contract-check loop closed by a backend test, D5's guard not redundant. The cap is published on seven request schemas, not six; the two "six" figures inside the REJECTED deposit-cap option are deliberately left at six, because they price that option as it was priced when it was rejected. |
| [0047](0047-a-pin-change-owes-the-same-notice-an-enrolment-does.md) | 2026-09-09, ADR-0052 · 2026-09-10 (no PR named) · *undated* — ADR-0048 | The evidence join is exact for notices that name a row, `(ActorUserId, Event)` the fallback; the "no foreign key" citation moved to the migration; the forcing-function test stayed green; the relay, "ratified and unbuilt" that morning, was built the same evening as ADR-0048. |
| [0048](0048-the-api-is-the-runner-that-delivers-owed-notices.md) | 2026-09-08, ADR-0051 · 2026-09-09 (including two same-day self-corrections; no PR named) · *undated* — ADR-0052 | The Function runner shipped: Warning → Information, the sweep moved to Infrastructure, four runner-guarded validators not "every"; "leased by a live runner" became held under another runner's live lease, and "no runner is live" was never emitted; the pasted API log line re-derived from the emitter; the `NO AUDIT ROW` limit the relay inherits is no longer the one ADR-0047 recorded: ADR-0052 made the check exact for a notice that names its audit row. |
| [0049](0049-closing-an-account-is-authorised-like-a-transfer.md) | 2026-09-06 (the record's own day: pre-review, then the frontend follow-up `3c30122`) · 2026-09-07, ADR-0050 · 2026-09-11, ADR-0044 · 2026-09-14, ADR-0044 · 2026-09-21, ADR-0056 | The deposit race has its own SQL Server proof; both targets handle the mint; the `TimeProvider` deferral was wrong on its facts; a wrong or locked PIN at the mint is audited; the `AccountDeleted` row's `Detail` names the authorisation it consumed. The rail carries a second non-transfer operation, and the first whose binding names a real amount with no counterparty; the two-armed ternary this ADR added to `RecordPinRefusalAsync` became a switch, because a fourth member would have filed a refused withdrawal PIN as a refused transfer. |
| [0050](0050-a-utc-day-bounds-a-users-external-transfers-and-the-mint-says-so-before-the-pin.md) | 2026-09-07 (pre-review, then review round 1) · 2026-09-08 (the frontend mirror PR) · 2026-09-11 (a separate small PR; the client's locale, no PR named) · 2026-09-16, twice (the per-user lock; the SQL job's steps cited by name; no ADR or PR named) · *undated* — its own review · *undated* (no ADR or PR named) | The application lock takes a `@LockTimeout`; the After table's A4 was re-run as the both-bounds transfer; the four members are declared in the document and carried by the client; `available`/`requested` declared inline; the internal mint's bare 422 names `PIN_REQUIRED`; figures are formatted by the client in a fixed en-IE locale, not the user's; `SetPrimaryAccountAsync` now takes a per-user applock on its own resource, and the lock order is unchanged; the SQL job's steps are cited by name, not by `ci.yml` line, since a step added above them moved every line; `AddDailyLimit`'s `TryAdd` is not the clock's first registration; the mint's pre-PIN query is an unbounded cost, which the first draft denied. |
| [0051](0051-the-relay-runs-as-an-azure-function-against-azurite.md) | 2026-09-08 (pre-review) · 2026-09-09, three edits (the quantifier, the review's correction, and a same-day self-correction of it; no PR named) | "Every rule runner-guarded" → four; a missing `Notices:Schedule` no longer fails quiet — `ValidateTheScheduleItIsBoundTo()` runs unconditionally, and the runner-parameterised `ValidateTheScheduleItTicksOn` the correction first named was struck hours later; `UseMonitor` cleared so the timer cannot fire at start. |
| [0052](0052-a-notice-names-the-audit-row-it-belongs-to.md) | 2026-09-11 (residual, measured; no ADR or PR named) | A nulled `AuditEventId` is asked the old question, so for a repeatable kind another row of the same user answers for it. |
| [0053](0053-the-committed-contract-is-what-the-api-generates.md) | 2026-09-11, twice (no PR named) · 2026-09-14, twice (the XML-generator fix; no PR named) · 2026-09-15, twice (Schemathesis made a gate; no PR named) · 2026-09-21, ADR-0056 · 2026-09-28, ADR-0057 · 2026-10-04, ADR-0063 | The comma-decimal-locale defect withdrawn as false, measured through the real route; the "either defect fixed" trigger narrowed to the XML generator; the XML-generator defect fixed and "it is why D3's newlines are in the contract" struck, the trigger's prediction wrong: the newlines moved, so D3's normalisation stays; D6 moved: Schemathesis gates runtime conformance on every PR in `ci.yml`'s `conformance` job, and the "Gating Schemathesis" trigger struck as done. The conformance floor moved 27 → 28 with the withdrawal mint, 28 → 29 with ADR-0057's `POST /api/auth/revoke`, and 29 → 30 with its `POST /api/auth/session-stamps`; `CommittedOpenApiDocumentTests`' own floor stays at 20, deliberately below the real count so deleting an endpoint does not trip it. The conformance floor moved 30 → 31 with the demo's claim, `POST /api/auth/demo/claim`, which answers its declared 404 to every request of that job, the demo being off there. |
| [0054](0054-the-bff-serves-the-built-spa-under-a-csp-measured-against-it.md) | 2026-09-25 (no ADR or PR named) | HSTS is no longer left to the edge: the BFF sends it in every environment but Development, over http too, because `UseHsts` skips any request that is not https. |
| [0055](0055-the-api-serves-one-client-the-bff.md) | 2026-09-28, ADR-0057 · 2026-10-04, ADR-0063 | D4 extended: the token endpoints also want loopback and the BFF's marker, and a half-applied key rotation reaches the browser as a 503; D7's private network between two hosts struck, since the API is a loopback sidecar; the address allow-list rejection annotated, and the mutual-TLS rejection, whose pointer to D7 now lands on a struck clause; "the API reached over a network" added to what would change this. The token endpoints D4's note lists are six: the demo's claim joined them. |
| [0057](0057-the-bffs-refresh-token-is-one-reusable-grant-per-session.md) | 2026-09-30, ADR-0058 · 2026-10-01, ADR-0059 · 2026-10-04, ADR-0063 | §4.2: the token endpoints are six, the demo's claim among them, the first that opens a session from no credential, so it rests on that rule and on the loopback precondition alone; `/api/auth/demo/claim` joined the BFF's blocked proxied paths. §4.3: a tripwire's or an unknown grant's row that cannot reach the database answers 503, not 500, and refresh, revoke and logout run without the request deadline. §8: `BackendApi:TimeoutSeconds` is 55 s, not 100, and the proxy's activity timeout as well; the renewal's, the revoke's, `/me`'s, the stamp poll's and the health probe's shorter waits stay as they were. The SPA retries a read's 503 once, after the answer's `retryAfterSeconds` and within two minutes, not up to 3 attempts on RTK's own back-off. |
| [0058](0058-the-api-gives-up-cleanly-when-the-database-is-down.md) | 2026-10-01, ADR-0059 · 2026-10-01, ADR-0060 · 2026-10-01 (no ADR or PR named) · 2026-10-05, ADR-0064 | Consequences: the SPA retries a read's 503 once, after its `retryAfterSeconds`, not up to 3 attempts, and it reads `applied` and the retry wait. The second precondition is met: a deployment migrates through the Seeder's `migrate`, which has the limits, and the design-time factory applies them to the string it reads; the first says where `migrate` fits in the count. D7: `applied: true` exists, on the 409 `IDEMPOTENCY_RESULT_UNKNOWN` only and never on a 503, and the document declares `applied` on the four money 409s too, so "on the four money 503s only" is struck. The first precondition's second job at 5 is written, the pool job of the Azure deployment: a deployment does not start beside a run of it, and a run the schedule starts after that one read makes 12 + 5 + 5 beside the migration and 2 × 12 + 5 = 29 while a revision is replaced. |
| [0059](0059-the-spa-tells-the-visitor-when-the-service-is-slow-or-down.md) | 2026-10-01 (no ADR or PR named) | Decision 3: `applied` is also read as `=== true`, on a money send's 409 `IDEMPOTENCY_RESULT_UNKNOWN`. The residual "`RESULT_UNKNOWN` also covers a commit that is proven" is struck as closed for the stale claim, with what is not closed: the first two minutes, answered `IN_FLIGHT`. Decision 9: a 409 on a money send that names no code latches the check too (ADR-0022 decision 3's note). Validation gains the went-through view's tests and measurements; the four runs of the lost-answer sequence on the compose stack, with the double click that closed the deposit dialog and the one that left focus on the dialog, and the fix for each; the double click that closed the check view, measured in a run of its own whose 409 the browser answered, since every lost-answer run got `applied: true` and drew no check view, and its fix; what NVDA said on the went-through view in the four flows, the heading of the two transfer pages, which it did not say, among it; and what was not heard. |
| [0060](0060-migrations-run-as-a-one-shot-container-before-the-app.md) | 2026-10-03, ADR-0062 · 2026-10-05, ADR-0064 | Decision 5: `seed-pool` and `recycle` run on an Azure SQL name too, so "only `migrate`" no longer holds; the note says why that is safe where `seed` and `reset` are not, how the job runs, and that where the app has a database user apart from the migration's it signs in as the app's, with the measurement. Decision 4: two exit 2s come after a connection was opened, `seed` and `reset` refusing a database that holds the demo pool's rows; a value that cannot be read as its type is a 2, not a 1; the pool's two commands add the codes 10 to 15. Decision 5's note: the job it says does not exist yet on Azure is in the template, behind a switch that is off by default; it runs `recycle` for the first fill too and is given no key id. On Azure it still does not exist. |
| [0061](0061-the-demo-is-deployed-to-azure-container-apps-with-no-database-password.md) | 2026-10-03 (the first deployment's second session; no ADR or PR named) · 2026-10-05, ADR-0064 | Decision 10: three alerts, not four. The rule on the log workspace read a metric that had no time series for an hour in which the workspace ingested 446 rows; it was deleted and is built only when it is asked for, and decision 1 counts eight things with the app, not nine. Struck as measured: whether a change that touches no identity needs a right on the attached one (it does not), that the alert on the workspace is accepted and counts lines (accepted, and it does not), and in Validation what the second session showed: each new revision ready with the smoke test answered through both containers, the app scaled to zero at the end of the day, the deployment identity changing an app and a job that carry an identity (not every one of its nine actions was exercised), the policy refusing a second replica. "In the second session" is struck from the sentence on the app's first sign-in: that session timed no cold start. With ADR-0064, none of it run on Azure: decision 1, a switch adds the pool job and a third role assignment to the second step, and "one of the seven secrets" is struck for eight; decision 2, "a job that is not manual" is struck for the one exception by name; decision 7, a deployment reads whether the demo is on, and with it on moves a second job and asks the address two things more; decision 8, the role is assigned a third time, on the pool job; decision 9, "the seven application secrets exist only as secrets of the app" is struck: eight, and the pepper is also a secret of the pool job. Validation: 444 tests, and a compiled template of 23 resources and 23 parameters. |
| [0062](0062-demo-visitors-get-private-copies-from-a-prepared-pool.md) | 2026-10-03, ADR-0061 · 2026-10-04, ADR-0063 · 2026-10-05, ADR-0064 | Consequences: "none of ADR-0061's four alerts" struck for three, since the alert on the log volume was removed the same day; no alert watches the database's size, as before. Decision 2: what `ClaimId`, `ClientKey` and `Writes` are for is decided, and `Writes` counts requests that could change something, refused ones and the reveal included, not only "requests that changed something". Decision 3: "only the Seeder binds the section" struck, since the API and the BFF bind it and run its validator, and the API asks for `Demo:ClientKeySecret` with the demo on. Decision 13: `compose.demo.yaml` runs the API and the BFF with the demo on and asks for a ninth variable; until then the app under it had its registration open. "Not measured: a copy used through the front door" struck: measured twice on the compose stack. Decision 13, with ADR-0064: "until `Schedule` is in `allowedJobTriggers`" struck, since the policy allows the pool job by its name in a parameter of its own; the job is in the template, and on Azure no job runs the commands yet. "A user who registers before the first `seed-pool` makes it exit 13" struck: on a database that only `migrate` has touched a registration answers 500 and leaves no user and no role, measured on a local stack, so nobody registers there before the first fill; and "whether registration closes before the first fill" struck as answered: it does. |
| [0063](0063-a-visitor-claims-a-prepared-copy-instead-of-registering.md) | 2026-10-05 (no ADR or PR named) · 2026-10-05, ADR-0064 | Consequences, "Neutral": the doubled slash in front of a server path that answered the page shell is struck. A GET or a HEAD of a server path behind extra slashes is 404 with no body, with the demo off as with it on. Decision 14, with ADR-0064: the change that turns the demo on is written, in `infra/main.bicep` behind a switch that is off by default; on Azure the demo is still off. |

</details>

The next free number is **0065**.

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
| [ADR-0021](0021-refresh-token-rotation-bff-remint.md) | Refresh-Token Rotation with Reuse-Detection (+ BFF Silent Re-Mint) — rotation superseded by ADR-0057 | Accepted | 2026-07-22 |
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
| [ADR-0034](0034-failed-family-revoke-recovery.md) | Recovery for a family revoke that fails — superseded by ADR-0057 | Accepted | 2026-08-08 |
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
| [ADR-0055](0055-the-api-serves-one-client-the-bff.md) | The API serves one client, the BFF | Accepted | 2026-09-19 |
| [ADR-0056](0056-a-withdrawal-is-authorised-like-a-transfer.md) | A withdrawal is authorised like a transfer | Accepted | 2026-09-21 |
| [ADR-0057](0057-the-bffs-refresh-token-is-one-reusable-grant-per-session.md) | The BFF's refresh token is one reusable grant per session | Accepted | 2026-09-28 |
| [ADR-0058](0058-the-api-gives-up-cleanly-when-the-database-is-down.md) | The API gives up cleanly when the database is down | Accepted | 2026-09-29 |
| [ADR-0059](0059-the-spa-tells-the-visitor-when-the-service-is-slow-or-down.md) | The SPA tells the visitor when the service is slow or down | Accepted | 2026-10-01 |
| [ADR-0060](0060-migrations-run-as-a-one-shot-container-before-the-app.md) | Migrations run as a one-shot container before the app | Accepted | 2026-10-01 |
| [ADR-0061](0061-the-demo-is-deployed-to-azure-container-apps-with-no-database-password.md) | The demo is deployed to Azure Container Apps with no database password | Accepted | 2026-10-02 |
| [ADR-0062](0062-demo-visitors-get-private-copies-from-a-prepared-pool.md) | Demo visitors get private copies from a prepared pool | Accepted | 2026-10-03 |
| [ADR-0063](0063-a-visitor-claims-a-prepared-copy-instead-of-registering.md) | A visitor claims a prepared copy instead of registering | Accepted | 2026-10-04 |
| [ADR-0064](0064-the-azure-deployment-runs-the-demo-from-a-scheduled-pool-job.md) | The Azure deployment runs the demo from a scheduled pool job | Accepted | 2026-10-05 |

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
