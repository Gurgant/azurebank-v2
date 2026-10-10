# Architecture Decision Records

This directory contains Architecture Decision Records (ADRs) for the AzureBank project.

## What is an ADR?

An ADR is a document that captures an important architectural decision made along with its context
and consequences. Here a record is one page: what is decided and why, as it stands today.

## When something earns an ADR

An ADR requires a decision that is **binding on future work**, **not already recorded** in another
ADR, and **not recoverable by reading** the code, the types, the tests or the CI gates. If a
competent reader could recover the rule by opening the repository, writing it down here creates a
second copy that will eventually go stale and start lying — which is worse than never having
written it.

Preferences, task lists, status reports and deferrals are not ADRs. The best candidates are the
ones with nothing to read: a **rejection** (no code exists for the thing that was decided against),
a constraint on something **outside the repository**, or a hard-won **negative finding** whose only
alternative record is running the same experiment again.

## If you read four, read these

Sixty-four decisions is more than anyone reads cold. These four carry the architecture; the rest
is detail hanging off them.

| | Why this one |
|---|---|
| **[ADR-0001](0001-bff-pattern.md) — BFF pattern** | The decision everything else inherits: the JWT never reaches the browser, so the whole auth story follows from here. |
| **[ADR-0009](0009-idempotency-monetary-operations.md) — Idempotent monetary operations** | Where this stops being a CRUD app. A keyed HMAC over the raw request body, a record that proves whether the money committed, and a deliberate correctness-over-availability trade. |
| **[ADR-0019](0019-spa-bff-integration.md) — SPA/BFF integration** | Cookie auth and one error channel: the contract the entire frontend is written against. |
| **[ADR-0022](0022-client-money-mutation-protocol.md) — Client money-mutation protocol** | The client half of ADR-0009: which outcomes keep an idempotency key and which spend it. One wrong answer moves a customer's money twice. |

## By theme

One line per record: number, title, status. Every record is Accepted; ADR-0021 and ADR-0034 are
superseded in part by ADR-0057, as their lines say. The four above are in bold.

**Platform and topology** — where each part runs, and who may call whom.

- **[ADR-0001](0001-bff-pattern.md)** · Backend-For-Frontend (BFF) Pattern · Accepted
- [ADR-0002](0002-yarp-proxy.md) · YARP Reverse Proxy Selection · Accepted
- [ADR-0018](0018-bff-origin-hardening.md) · BFF origin hardening — __Host- session cookie, Fetch-Metadata, no CORS · Accepted
- **[ADR-0019](0019-spa-bff-integration.md)** · SPA–BFF integration architecture for the frontend build-out · Accepted
- [ADR-0039](0039-bff-session-cache-is-a-fallback.md) · The BFF session cache is a fallback, never the answer · Accepted
- [ADR-0054](0054-the-bff-serves-the-built-spa-under-a-csp-measured-against-it.md) · The BFF serves the built SPA under a CSP measured against it · Accepted
- [ADR-0055](0055-the-api-serves-one-client-the-bff.md) · The API serves one client, the BFF · Accepted
- [ADR-0058](0058-the-api-gives-up-cleanly-when-the-database-is-down.md) · The API gives up cleanly when the database is down · Accepted
- [ADR-0060](0060-migrations-run-as-a-one-shot-container-before-the-app.md) · Migrations run as a one-shot container before the app · Accepted
- [ADR-0061](0061-the-demo-is-deployed-to-azure-container-apps-with-no-database-password.md) · The demo is deployed to Azure Container Apps with no database password · Accepted

**Money** — how a money move is applied once, bounded and numbered.

- **[ADR-0009](0009-idempotency-monetary-operations.md)** · Idempotency for Monetary Operations · Accepted
- **[ADR-0022](0022-client-money-mutation-protocol.md)** · Client-side money-mutation protocol · Accepted
- [ADR-0024](0024-no-client-facing-optimistic-concurrency.md) · No client-facing optimistic concurrency — ETag/If-Match rejected · Accepted
- [ADR-0028](0028-data-router-for-blocking-browser-back.md) · A data router, bought for one hook — blocking browser Back on a live idempotency key · Accepted
- [ADR-0035](0035-transaction-number-check-symbol.md) · A check symbol on the transaction number · Accepted
- [ADR-0036](0036-account-number-collision-recovery.md) · Recovering from an account-number collision · Accepted
- [ADR-0046](0046-one-money-cap-for-every-move-and-the-client-promises-what-the-contract-publishes.md) · One money cap for every move, and the client promises what the contract publishes · Accepted
- [ADR-0050](0050-a-utc-day-bounds-a-users-external-transfers-and-the-mint-says-so-before-the-pin.md) · A UTC day bounds a user's external transfers, and the mint says so before the PIN · Accepted

