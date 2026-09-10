# The OpenAPI document

`openapiv1.json` is the contract. It is **generated from the API**, committed, and then everything
downstream is generated from it — the frontend's `schema.d.ts` (`openapi-typescript`) and its runtime
Zod validators (`typed-openapi`), both of which CI regenerates and compares on every pull request.
**The document itself is gated too, since ADR-0053:** `CommittedOpenApiDocumentTests` fails whenever
this file is not byte-for-byte what the API generates, on every test run, local and CI alike.

## Regenerating it

**From the test, with no running API and no secrets** — the usual route. From `backend/`:

```bash
AZUREBANK_REGENERATE_OPENAPI=1 dotnet test --filter "FullyQualifiedName~CommittedOpenApiDocumentTests"
```

It writes this file and then fails on purpose, so the flag can only ever make a build red. Re-run
without it to confirm, then regenerate the frontend artefacts (step 3 below). The test host generates
the document through `IOpenApiDocumentProvider` from the same composition the API uses, and on
2026-09-10 that document, the one the API serves over HTTP, and this file were byte-identical.

**Or against a running API, with the script** — when you want to check a server that is actually up,
or read the prose report below:

```bash
# 1. Start the API. The document is only mapped in Development — see below.
cd backend/src/AzureBank.Api
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5068 dotnet run --no-launch-profile

# 2. From the repo root, once /health/ready answers 200 (about six seconds on a warm build):
node scripts/openapi-spec.mjs check    # is the committed document what the API serves?
node scripts/openapi-spec.mjs regen    # make it so

# 3. If regen changed anything, the frontend artifacts must follow or CI fails on the drift gate:
cd frontend && npm run generate:api && npm run generate:zod
```

`OPENAPI_BASE_URL` overrides the address. Port 5068 is the plain-HTTP port CI's real-stack job uses.

## Why a script rather than a paragraph

Until it existed, this procedure lived in one commit message, and every part of it had a way to go
wrong quietly:

- **`MapOpenApi()` is registered only in Development.** Start the API any other way and
  `/openapi/v1.json` is a 404 that reads like a routing bug.
- **Round-tripping the document through a JSON parser reformats all 106 KB of it.** Indentation, key
  escaping, the trailing newline — a real change then hides inside a whole-file diff. The script
  passes the served text through untouched apart from line endings.
- **Line endings.** The API serves LF; a Windows checkout with `core.autocrlf=true` holds the same
  document as CRLF. Measured on 2026-08-13: 109,944 bytes on disk against 106,506 served, 3,438 line
  endings differing, not one byte of content. A raw byte comparison calls that drift; a text-mode
  read in a language that silently normalises newlines calls it a byte match. Neither is true, and
  the script normalises both sides before comparing.

## What `check` reports, and why it is not a diff

When the document has moved, `check` prints a **semantic** report of every piece of hand-written
prose — operation summaries, operation descriptions and response descriptions, each added, removed
or reworded. A path or an operation appearing or disappearing shows up as all of its entries doing
so:

```text
  ~ CHANGED  GET /api/accounts 200
      was: ""
      now: "List of user's accounts"

  - REMOVED  GET /api/accounts [summary]  ("Get all accounts for the current user")
```

The failure worth catching is a regeneration that silently drops something a human wrote, and a
three-thousand-line textual diff hides that perfectly. This is not hypothetical: every one of the
spec's operations carries a summary — 27 of 27 on 2026-09-10 (this said "24" in the present tense
and the count had moved).

**It does not compare schemas, parameters, examples, tags or security.** A change confined to those
is reported as "the difference is elsewhere" rather than passed off as nothing — the fallback
message states the scope, so the report never claims more than it checked.

## What this does NOT do

**The script is still not wired into CI — and that is no longer the gap.** This section used to say
that a stale committed spec passed every gate, because the only gate regenerated the frontend
artifacts *from* this file. Since ADR-0053 a stale spec fails the backend suite instead: the test
above compares the whole document, schemas included, which this script's `check` deliberately does
not. The route this section anticipated — running `check` against a live API in the pipeline — was
declined for that reason; ADR-0053 D1 has the argument.

**Neither the test nor the script proves the document tells the TRUTH.** Both compare the committed
file with what the API generates. If a transformer generates a response the server can never send,
both agree with it, because both read the same generator. That is a claim about runtime behaviour,
and only a call to the running API can disagree with it — Schemathesis, which still reports without
gating (`docs/engineering-traps.md` has a case where that is exactly what happened).
