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

### 2. Run Auto-Generated Tests

```bash
# From the repository root. The document lives at docs/api/openapiv1.json and never
# lived in backend/ -- `git log --all -- backend/openapiv1.json` is empty. CI reads the
# same path; it also passes a bearer token, the service-credential header, a check list,
# a report path and a coverage step, so this is the document location CI uses and not the
# whole command it runs (.github/workflows/ci.yml, the conformance job).
schemathesis run docs/api/openapiv1.json --url http://localhost:5068
```

### 3. Run with Configuration File

⚠️ **`schemathesis.toml` is NOT loadable by 4.27.1, and the heading here claimed it was
"v4 TOML format" until 2026-09-21.** The flag placement below is correct; the file it
points at is not. Running it answers `Failed to load configuration file`, and the errors
arrive one at a time rather than all at once — measured by adding the first missing key and
re-running, which surfaced a second, structural one:

| section | what 4.27.1 says |
|---|---|
| `[project.0]` | `Missing required properties: - 'title'` |
| `[phases]` | `'enabled' -> Must be a boolean, but got list: ['examples', 'coverage', 'fuzzing']` |

The second is a shape change, not a missing key, so this is a v3-era file rather than a
v4 one with a gap — which is why it is recorded rather than patched inside a documentation
correction. CI is unaffected: it passes its settings as flags and never passes
`--config-file`.

⚠️ **But it is not inert.** Schemathesis auto-loads `schemathesis.toml` from the working
directory and its PARENTS, so the one place this file IS read is the folder it sits in —
which is where a reader of this page is most likely to run from. Measured both ways, with
the same command and only the directory changed:

| run from | what happens |
|---|---|
| the repository root | `Loaded specification from docs/api/openapiv1.json` — no configuration error |
| `tests/contract/` | `Failed to load configuration file` — stops before any test runs |

So **run these commands from the repository root**, as every example here does, until the
file is repaired.

```bash
# The flag placement IS right: --config-file is a GLOBAL option, so it goes BEFORE `run`.
# After it, 4.27.1 answers "Usage: schemathesis run [OPTIONS] LOCATION".
schemathesis --config-file tests/contract/schemathesis.toml run docs/api/openapiv1.json
```

## Test Options

### Basic Run
```bash
schemathesis run docs/api/openapiv1.json --url http://localhost:5068
```

### With Authentication

⚠️ **`hooks.py` does not load under 4.27.1, so this section documents a mechanism that
is right and a file that is broken.** Measured 2026-09-21, not read: the command below is
how hooks are loaded, and running it prints `Unable to load Schemathesis extension hooks`.

```bash
# Hooks load from an environment variable, not a flag: 4.27.1 has no --hooks, and the
# name is HOOKS_MODULE_ENV_VAR in schemathesis/core/hooks.py. BOTH accepted forms were
# tried -- the dotted module below, and the file path `tests/contract/hooks.py` -- and
# both fail identically, INSIDE the file rather than on the path.
SCHEMATHESIS_HOOKS=tests.contract.hooks \
schemathesis run docs/api/openapiv1.json \
  --url http://localhost:5068
```

The file is v3-era. Registering each of its four hooks with the library's own validator
rather than reading them:

| `hooks.py` | hook | result under 4.27.1 |
|---|---|---|
| `:93` | `before_call` | `Hook 'before_call' takes 3 arguments but 2 is defined` — v4 passes `kwargs` third |
| `:118` | `before_generate_case` | registers |
| `:128` | `add_case` | `There is no hook with name 'add_case'` — removed in v4 |
| `:139` | `after_call` | registers |

Two of four. But the module raises at import on the FIRST one, so **none** of the four
ever registers — this is not a partial failure that leaves authentication half-working.

The conformance job is unaffected and its green is honest: CI never sets
`SCHEMATHESIS_HOOKS`, and passes the bearer token and the service-key header as `-H`
arguments instead. Repairing the file is a code change and is recorded rather than done
here.

### Verbose Output
```bash
schemathesis run docs/api/openapiv1.json \
  --url http://localhost:5068 \
  --checks all
```

### Specific Endpoint
```bash
schemathesis run docs/api/openapiv1.json \
  --url http://localhost:5068 \
  --include-path-regex "/api/auth/.*"
```

### Generate Report
```bash
schemathesis run docs/api/openapiv1.json \
  --url http://localhost:5068 \
  --report junit
```

`--report` takes a FORMAT in 4.27.1 (`junit`, `vcr`, `har`); bare, it is rejected.

### CI/CD Mode (JUnit output)
```bash
schemathesis run docs/api/openapiv1.json \
  --url http://localhost:5068 \
  --report junit \
  --report-junit-path test-results.xml
```

## What Gets Tested

Schemathesis automatically:

1. **Parses OpenAPI spec** - Reads all endpoint definitions
2. **Generates test cases** - Creates hundreds of inputs per endpoint
3. **Tests edge cases** - Boundary values, nulls, empty strings
4. **Fuzz tests** - Malformed data, special characters
5. **Validates responses** - Schema compliance, status codes
6. **Finds bugs** - 500 errors, validation bypasses, crashes

## Files

⚠️ **Nothing in this folder except this page is loaded by anything today**, and the labels
below said otherwise until 2026-09-21. Each row now carries what was measured, not what the
filename suggests.

| File | Purpose | State under the pinned 4.27.1 |
|------|---------|---|
| `schemathesis.toml` | Intended main configuration | ❌ does not load — v3-shaped despite the extension; this row called it "(v4 format)" |
| `schemathesis.yaml` | Superseded configuration | ❌ v3; its own header gives `--config`, a flag v4 renamed to `--config-file`. Correctly labelled legacy already |
| `hooks.py` | Intended authentication hooks | ❌ does not import — 2 of its 4 hook signatures are v3, and the first aborts the module |
| `README.md` | This documentation | — |

CI depends on none of them: it passes the token, the header and the checks as flags. The two
❌ rows that are not already labelled legacy are recorded for repair rather than fixed here,
because their contents are code and this change corrects records.

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

### SSL Certificate Errors
```bash
# Add --tls-verify=false for localhost
schemathesis run docs/api/openapiv1.json --url http://localhost:5068 --tls-verify=false
```

### Rate Limiting
```bash
# Reduce concurrency. --url is REQUIRED whenever the schema is given as a file -- without it
# 4.27.1 answers "Error: The `--url` option is required when specifying a schema via a file."
schemathesis run docs/api/openapiv1.json --url http://localhost:5068 --workers=1
```

### Timeout Issues
```bash
# Increase timeout. SECONDS, not milliseconds: this line said 30000 until 2026-09-21, which is
# eight hours and twenty minutes per request, not a generous timeout.
schemathesis run docs/api/openapiv1.json --url http://localhost:5068 --request-timeout=30
```
