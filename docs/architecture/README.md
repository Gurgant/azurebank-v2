# Architecture

[`overview.md`](overview.md) is current: how AzureBank works, kept in step with the code.

Every document in the table below is history: plans, research and audits that came into the
repository with the design history on 2026-07-12. Each carries a banner saying so. None of it is
instruction; where it disagrees with the code or with
[`docs/api/openapiv1.json`](../api/openapiv1.json), those win.

| Document | What it was |
|---|---|
| [`BFF-COMPLETION-PLAN.md`](BFF-COMPLETION-PLAN.md) | The plan for finishing the BFF, 2026-01-21 |
| [`BFF-RESEARCH-ALTERNATIVES.md`](BFF-RESEARCH-ALTERNATIVES.md) | Research into the BFF pattern and simpler alternatives |
| [`DOCKER-VS-ASPIRE-BFF-MODES.md`](DOCKER-VS-ASPIRE-BFF-MODES.md) | Docker Compose against .NET Aspire, for running with or without the BFF — the second of which ADR-0055 has since ruled out: the API serves only the BFF |
| [`AZURE-VNET-BFF-API-ISOLATION.md`](AZURE-VNET-BFF-API-ISOLATION.md) | A guide to deploying on Azure with the BFF public and the API reachable only from it, in a VNET |
| [`UI-UX-EVALUATION-MASTER-PLAN.md`](UI-UX-EVALUATION-MASTER-PLAN.md) | A UI/UX evaluation plan, 2026-01-30 |
| [`audits/dto-architecture-audit.md`](audits/dto-architecture-audit.md) | An audit of the DTOs and a migration plan, 2026-01-26 |
| [`future-implementations/refresh-token-implementation.md`](future-implementations/refresh-token-implementation.md) | The plan for refresh tokens, written before they were built |

`MINIMAL-TOKEN-API-AUDIT-PLAN.md`, a working plan for a separate task that this repository never
carried out, was moved out on 2026-09-24.
