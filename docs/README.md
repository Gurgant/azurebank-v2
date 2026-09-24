# Documentation

What is current here, and what is kept only as history. When two documents disagree, or a document
disagrees with the code, **the code and the generated contract win**:
[`api/openapiv1.json`](api/openapiv1.json) is written by the API itself and compared against it by
the backend test suite (ADR-0053), so it is the one description that cannot drift from the code.

## Current

| Where | What |
|---|---|
| [`adr/`](adr/README.md) | The decision records: why the system is the way it is |
| [`api/`](api/README.md) | The generated OpenAPI contract, and how to read it |
| [`architecture/overview.md`](architecture/overview.md) | How AzureBank works |
| [`runbooks/`](runbooks/) | What to do when the audit chain is unavailable, or a PIN enrolment is repudiated |
| [`deferred/`](deferred/README.md) | What was understood well enough to build and chosen not to, and why |
| [`audit/`](audit/README.md) | A sample of the audit trail's exported anchor copy |
| [`notices/`](notices/README.md) | The notices a PIN enrolment or change sends, as rendered |
| [`audit-trail-against-real-practice.md`](audit-trail-against-real-practice.md) | The audit trail held against published practice |
| [`engineering-practices.md`](engineering-practices.md) | How this project is built and kept correct |
| [`engineering-traps.md`](engineering-traps.md) | Things that fail silently or ship green, and how each was caught |
| [`brand-assets.md`](brand-assets.md) | Where every icon comes from |

## History

| Where | What |
|---|---|
| [`design/`](design/README.md) | The design corpus from before and during the January 2026 build, and a map of it from July |
| [`architecture/`](architecture/README.md), all but `overview.md` | Plans, research and audits that came in with it |

The two folders' own READMEs are current: they map the history, and are not part of it. A history
document is kept as it was written, for provenance, and says so in a banner at its top. None of it
is instruction.
