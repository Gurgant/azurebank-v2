# Contract Testing with Schemathesis

## Overview

This folder contains configuration for **Schemathesis**, an automatic API testing tool that generates comprehensive test suites from OpenAPI specifications.

## Prerequisites

```bash
# Pin the version CI pins. Everything on this page was measured against it, and v4 renamed
# or removed enough flags that an unpinned install documents a different tool:
# SCHEMATHESIS_VERSION in .github/workflows/ci.yml.
pip install 'schemathesis==4.27.1'

# Or use Docker (no Python needed). `stable` is NOT pinned and can drift past 4.27.1.
docker pull schemathesis/schemathesis:stable
```

Whichever you use, run it on the machine the API listens on. The six token endpoints — login,
register, refresh, revoke, logout and the public demo's claim — and the session-stamp feed answer
404 to a caller whose address is not loopback, and to one that does not send exactly one
`X-AzureBank-Token-Road` (`TokenRoadMiddleware`). The document declares no 404 on six of the
seven, so such a run fails rather than skipping them. The seventh, the demo's claim, declares
one: it answers 404 to every request that carries the service key while `Demo:Enabled` is false,
which is how this page and CI run the API (ADR-0063; measured below). *(Until 2026-10-04 this said five token endpoints, and
that none of the six operations declared a 404.)* Measured on 2026-09-28 from loopback with the hooks as they were before the
marker, which the API refuses with the same 404: run on the four anonymous operations, login,
register, refresh and revoke each failed `Undocumented HTTP status code` (`4 failures`); run on an
operation that needs a token, with none handed over, it stopped at the throwaway user's registration
(*Troubleshooting*, below). From a container, whether the API sees loopback depends on how the
container reaches it; not measured.

## Quick Start

### 1. Start the API

```bash
cd backend/src/AzureBank.Api
dotnet run
```

### 2. Run the tests

```bash
# From the repository ROOT: the configuration names its hooks module from there.
# AZUREBANK_SERVICE_KEY holds the value of the API's ServiceCredential:BffKey -- $SERVICE_KEY,
# if you followed the local setup's recipe in docs/engineering-practices.md (until 2026-09-25
# this said the root README's). The hooks read it from the environment, so it is
# never on a command line.
export AZUREBANK_SERVICE_KEY="$SERVICE_KEY"
schemathesis --config-file tests/contract/schemathesis.toml run docs/api/openapiv1.json
```

`--config-file` is a GLOBAL option, so it goes BEFORE `run`: after it, 4.27.1 answers
`Usage: schemathesis run [OPTIONS] LOCATION`.

What the two files add to that line:

- **`hooks.py`** sends `X-AzureBank-Service-Key` on every request (ADR-0055), and with it exactly
  one `X-AzureBank-Token-Road`, the marker the BFF's own client sends: the six token endpoints and
  the session-stamp feed answer 404 without it. It also sends a bearer token on every operation the
  contract does not declare anonymous — register, login, the demo's claim, refresh, revoke and
  session-stamps go without — for a throwaway user it registers itself, unless `AZUREBANK_CONTRACT_TOKEN` hands over
  the token of a user who already exists. CI hands over the seeded demo user's.
- **`schemathesis.toml`** sets the base URL and the shape of the run (one worker, 100 examples
  per operation, positive and negative inputs, all four phases, seed 42), loads `hooks.py`, and
  runs every check. CI's conformance job runs this same file (backlog row 41); until then the file
  ran CI's four response checks and no others, and CI loaded neither file.

**Measured on 2026-09-24**, against a local API on a LocalDB database just reset and seeded by
`AzureBank.Seeder`, as CI's is, with the two lines above:

```
=================================== SUMMARY ====================================

API Operations:
  Selected: 28/28
  Tested: 28

Test Phases:
  ⏭  Examples
  ✅ Coverage
  ✅ Fuzzing
  ✅ Stateful

Warnings:
  ⚠️ Schema validation mismatch: 3 operations mostly rejected generated data

Test cases:
  7731 generated, 7731 passed, 2357 skipped

Seed: 42

============================= 1 warning in 97.07s ==============================
```

All 28 operations tested with every check, exit 0. The warning is about what Schemathesis
generated, not about a response: the API rejected most of its inputs for login, refresh and
register. It is not stable — runs on 2026-09-23 and 2026-09-24 named two operations as often as
three.

