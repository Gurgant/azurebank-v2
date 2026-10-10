# The OpenAPI document

`openapiv1.json` is the contract. It is **generated from the API**, committed, and never edited by
hand. The frontend's `schema.d.ts` (`openapi-typescript`) and its runtime Zod validators
(`typed-openapi`) are generated from it, and CI regenerates and compares both on every pull request.
The document itself is gated too (ADR-0053): `CommittedOpenApiDocumentTests` fails, on every test
run, when this file is not byte for byte what the API generates, line endings and the newlines
inside strings excepted.

## Regenerating it

**From the test, with no running API and no secrets**: the usual route. From `backend/`:

```bash
AZUREBANK_REGENERATE_OPENAPI=1 dotnet test --filter "FullyQualifiedName~CommittedOpenApiDocumentTests"
```

It writes this file and then fails on purpose, so that the flag can only make a build red. Run it
again without the flag to confirm, then regenerate the frontend artefacts (step 3 below).

**Or against a running API, with the script**, to check a server that is up or to read its report.
The API needs its secrets to start ([local setup](../engineering-practices.md#local-setup)).

```bash
# 1. Start the API. The document is only mapped in Development.
cd backend/src/AzureBank.Api
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5068 dotnet run --no-launch-profile

# 2. From the repo root, once /health/ready answers 200:
node scripts/openapi-spec.mjs check    # is the committed document what the API serves?
node scripts/openapi-spec.mjs regen    # make it so

# 3. If the document changed, the frontend artifacts must follow or CI fails on the drift gate:
cd frontend && npm run generate:api && npm run generate:zod
```

`OPENAPI_BASE_URL` overrides the address. Port 5068 is the plain-HTTP port CI's real-stack job uses.

## What the script does, and what goes wrong without it

- **`MapOpenApi()` is registered only in Development.** Started any other way, the API answers
  `/openapi/v1.json` with a 404 that reads like a routing bug.
- **A round trip through a JSON parser reformats the whole document**, and a real change then hides
  in a whole-file diff. The script writes the served text through, untouched apart from line
  endings.
- **Line endings.** The API serves LF, and a Windows checkout with `core.autocrlf=true` holds the
  same document as CRLF: a raw byte comparison calls that drift. The script normalises both sides
  before it compares, and `regen` writes LF.
- **`check` reports the prose and compares everything.** When the document has moved it exits 1 and
  lists each operation summary, operation description and response description that was added,
  removed or reworded: a regeneration that drops something a person wrote hides in a textual diff of
  more than five thousand lines. A change confined to schemas, parameters, examples, tags or
  security still fails the check, and is reported only as "The difference is elsewhere".

## What this does NOT do

- **The script is not wired into CI, and that is not a gap.** A stale committed document fails the
  backend suite (ADR-0053 D1). `check` needs a running Development API with every secret and names
  no JSON path. The test needs neither, and names the first paths that differ.
- **Neither the test nor the script proves that the document tells the truth.** Both compare the
  committed file with what the API generates, so if a transformer describes a response the server
  can never send, both agree with it. Only a call to the running API can disagree: Schemathesis
  does, on every pull request, as the `conformance` job in `ci.yml` (ADR-0053 D6,
  [`tests/contract/`](../../tests/contract/README.md)).
