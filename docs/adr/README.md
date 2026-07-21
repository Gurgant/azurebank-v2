# Architecture Decision Records

This directory contains Architecture Decision Records (ADRs) for the AzureBank project.

## What is an ADR?

An ADR is a document that captures an important architectural decision made along with its context and consequences.

## ADR Index

| ID | Title | Status | Date |
|----|-------|--------|------|
| [ADR-0000](0000-template.md) | ADR Template | Template | - |
| [ADR-0001](0001-bff-pattern.md) | BFF Pattern | Accepted | 2026-01-10 |
| [ADR-0002](0002-yarp-proxy.md) | YARP Reverse Proxy | Accepted | 2026-01-10 |
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
| [ADR-0018](0018-bff-origin-hardening.md) | BFF Origin Hardening (__Host- Cookie, Fetch-Metadata, No CORS) | Accepted | 2026-07-20 |
| [ADR-0019](0019-spa-bff-integration.md) | SPA–BFF Integration Architecture (Cookie Auth, One Error Channel) | Accepted | 2026-07-20 |
| [ADR-0020](0020-account-number-reveal.md) | On-Demand Account-Number Reveal (Masked-by-Default + PIN-Gated) | Accepted | 2026-07-21 |

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