**Measured again on 2026-09-28**, after `POST /api/auth/revoke` joined the contract, the way CI's
conformance job runs it: SQL Server in a container, the database reset and seeded, the API started
with `dotnet run`, the demo user's token handed over. Three runs, each `Selected: 29/29`,
`Tested: 29`, the same warning on the same three operations, exit 0. The case counts are NOT
stable across runs, seed or no seed: two runs that started with no `.hypothesis` directory in the
checkout each reported `8544 generated, 8544 passed, 2879 skipped`, and one run after a failed run
in the same checkout reported `8184 generated, 8184 passed, 2586 skipped`. That failed run was the
first of the day, on revoke: `{"refreshTokens": [null]}` answered 200, and the API now refuses a
null grant with 400.

**Measured again on 2026-10-04**, with the public demo's claim in the contract
(`POST /api/auth/demo/claim`, ADR-0063), the way CI's conformance job runs it but for the machine:
Windows, a LocalDB database just migrated and seeded, the API started with `dotnet run` in
Development with no `Demo__` variable, the demo user's token handed over. Two runs, each exit 0,
`Selected: 31/31`, `Tested: 31`, `8880 generated, 8880 passed, 2879 skipped`, and a JUnit report of
32 test cases (the 31 operations and the stateful phase). Two warnings, the same in both runs:

- `Missing test data: 1 operation repeatedly returned 404 Not Found, preventing tests from
  reaching your API's core logic`: the claim. With the demo off it answered its declared 404 to
  each of its 152 requests, so nothing behind that 404 was tested, and nothing failed.
- `Schema validation mismatch: 4 operations mostly rejected generated data`: the claim, login,
  refresh and register (three of them before the claim, as above).

Registration answered 400, 409, 201 and 415 in those runs and never the 403 it declares: that
answer exists only with the demo on. Not run: Schemathesis against an API with the demo on.

⚠️ **A run writes to the database it runs against.** One run on 2026-09-23 added 1 user, 97
accounts and 137 audit events. Point it at a database you can throw away. Run as the seeded demo
user, it also locks that user out: after one run on 2026-09-24, its next login answered 429
`ACCOUNT_LOCKED`. Reset and seed the database before running as it again.

Until 2026-09-23 this Quick Start was
`schemathesis run docs/api/openapiv1.json --url http://localhost:5068`, with no service key.
Measured that day, it exited 1 after 33 failures, and every one of the 28 operations had
answered only 401 `SERVICE_CREDENTIAL_REQUIRED` — in Schemathesis's words, "Missing
authentication: 28 operations returned only 401/403 responses". Since ADR-0055 it had tested
the API's front door, and nothing behind it.

## Test Options

Every command below was run again on 2026-09-24, after the configuration started running every
check.

### What every check found

The Quick Start runs every check since backlog row 41, and CI runs the same file. Until then it ran
CI's four response checks, and `--checks all` on top of it exited 1: five failures from the
input-side checks CI left out on purpose, and three more that showed only once the first were
gone:

- `API accepted schema-violating request`, five times: an EMPTY `at` on
  `GET /api/accounts/{id}/balance`, an empty `ToDate` on `GET /api/transactions` and on
  `GET /api/transactions/summary`, and, once those were refused, an empty `AccountId` on the same
  two. Each answered 200 where the contract's `date-time` and `uuid` formats say reject: the API
  read an empty value as an absent one. It now refuses one with 400, in the words it uses for
  `garbage`, on all seven nullable query parameters.
- `Missing header not rejected`, twice: `POST /api/transactions/withdraw` and `POST /api/transfers`
  without `Step-Up-Authorization` answered 404 for an account that does not exist, where the check
  expects 400, 401, 403, 406, 415 or 422. That order is decided, not accidental: the account's
  404 comes before the missing header's 401 by ADR-0056 (D4) on the withdrawal and ADR-0042 on
  the transfer, where it keeps a caller without the second factor from asking which payees
  exist. `schemathesis.toml` tells the check so, for those two operations.
- `API accepted schema-violating request` once more, on `GET /api/transactions` sent
  `?x-schemathesis-unknown-property=42`: the list ignores a query parameter it does not declare,
  as ASP.NET Core does. The contract does not say the query is closed, so `schemathesis.toml`
  stops sending the list one, rather than the API starting to refuse them.

This section was "Verbose Output" until 2026-09-23, which `--checks all` never had anything to do
with, and "Every check, not only CI's four" until backlog row 41.

### Specific Endpoint

```bash
schemathesis --config-file tests/contract/schemathesis.toml run docs/api/openapiv1.json \
  --include-path-regex "/api/auth/.*"
```

7 operations tested, exit 0. Not run again since revoke and session-stamps joined the contract,
which puts 9 operations under `/api/auth/` in the document.