**Interface** — the theme, a page that fails, and a service that is slow or down.

- [ADR-0027](0027-dark-mode-through-css-custom-properties.md) · Dark mode through CSS custom properties, decided before the first paint · Accepted
- [ADR-0033](0033-root-error-boundary.md) · A root error boundary, so a render error is not a blank page · Accepted
- [ADR-0059](0059-the-spa-tells-the-visitor-when-the-service-is-slow-or-down.md) · The SPA tells the visitor when the service is slow or down · Accepted

**Authentication and account safety** — passwords, sessions, the PIN and the authorisation it buys.

- [ADR-0003](0003-argon2id-password-hashing.md) · Argon2id Password Hashing · Accepted (Argon2id hashes the PIN; passwords use Identity's PBKDF2)
- [ADR-0008](0008-step-up-authentication.md) · Step-Up Authentication with PIN · Accepted
- [ADR-0010](0010-pin-attempt-limiting.md) · API-side PIN attempt-limiting (lockout) · Accepted
- [ADR-0011](0011-pin-hash-pepper.md) · PIN-hash pepper (keyed hashing) with self-describing rehash-on-use · Accepted
- [ADR-0012](0012-login-attempt-limiting.md) · Password/login attempt-limiting (account lockout) · Accepted
- [ADR-0021](0021-refresh-token-rotation-bff-remint.md) · Refresh-token rotation with reuse-detection (+ BFF silent re-mint) · Accepted; decisions 3, 5 and 6 superseded by ADR-0057
- [ADR-0026](0026-absolute-session-cap-reauthentication.md) · The absolute session cap is re-authenticated, never extended · Accepted
- [ADR-0034](0034-failed-family-revoke-recovery.md) · Recovery for a family revoke that fails · Accepted; decision 1 superseded by ADR-0057
- [ADR-0037](0037-atomic-registration.md) · Registration is all-or-nothing · Accepted
- [ADR-0038](0038-bff-session-is-the-only-credential.md) · The session is the only credential the BFF will accept · Accepted
- [ADR-0040](0040-changing-a-credential-requires-the-current-one.md) · Changing a credential requires proving the current one · Accepted
- [ADR-0041](0041-the-api-verifies-the-transfer-pin.md) · The API verifies the transfer PIN, not the BFF · Accepted
- [ADR-0042](0042-a-transfer-authorisation-is-bound-and-spent-once.md) · A transfer authorisation is bound to its amount and payee, and spent once · Accepted
- [ADR-0049](0049-closing-an-account-is-authorised-like-a-transfer.md) · Closing an account is authorised like a transfer · Accepted
- [ADR-0056](0056-a-withdrawal-is-authorised-like-a-transfer.md) · A withdrawal is authorised like a transfer · Accepted
- [ADR-0057](0057-the-bffs-refresh-token-is-one-reusable-grant-per-session.md) · The BFF's refresh token is one reusable grant per session · Accepted

**Not leaking who exists** — what a stranger can learn about other users.

- [ADR-0013](0013-registration-user-enumeration.md) · Registration user-enumeration — bounded, documented acceptance · Accepted
- [ADR-0014](0014-recipient-lookup-enumeration.md) · Recipient lookup — exact-match, harvest-resistant · Accepted
- [ADR-0015](0015-decouple-username-renameable-handle.md) · Decouple Identity UserName from the AzureTag; make the handle renameable · Accepted
- [ADR-0020](0020-account-number-reveal.md) · On-demand account-number reveal (masked-by-default + PIN-gated `/full-number`) · Accepted

**Contract and correctness** — how frontend, backend and the published contract are held together.

- [ADR-0005](0005-scalar-api-documentation.md) · Scalar API Documentation · Accepted
- [ADR-0007](0007-fluentvalidation.md) · FluentValidation for Request Validation · Accepted
- [ADR-0023](0023-runtime-response-validation.md) · Runtime response validation — spec-generated Zod, fail-closed on money · Accepted
- [ADR-0029](0029-contract-conformance-gate.md) · One suite, two backends — making mock drift fail the build · Accepted
- [ADR-0030](0030-real-backend-integration-layer.md) · Running the app's own data layer against the real backend · Accepted
- [ADR-0031](0031-e2e-playwright.md) · The app in a real browser, against the real stack · Accepted
- [ADR-0032](0032-real-stack-layers-in-ci.md) · Running the real-backend layers in CI, on a stack the job owns · Accepted
- [ADR-0043](0043-the-document-declares-the-error-body.md) · The published document declares the error body the API actually sends · Accepted
- [ADR-0053](0053-the-committed-contract-is-what-the-api-generates.md) · The committed contract is what the API generates · Accepted

**Audit trail and owed notices** — what is recorded, how it is checked, and what a user is told.

- [ADR-0044](0044-the-audit-trail-is-append-only-and-chained.md) · Security events go to an append-only, hash-chained table · Accepted
- [ADR-0045](0045-the-enrolment-notice-rides-the-enrolment-and-stops-at-a-pickup-directory.md) · The enrolment notice rides the enrolment, and stops at a pickup directory · Accepted
- [ADR-0047](0047-a-pin-change-owes-the-same-notice-an-enrolment-does.md) · A PIN change owes the same notice an enrolment does · Accepted
- [ADR-0048](0048-the-api-is-the-runner-that-delivers-owed-notices.md) · The API is the runner that delivers owed notices · Accepted
- [ADR-0051](0051-the-relay-runs-as-an-azure-function-against-azurite.md) · The relay runs as an Azure Function, rehearsed locally against Azurite · Accepted
- [ADR-0052](0052-a-notice-names-the-audit-row-it-belongs-to.md) · A notice names the audit row it belongs to · Accepted

**The public demo** — how each visitor gets a demo of their own, and how it ends.

- [ADR-0062](0062-demo-visitors-get-private-copies-from-a-prepared-pool.md) · Demo visitors get private copies from a prepared pool · Accepted
- [ADR-0063](0063-a-visitor-claims-a-prepared-copy-instead-of-registering.md) · A visitor claims a prepared copy instead of registering · Accepted
- [ADR-0064](0064-the-azure-deployment-runs-the-demo-from-a-scheduled-pool-job.md) · The Azure deployment runs the demo from a scheduled pool job · Accepted

**Operations** — telemetry, and keeping personal data out of it.

- [ADR-0016](0016-observability-three-pillars.md) · Observability — OpenTelemetry three pillars with a local Grafana LGTM stack · Accepted
- [ADR-0017](0017-pii-redaction-codeql-barrier.md) · PII-safe telemetry and the CodeQL log-forging barrier · Accepted

**Build and tooling** — packages, mapping, and what the earlier code is for.

- [ADR-0004](0004-central-package-management.md) · Central Package Management (CPM) · Accepted
- [ADR-0006](0006-mapperly-object-mapping.md) · Mapperly Object Mapping · Accepted
- [ADR-0025](0025-originals-reference-mine.md) · The originals are a reference mine, not a code source · Accepted

## Correcting a document

A record says what is true now. When a decision changes, its text is rewritten and its Status line
gains the date; the earlier wording is in the git history. The rule for every document is in
[`engineering-practices.md`](../engineering-practices.md#correcting-a-document).

A record's file name and number, and the numbers and labels of its decisions, never change: code
and other pages cite them. A withdrawn item stays under its label and names its replacement.

## Creating a New ADR

1. Copy [`0000-template.md`](0000-template.md) to a new file with the next free number, **0065**.
2. Replace each line of guidance, and keep the record to one page.
3. Add its line to this index, under its theme in [By theme](#by-theme).
4. Submit it as part of the pull request.

## ADR Lifecycle

- **Proposed**: Under discussion
- **Accepted**: Decision made and implemented
- **Deprecated**: No longer applies
- **Superseded**: Replaced by another ADR, in whole or in part; the record stays and names it

## References

- [ADR GitHub Organization](https://adr.github.io/)
- [MADR Template](https://adr.github.io/madr/)
