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
# if you followed the root README's recipe. The hooks read it from the environment, so it is
# never on a command line.
export AZUREBANK_SERVICE_KEY="$SERVICE_KEY"
schemathesis --config-file tests/contract/schemathesis.toml run docs/api/openapiv1.json
```

`--config-file` is a GLOBAL option, so it goes BEFORE `run`: after it, 4.27.1 answers
`Usage: schemathesis run [OPTIONS] LOCATION`.

What the two files add to that line:

- **`hooks.py`** sends `X-AzureBank-Service-Key` on every request (ADR-0055), and a bearer token
  on every operation the contract does not declare anonymous — register, login and refresh go
  without — for a throwaway user it registers itself.
- **`schemathesis.toml`** sets the base URL and the shape of the run (one worker, 100 examples
  per operation, positive and negative inputs, all four phases, seed 42), loads `hooks.py`, and
  runs the four response checks CI's conformance job runs, and no others.

**Measured on 2026-09-23**, against a local API on a freshly migrated LocalDB database, with the
two lines above:

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
  5362 generated, 5362 passed, 174 skipped

Seed: 42

============================= 1 warning in 79.95s ==============================
```

All 28 operations tested, exit 0. The warning is about what Schemathesis generated, not about a
response: the API rejected most of its inputs for login, refresh and register. It is not
stable — a second run the same day named two operations instead of three.

⚠️ **A run writes to the database it runs against.** That second run added 1 user, 97 accounts
and 137 audit events. Point it at a database you can throw away.

Until 2026-09-23 this Quick Start was
`schemathesis run docs/api/openapiv1.json --url http://localhost:5068`, with no service key.
Measured that day, it exited 1 after 33 failures, and every one of the 28 operations had
answered only 401 `SERVICE_CREDENTIAL_REQUIRED` — in Schemathesis's words, "Missing
authentication: 28 operations returned only 401/403 responses". Since ADR-0055 it had tested
the API's front door, and nothing behind it.

## Test Options

Each is the Quick Start line with something added, and each was run on 2026-09-23.

### Every check, not only CI's four

```bash
schemathesis --config-file tests/contract/schemathesis.toml run docs/api/openapiv1.json \
  --checks all
```

Exit 1, with five failures, all from input-side checks that CI leaves out on purpose:

- three `API accepted schema-violating request`: an EMPTY `at` on
  `GET /api/accounts/{id}/balance`, and an empty `ToDate` on `GET /api/transactions` and
  `GET /api/transactions/summary`, each answered 200 where the contract's `date-time` format says
  reject. A real divergence, recorded for repair rather than changed here;
- two `Missing header not rejected`: `POST /api/transactions/withdraw` and `POST /api/transfers`
  without `Step-Up-Authorization` answered 404 for an account that does not exist, where the check
  expects 400, 401, 403, 406, 415 or 422. That order is decided, not accidental: the account's
  404 comes before the missing header's 401 by ADR-0056 (D4) on the withdrawal and ADR-0042 on
  the transfer, where it keeps a caller without the second factor from asking which payees
  exist. The check assumes the opposite order.

This heading was "Verbose Output" until 2026-09-23; `--checks all` has nothing to do with
verbosity.

### Specific Endpoint

```bash
schemathesis --config-file tests/contract/schemathesis.toml run docs/api/openapiv1.json \
  --include-path-regex "/api/auth/.*"
```

7 operations tested, exit 0.

### Generate Report

```bash
schemathesis --config-file tests/contract/schemathesis.toml run docs/api/openapiv1.json \
  --report junit
```

`--report` takes a FORMAT in 4.27.1 — `junit`, `vcr`, `har`, `ndjson`, `json` or `allure`; bare,
it is rejected. The report lands in `schemathesis-report/`, which `.gitignore` covers, under a
name carrying the run's timestamp (`junit-20260923T170759Z.xml` in the run measured).

### CI/CD Mode (JUnit output)

```bash
schemathesis --config-file tests/contract/schemathesis.toml run docs/api/openapiv1.json \
  --report junit \
  --report-junit-path test-results.xml
```

A fixed name instead of the timestamped one, which `.gitignore` also covers. The report held one
test case per operation, 28, and one more for the stateful phase.

### Without the configuration file

```bash
SCHEMATHESIS_HOOKS=tests.contract.hooks \
schemathesis run docs/api/openapiv1.json \
  --url http://localhost:5068 \
  --checks status_code_conformance,content_type_conformance,response_headers_conformance,response_schema_conformance
```

The hooks load from an environment variable here — 4.27.1 has no `--hooks` flag — and `--url` is
required whenever the schema is given as a file. 28 operations tested, exit 0. Leave `--checks`
out and every check runs, with the five failures above.

## What Gets Tested

Schemathesis automatically:

1. **Parses OpenAPI spec** - Reads all endpoint definitions
2. **Generates test cases** - Creates hundreds of inputs per endpoint
3. **Tests edge cases** - Boundary values, nulls, empty strings
4. **Fuzz tests** - Malformed data, special characters
5. **Validates responses** - Schema compliance, status codes
6. **Finds bugs** - 500 errors, crashes, and with `--checks all`, validation bypasses

## Files

| File | Purpose | State under the pinned 4.27.1 |
|------|---------|---|
| `schemathesis.toml` | The Quick Start's configuration: base URL, the shape of the run, the hooks, CI's four checks | ✅ loads — the runs above |
| `hooks.py` | The service key on every request, and a throwaway user's bearer token on every operation that is not anonymous | ✅ imports — the runs above |
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

CI depends on none of them: its conformance job logs the seeded demo user in and passes the
token, the key and the checks as flags.

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
withdrawal mint took the document to 28 operations on 2026-09-21 (ADR-0056) and the floor was
raised with it, so the 27 above is what THAT run tested and not what a run tests today. The
transcript is left as it was recorded rather than edited to match, because a quoted run that is
quietly updated stops being evidence.

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

`schemathesis.toml` already sets `workers = 1` and `request-timeout = 30`. On the command line,
without the file or over it, they are `--workers=1` and `--request-timeout=30` — appended to the
Quick Start line, measured, exit 0. The timeout is in SECONDS: this page said 30000 until
2026-09-21, which is eight hours and twenty minutes per request, not a generous timeout.