### Generate Report

```bash
schemathesis --config-file tests/contract/schemathesis.toml run docs/api/openapiv1.json \
  --report junit
```

`--report` takes a FORMAT in 4.27.1 — `junit`, `vcr`, `har`, `ndjson`, `json` or `allure`; bare,
it is rejected. The report lands in `schemathesis-report/`, which `.gitignore` covers, under a
name carrying the run's timestamp (`junit-20260924T150847Z.xml` in the run measured).

### CI/CD Mode (JUnit output)

```bash
schemathesis --config-file tests/contract/schemathesis.toml run docs/api/openapiv1.json \
  --report junit \
  --report-junit-path test-results.xml
```

A fixed name instead of the timestamped one, which `.gitignore` also covers. The report held one
test case per operation, 28, and one more for the stateful phase. On 2026-09-28, after revoke
joined the contract, the conformance job's command line reported 29 and one more: `29 operations
tested, 30 test cases in the report`.

### Without the configuration file

```bash
SCHEMATHESIS_HOOKS=tests.contract.hooks \
schemathesis run docs/api/openapiv1.json \
  --url http://localhost:5068 \
  --checks status_code_conformance,content_type_conformance,response_headers_conformance,response_schema_conformance
```

The hooks load from an environment variable here — 4.27.1 has no `--hooks` flag — and `--url` is
required whenever the schema is given as a file. 28 operations tested (2026-09-24, before revoke
joined the contract), exit 0. Leave `--checks` out and every check runs: exit 1 on 2026-09-24,
with three failures, the three the configuration file declares for the operations concerned
(*What every check found*, above).

## What Gets Tested

Schemathesis automatically:

1. **Parses OpenAPI spec** - Reads all endpoint definitions
2. **Generates test cases** - Creates hundreds of inputs per endpoint
3. **Tests edge cases** - Boundary values, nulls, empty strings
4. **Fuzz tests** - Malformed data, special characters
5. **Validates responses** - Schema compliance, status codes
6. **Finds bugs** - 500 errors, crashes, and validation bypasses: every check runs

## Files

| File | Purpose | State under the pinned 4.27.1 |
|------|---------|---|
| `schemathesis.toml` | The Quick Start's configuration, and CI's: base URL, the shape of the run, the hooks, every check | ✅ loads — the runs above |
| `hooks.py` | The service key and the token-road marker on every request, and a bearer token on every operation that is not anonymous: a throwaway user's, or the one `AZUREBANK_CONTRACT_TOKEN` hands over | ✅ imports — the runs above |
| `README.md` | This documentation | — |

Both were v3-era until 2026-09-23, and 4.27.1 refused them (backlog rows 38 and 39). Measured
again that day, before the repair, the configuration stopped at
`Missing required properties: - 'title'` and `hooks.py` at
`Hook 'before_call' takes 3 arguments but 2 is defined`.

Two files were removed instead of repaired, each measured the same day first:

- **`tests/contract/schemathesis.yaml`**, a v3 configuration. 4.27.1 reads TOML only — handed
  this file it answers `The configuration file content is not valid TOML` — and the `--config`
  flag in its own header is rejected: `No such option '--config'`.
- **`run-contract-tests.ps1`**, at the repository root, which nothing referenced. Its login sent
  no service key and fell back to "testing unauthenticated", it pointed `SCHEMATHESIS_HOOKS` at
  the library's own `schemathesis.hooks`, and its run died on
  `No such option '--hypothesis-seed'`, so it printed `CONTRACT TESTS FAILED (exit code: 2)`.
  The Quick Start is what it was trying to be.

CI runs both since backlog row 41: its conformance job loads the configuration, and with it the
hooks, after logging the seeded demo user in and handing its token over in
`AZUREBANK_CONTRACT_TOKEN`. Until then it passed the token, the key and the checks as flags, and
depended on neither file.

## Expected Output

From the `Runtime conformance` job on `main`, run 35581244192, 2026-09-21 — the tail of a real
run rather than an illustration:

```
=================================== SUMMARY ====================================
============================= 2 warnings in 54.61s =============================
27 operations tested, 28 test cases in the report
```

The operation count comes from the committed document, so it moves when the contract does; the
step after the run fails the job if it drops below the floor. **It has moved since that run**: the
withdrawal mint took the document to 28 operations on 2026-09-21 (ADR-0056), revoke took it
to 29 on 2026-09-28, `POST /api/auth/session-stamps` to 30 the same day (ADR-0057 §5.3), and the
public demo's claim to 31 on 2026-10-03 (ADR-0063); the
floor was raised with each, so the 27 above is what THAT run tested and not what a run tests today.
A full run at 31 was measured by hand on 2026-10-04 (Quick Start, above); CI's own job had not
run at 31 when this was written. *(Until 2026-10-04 this said no full run had been measured since
session-stamps joined.)* The transcript is left as it was
recorded rather than edited to match, because a quoted run that is quietly updated stops being
evidence.

*(What stood here until 2026-09-21 was an invented transcript: it announced `Collected API
operations: 20` against a document that declares 27, listed `GET /api/users/search` — a route
ADR-0014 deleted on 2026-07-17 — and ended `847 passed in 45.23s`, a figure no run produced. It
was also the reason the Bruno collection called that same dead endpoint, found in #196.)*

## Troubleshooting

### `AZUREBANK_SERVICE_KEY is not set`

The hooks stop the run before its first request, exit 1. Export it as the Quick Start does.

### `Registering the throwaway user answered 401`

The key is set but is not the one the API holds, so register answered 401
`SERVICE_CREDENTIAL_REQUIRED`. Measured with a wrong key: the first three operations that needed
a token errored with this message, then the library stopped asking
(`Token fetch failed 3 times in a row; retrying in 30s`), the anonymous operations failed on the
401 itself, and the file's `max-failures = 10` ended the run — exit 1, after about 20 seconds.

### `Registering the throwaway user answered 404`

Register refused the caller the way it refuses any caller off the BFF's road: the request did not
come over loopback, or did not carry exactly one `X-AzureBank-Token-Road` (`TokenRoadMiddleware`).
The hooks send the marker, so a 404 here means the run is not on the API's machine, or runs hooks
from before 2026-09-28. Measured that day with those older hooks and no token handed over, on
`^/api/accounts$`: both operations errored with
`Error in 'ThrowawayUser.get()': Registering the throwaway user answered 404`, the summary read
`Authentication failed: 2 operations returned authentication errors`, exit 1.

### `No module named 'tests'`

The run started outside the repository root. From `tests/contract/`, Schemathesis finds
`schemathesis.toml` by itself — it looks in the working directory and its parents — and the
`hooks` module the file names does not resolve from there. Measured: exit 1, before any request.

### `UnicodeEncodeError: 'charmap' codec can't encode characters` (Windows)

