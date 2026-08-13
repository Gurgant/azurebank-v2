# The OpenAPI document

`openapiv1.json` is the contract. It is **generated from the API**, committed, and then everything
downstream is generated from it — the frontend's `schema.d.ts` (`openapi-typescript`) and its runtime
Zod validators (`typed-openapi`), both of which CI regenerates and compares on every pull request.

## Regenerating it

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
spec's 24 operations carries a summary.

**It does not compare schemas, parameters, examples, tags or security.** A change confined to those
is reported as "the difference is elsewhere" rather than passed off as nothing — the fallback
message states the scope, so the report never claims more than it checked.

## What this does NOT do

**It is not wired into CI, so a stale committed spec still passes every gate.** The existing gate
regenerates the frontend artifacts *from* this file and compares them — it proves the generated code
matches the document, never that the document matches the server. Closing that loop means running
this `check` against a live API in the pipeline, which is a separate decision recorded in the
working repo's backlog. This script is what such a job would call; it is worth having before then,
because it turns "run these steps in this order and do not use a JSON formatter" into one command
that fails loudly when any of it is wrong.
