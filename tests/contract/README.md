# Contract Testing with Schemathesis

Schemathesis generates requests from the committed contract, `docs/api/openapiv1.json`, sends
them to a running API and checks every answer against that document. This folder holds its
configuration, `schemathesis.toml`, and its hooks, `hooks.py`. CI's `Runtime conformance` job runs
the same two files on every pull request (ADR-0053, D6).

## Prerequisites

```bash
# The version CI pins: SCHEMATHESIS_VERSION in .github/workflows/ci.yml.
pip install 'schemathesis==4.27.1'
```

- **Pin the version.** v4 renamed or removed enough flags that another version is a different
  tool, and everything on this page was measured against 4.27.1. The Docker image
  `schemathesis/schemathesis:stable` is not pinned, and a run from a container was not measured:
  whether the API sees loopback depends on how the container reaches it.
- **Run it on the machine the API listens on.** The six token endpoints (login, register,
  refresh, revoke, logout and the public demo's claim) and the session-stamp feed answer 404 to a
  caller whose address is not loopback, and to one that does not send exactly one
  `X-AzureBank-Token-Road` (`TokenRoadMiddleware`, ADR-0057 §4.2). The document declares no 404
  on six of the seven, so a run from elsewhere fails on them.
- **Point it at a database you can throw away.** A run writes: one run on 2026-09-23 added 1
  user, 97 accounts and 137 audit events. Run as the seeded demo user, it also locks that user
  out: after one run its next login answered 429 `ACCOUNT_LOCKED`. Reset and seed the database
  before running as that user again.

## Quick Start

```bash
# 1. Start the API, on http://localhost:5068.
cd backend/src/AzureBank.Api
dotnet run
```

```bash
# 2. From the repository ROOT: the configuration names its hooks module from there.
export AZUREBANK_SERVICE_KEY="$SERVICE_KEY"
schemathesis --config-file tests/contract/schemathesis.toml run docs/api/openapiv1.json
```

- `AZUREBANK_SERVICE_KEY` holds the value of the API's `ServiceCredential:BffKey`: `$SERVICE_KEY`
  after the [local setup](../../docs/engineering-practices.md#local-setup)'s recipe. The hooks
  read it from the environment, so it is never on a command line.
- `--config-file` is a global option, so it goes before `run`: after it, 4.27.1 answers
  `Usage: schemathesis run [OPTIONS] LOCATION`.

What the two files add to that line:

| File | Adds |
|---|---|
| `hooks.py` | `X-AzureBank-Service-Key` on every request (ADR-0055), and with it exactly one `X-AzureBank-Token-Road`, the marker the BFF's own client sends. A bearer token on every operation the contract does not declare anonymous (register, login, the demo's claim, refresh, revoke and session-stamps go without), for a throwaway user the hooks register themselves, unless `AZUREBANK_CONTRACT_TOKEN` hands over the token of a user who already exists |
| `schemathesis.toml` | The base URL, `http://localhost:5068`, and the shape of the run: one worker, 100 examples for each operation, positive and negative inputs, all four phases, seed 42, a request timeout of 60 s, a stop after 10 failures. It loads `hooks.py` and runs every check |

The configuration also tells the checks two things about the API, on three operations:

- `POST /api/transactions/withdraw` and `POST /api/transfers` without `Step-Up-Authorization`
  answer 404 for an account that does not exist, where the check expects 400, 401, 403, 406, 415
  or 422. That order is decided: the account's 404 comes before the missing header's 401 by
  ADR-0056 (D4) on the withdrawal and by ADR-0042 (decision 8) on the transfer, where it keeps a
  caller without the second factor from asking which payees exist.
- `GET /api/transactions` is sent no query parameter it does not declare. The list ignores one,
  as ASP.NET Core does, and the contract does not say the query is closed.

A run without the hooks, and so without the service key, tests the API's front door and nothing
behind it: every operation answers only 401 `SERVICE_CREDENTIAL_REQUIRED`, in Schemathesis's
words "Missing authentication" (ADR-0055).

## Expected Output

Measured on 2026-10-04, the way CI runs it but for the machine: Windows, a LocalDB database just
migrated and seeded, the API started with `dotnet run` in Development with no `Demo__` variable,
the seeded demo user's token handed over. Two runs, each exit 0, `Selected: 31/31`, `Tested: 31`,
`8880 generated, 8880 passed, 2879 skipped`, and a JUnit report of 32 test cases: the 31
operations and the stateful phase. Two warnings, the same in both runs, and neither is a failure:

- `Missing test data: 1 operation repeatedly returned 404 Not Found`: the demo's claim. With
  `Demo:Enabled` false it answers its declared 404 to every request that carries the service key
  (ADR-0063), so nothing behind that 404 is tested.
- `Schema validation mismatch: 4 operations mostly rejected generated data`: the claim, login,
  refresh and register. It is about what Schemathesis generated, not about a response.

The case counts are not stable across runs, seed or no seed. On 2026-09-28, at 29 operations, two
runs that started with no `.hypothesis` directory in the checkout each generated 8544 cases, and
one run after a failed run in the same checkout 8184. Nor is the second warning: at 28
operations it named two of them as often as three.

Not run: Schemathesis against an API with the demo on. Registration answers 400, 409, 201 and
415 in these runs and never the 403 it declares, which exists only with the demo on.

## Test Options

```bash
# One part of the API. Measured at 7 operations; the document holds 10 under /api/auth/ today.
schemathesis --config-file tests/contract/schemathesis.toml run docs/api/openapiv1.json \
  --include-path-regex "/api/auth/.*"

# A JUnit report in schemathesis-report/, under a name that carries the run's timestamp.
schemathesis --config-file tests/contract/schemathesis.toml run docs/api/openapiv1.json \
  --report junit

# The same report under a fixed name.
schemathesis --config-file tests/contract/schemathesis.toml run docs/api/openapiv1.json \
  --report junit \
  --report-junit-path test-results.xml

# Without the configuration file.
SCHEMATHESIS_HOOKS=tests.contract.hooks \
schemathesis run docs/api/openapiv1.json \
  --url http://localhost:5068 \
  --checks status_code_conformance,content_type_conformance,response_headers_conformance,response_schema_conformance
```

- `--report` takes a format in 4.27.1: `junit`, `vcr`, `har`, `ndjson`, `json` or `allure`. Bare,
  it is rejected. `.gitignore` covers `schemathesis-report/` and `test-results.xml`.
- Without the configuration file the hooks load from an environment variable, since 4.27.1 has
  no `--hooks` flag, and `--url` is required whenever the schema is given as a file. Leave
  `--checks` out there and every check runs: exit 1 with three failures, the three the
  configuration file declares (measured 2026-09-24).
- `schemathesis.toml` sets `workers = 1` and `request-timeout = 60`, in seconds. On the command
  line they are `--workers=1` and `--request-timeout=60`. It is 60 because ADR-0058 gives every
  request except refresh, revoke and logout a 40 s deadline: a request stalled before its commit
  answers its documented 503 within 53 s, and the client has to outwait it to see that answer.

## In CI

The `Runtime conformance` job of `.github/workflows/ci.yml` starts SQL Server in a container,
resets and seeds the database, starts the API with `dotnet run`, logs the seeded demo user in,
hands its token over in `AZUREBANK_CONTRACT_TOKEN` and runs the Quick Start's command with a
JUnit report. A finding fails the job. So does the step after the run when the report holds fewer
than 31 operations: the document declares 31, and that floor moves when the contract does.

## Troubleshooting

- **`AZUREBANK_SERVICE_KEY is not set`**: the hooks stop the run before its first request, exit
  1. Export it as the Quick Start does.
- **`Registering the throwaway user answered 401`**: the key is set but is not the one the API
  holds, so register answered 401 `SERVICE_CREDENTIAL_REQUIRED`. Measured with a wrong key: the
  run ended by itself, exit 1, after about 20 seconds, at the file's `max-failures = 10`.
- **`Registering the throwaway user answered 404`**: register refused the caller the way it
  refuses any caller off the BFF's road. The hooks send the marker, so the request did not come
  over loopback: the run is not on the API's machine.
- **`No module named 'tests'`**: the run started outside the repository root. From
  `tests/contract/`, Schemathesis finds `schemathesis.toml` by itself, since it looks in the
  working directory and its parents, and the hooks module the file names does not resolve from
  there. Exit 1, before any request.
- **`UnicodeEncodeError: 'charmap' codec can't encode characters`** (Windows): 4.27.1 prints a
  box-drawing line that the Windows code page cannot encode. Measured with the output redirected
  to a file.

  ```bash
  export PYTHONIOENCODING=utf-8
  ```

- **`Unmatched filters` in Git Bash** (Windows): Git Bash rewrites an argument that starts with
  `/` into a Windows path before Schemathesis sees it. Measured:
  `--include-path-regex '/api/auth/me'` arrived as `'C:/Program Files/Git/api/auth/me'`, matched
  no operation, tested nothing, and exited 0. With the variable below it matched one. The pattern
  under Test Options, `/api/auth/.*`, was not affected.

  ```bash
  export MSYS_NO_PATHCONV=1
  ```

- **`CERTIFICATE_VERIFY_FAILED` against the local HTTPS profile**: that profile serves the
  ASP.NET development certificate, which is not in the certificate bundle Python checks against.
  Without the flag below the run stops before its first request, at the hooks' registration;
  with it, every operation was tested, exit 0 (measured 2026-09-23).

  ```bash
  schemathesis --config-file tests/contract/schemathesis.toml run docs/api/openapiv1.json \
    --url https://localhost:7215 --tls-verify=false
  ```

  **The flag is for that run, and for localhost only.** `schemathesis.toml` keeps certificate
  verification on, on purpose: the hooks send the service key on every request, and a file that
  switched verification off would switch it off for any `--url` given with it, handing the key
  to whoever answers. The Quick Start's `http://localhost:5068` involves no TLS at all.