```bash
export PYTHONIOENCODING=utf-8
```

Measured with the output redirected to a file: 4.27.1 printed `Schemathesis v4.27.1` and died on
the box-drawing line under it, which the Windows code page cannot encode.

### `Unmatched filters` in Git Bash (Windows)

```bash
export MSYS_NO_PATHCONV=1
```

Git Bash rewrites an argument that starts with `/` into a Windows path before Schemathesis sees
it. Measured: `--include-path-regex '/api/auth/me'` arrived as
`'C:/Program Files/Git/api/auth/me'`, matched no operation, tested nothing — and exited 0. With
the variable above it matched one. The pattern in Specific Endpoint, `/api/auth/.*`, was not
affected.

### `CERTIFICATE_VERIFY_FAILED` against the local HTTPS profile

```bash
schemathesis --config-file tests/contract/schemathesis.toml run docs/api/openapiv1.json \
  --url https://localhost:7215 --tls-verify=false
```

The API's HTTPS profile serves the ASP.NET development certificate, which is not in the
certificate bundle Python checks against. Measured on 2026-09-23 against it: without the flag the
run stops before its first request, at the hooks' registration —
`[SSL: CERTIFICATE_VERIFY_FAILED] certificate verify failed: self-signed certificate` — and with it
all 28 operations were tested, exit 0.

**The flag is for that run, and for localhost only.** `schemathesis.toml` deliberately keeps
certificate verification on: the hooks send the service key on every request, and a file that
switched verification off would switch it off for any `--url` given with it — handing the key to
whoever answers. The Quick Start's `http://localhost:5068` involves no TLS at all.

### Rate limiting, timeouts

`schemathesis.toml` already sets `workers = 1` and `request-timeout = 60`. On the command line,
without the file or over it, they are `--workers=1` and `--request-timeout=60` — appended to the
Quick Start line (measured with 30, exit 0). The timeout is in SECONDS: this page said 30000 until
2026-09-21, which is eight hours and twenty minutes per request, not a generous timeout. It was 30
until ADR-0058 gave every request except refresh, revoke and logout a 40 s deadline: a request
stalled before its commit now answers its documented 503 within 53 s, and the client has to outwait
it to see that answer.
