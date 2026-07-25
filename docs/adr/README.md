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

Twenty-five records is more than anyone reads cold. These four carry the architecture; the rest is
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
[0019](0019-spa-bff-integration.md) SPA/BFF integration

**Money** — [0009](0009-idempotency-monetary-operations.md) idempotent monetary operations (server) ·
[0022](0022-client-money-mutation-protocol.md) client money-mutation protocol ·
[0024](0024-no-client-facing-optimistic-concurrency.md) no client-facing optimistic concurrency

**Authentication and account safety** — [0003](0003-argon2id-password-hashing.md) Argon2id password hashing ·
[0008](0008-step-up-authentication.md) step-up authentication ·
[0010](0010-pin-attempt-limiting.md) PIN attempt-limiting ·
[0011](0011-pin-hash-pepper.md) PIN-hash pepper ·
[0012](0012-login-attempt-limiting.md) login attempt-limiting ·
[0021](0021-refresh-token-rotation-bff-remint.md) refresh-token rotation with reuse detection

**Not leaking who exists** — [0013](0013-registration-user-enumeration.md) registration enumeration ·
[0014](0014-recipient-lookup-enumeration.md) recipient lookup, exact-match and harvest-resistant ·
[0015](0015-decouple-username-renameable-handle.md) decoupling the username from a renameable handle ·
[0020](0020-account-number-reveal.md) on-demand account-number reveal

**Contract and correctness** — [0007](0007-fluentvalidation.md) FluentValidation ·
[0023](0023-runtime-response-validation.md) runtime response validation ·
[0005](0005-scalar-api-documentation.md) Scalar API documentation

**Operations** — [0016](0016-observability-three-pillars.md) observability, three pillars ·
[0017](0017-pii-redaction-codeql-barrier.md) PII-safe telemetry and the log-forging barrier

**Build and tooling** — [0004](0004-central-package-management.md) central package management ·
[0006](0006-mapperly-object-mapping.md) Mapperly object mapping ·
[0025](0025-originals-reference-mine.md) the originals are a reference mine

All twenty-five are **Accepted and shipped** — nothing here is aspirational, which is why there is
no Proposed tier. Where a later record changes an earlier one, the earlier keeps an inline
supersession note at the affected clause rather than being rewritten: ADR-0019's Decision 6 points
at ADR-0023, and ADR-0009 points at ADR-0022 for its client half. The next free number is **0026**.

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
