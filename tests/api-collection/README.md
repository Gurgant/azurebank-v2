# Bruno API Collection - AzureBank

## Overview

This is a **Bruno** API collection for testing the AzureBank API. Bruno is a fast, git-friendly, open-source API client.

## Installation

```bash
# Windows (Chocolatey)
choco install bruno

# macOS (Homebrew)
brew install bruno

# Or download from https://www.usebruno.com/downloads
```

## Opening the Collection

1. Open Bruno
2. Click **"Open Collection"**
3. Navigate to `tests/api-collection/`
4. Select this folder

## Environments

| Environment | File | Purpose |
|-------------|------|---------|
| `local` | `environments/local.bru` | Local development, against `https://localhost:7215`. `serviceKey` ships EMPTY and **stays** empty: this file is tracked and nothing in `.gitignore` covers it, so a key written here is a key committed. Pass it per run instead (below). |
| `ci` | `environments/ci.bru` | The `Contract tests` workflow, which runs `--env ci` on every pull request, every push to `main`, and by hand. Self-contained: the throwaway key and the `http://localhost:5068` that workflow starts the API on. Nothing in it is a real secret. |

The API serves only the BFF (ADR-0055), so every request here carries
`X-AzureBank-Service-Key`, which `collection.bru` reads from `serviceKey`. Supply your own
`ServiceCredential:BffKey` — the [local setup](../../docs/engineering-practices.md#local-setup)'s
recipe generates it — on the command line:

```bash
cd tests/api-collection
bru run . -r --env local --env-var serviceKey="$YOUR_KEY" --insecure
```

Three things in that line are not optional, and each was measured on 2026-09-21:

- **`cd` first.** `bru run` works only from the collection root: run it from the repository root
  and it answers `You can run only at the root of a collection`, having sent nothing.
- **`-r`.** Recursion is enabled only when NO path argument is given, so `bru run .` lists the root
  folder, finds no `.bru` there — every request lives in a subfolder — and reports
  `Requests 0 / Tests 0/0 / Status PASS`. A green run that executed nothing.
- **`--env-var serviceKey`.** `local.bru` ships it empty on purpose (above), so without it every
  request answers `401 SERVICE_CREDENTIAL_REQUIRED`.

`--insecure` is for ASP.NET's development certificate on `https://localhost:7215`. Node does not
read the Windows certificate store, so trusting that certificate with `dotnet dev-certs https
--trust` is not enough. Measured without the flag: `self-signed certificate; if the root CA is
installed locally, try running Node.js with --use-system-ca` — which is the other way out, if you
would rather not turn verification off.

`BrunoEnvironmentSecretTests` fails the build if that value stops being empty in `local.bru`, or if
this file starts telling you to write it there.

The six token endpoints — login, register, refresh, revoke, logout and the public demo's claim —
and the BFF's session-stamp feed take one thing more; no request here calls the claim or the
feed. They answer 404 unless the request carries exactly one `X-AzureBank-Token-Road`, the marker
the BFF's own client sends, and comes from the machine the API listens on (loopback). So the
requests that call them — register, login, revoke, logout and the transfers folder's
register-recipient — send
`X-AzureBank-Token-Road: bff` themselves. It is not a secret: the API checks that exactly one
arrived and that it says `bff`, compared exactly. Measured on 2026-09-28 against the API as
`Contract tests` starts it: this collection before the marker, 28 of 28 requests failed, register,
login and logout on 404; with it, all green (below).

**Measured on 2026-09-19 and again on 2026-09-20** with Bruno CLI 4.1.0 against the running API:
with the command above, `register` answered **201**; with `local.bru`'s empty `serviceKey`, every
request answered **401**.

Since 2026-09-25 the API redirects nothing to HTTPS: measured on the `https` profile,
`GET http://localhost:5068/health/live` answered 307 to `https://localhost:7215/health/live`
before and 200 after.

Two 401s can answer a request here, and `errorCode` tells them apart:
`SERVICE_CREDENTIAL_REQUIRED` is this API refusing the caller, `INVALID_CREDENTIALS` is the
credentials.

## Collection Structure

```
api-collection/
├── bruno.json              # Collection config
├── collection.bru          # The X-AzureBank-Service-Key header every request sends (ADR-0055)
├── environments/
│   ├── local.bru          # Local environment
│   └── ci.bru             # CI environment
└── endpoints/             # in RUN order: folder seq, then request seq
    ├── auth/                         # 1
    │   ├── folder.bru                # the folder's seq; without one, Bruno runs folders alphabetically
    │   ├── register.bru
    │   ├── login.bru
    │   ├── get-me.bru
    │   ├── set-pin.bru
    │   ├── verify-pin.bru
    │   ├── revoke.bru                # ends the grant login published, as the BFF does for one session
    │   └── logout.bru                # ends every grant of the user
    ├── accounts/                     # 2
    │   ├── folder.bru
    │   ├── list-accounts.bru
    │   ├── create-account.bru
    │   ├── get-account.bru
    │   ├── get-balance.bru
    │   └── update-account.bru
    ├── transactions/                 # 3
    │   ├── folder.bru
    │   ├── deposit.bru
    │   ├── authorize-withdraw.bru    # mints; the withdrawal presents it (ADR-0056)
    │   ├── withdraw.bru
    │   ├── list-transactions.bru
    │   └── get-transaction.bru
    ├── transfers/                    # 4
    │   ├── folder.bru
    │   ├── register-recipient.bru
    │   ├── authorize-transfer.bru    # mints; the transfer presents it (ADR-0042)
    │   ├── transfer-to-user.bru
    │   ├── authorize-internal-transfer.bru
    │   └── internal-transfer.bru
    ├── idempotency/                  # 5
    │   ├── folder.bru
    │   ├── deposit-first-execution.bru
    │   ├── deposit-replay.bru
    │   ├── deposit-key-reuse-422.bru
    │   ├── deposit-missing-key-400.bru
    │   └── deposit-invalid-key-400.bru
    └── users/                        # 6
        ├── folder.bru
        ├── user-not-found.bru
        └── get-user-by-tag.bru
```

## Running Tests

### Via Bruno GUI

1. Open collection in Bruno
2. Select environment (local/ci)
3. Run individual requests or folder

### Via CLI

Every `--env local` command carries the same two arguments, for the two reasons above:
`--env-var serviceKey` because `local.bru` ships that value empty, and `--insecure` for the
development certificate.

```bash
# Install CLI
npm install -g @usebruno/cli

# Everything below runs from the collection root, which is where bru insists on being.
cd tests/api-collection

# The whole collection. -r or it sends nothing; see above.
bru run . -r --env local --env-var serviceKey="$YOUR_KEY" --insecure

# One folder. No -r here: its requests are that folder's direct children.
bru run endpoints/auth --env local --env-var serviceKey="$YOUR_KEY" --insecure

# The whole collection, with a JUnit report.
bru run . -r --env local --env-var serviceKey="$YOUR_KEY" --insecure --reporter-junit results.xml
```

Measured on 2026-09-28, by hand, with the `Contract tests` workflow's command line (`--env ci`)
and Bruno CLI 4.1.0, against the API started as the workflow starts it (SQL Server in a
container, the database reset and seeded): the whole collection is **29 requests, 82 tests, 63
assertions, all green**, and 145 test cases in the JUnit report.

## Test Workflow

The recommended order for running tests:

1. **Register** - Creates new user and gets token
2. **Set PIN** - Sets up the PIN that every mint spends: the withdrawal's and both transfers'
3. **List Accounts** - Gets primary account ID
4. **Deposit** - Adds money to account
5. Other tests...

## Variables

The collection uses these variables (set automatically by tests):

| Variable | Set By | Description |
|----------|--------|-------------|
| `serviceKey` | the environment file, by hand | The API's service credential (ADR-0055), sent by `collection.bru` as `X-AzureBank-Service-Key` on every request. Empty in `local.bru` by design — a committed key would be both a wrong value and a bad habit |
| `authToken` | Register/Login | JWT authentication token |
| `refreshToken` | Login | The session's grant, which Revoke ends |
| `accountId` | Register/List Accounts | Primary account ID |
| `transactionId` | Deposit/Withdraw | Transaction ID |

## Adding New Tests

1. Create a new `.bru` file in the appropriate folder
2. Follow the existing pattern:

```bru
meta {
  name: My Test
  type: http
  seq: N
}

get {
  url: {{baseUrl}}/api/endpoint
  body: none
  auth: bearer
}

auth:bearer {
  token: {{authToken}}
}

assert {
  res.status: eq 200
}

tests {
  test("Description", function() {
    expect(res.status).to.equal(200);
  });
}
```

## CI/CD Integration

### GitHub Actions

The repository runs `.github/workflows/contract-tests.yml` on every pull request that targets
`main`, on every push to `main`, and by hand. Its shape, and the reason for each part:

```yaml
on:
  push:
    branches: [main]
  pull_request:
    branches: [main]
  workflow_dispatch:                          # and still by hand

- name: Install Bruno CLI
  run: npm install -g @usebruno/cli@4.1.0     # pinned: an unpinned run proves nothing nameable

- name: Run Bruno tests
  working-directory: tests/api-collection     # bru runs only from the collection root
  run: |
    bru run . -r --env ci \                   # -r, or it sends nothing and still passes
      --reporter-junit ../../bruno-results.xml
```

The step can fail: a job that runs the collection and cannot fail says nothing. The step that
follows it refuses a report with too few test cases, so a run that did not happen cannot look
like a run that found nothing.

## Why Bruno?

- **Git-friendly**: Collections stored as plain text files
- **Offline-first**: No cloud sync, data stays local
- **Open source**: Free forever, MIT licensed
- **Fast**: Native app, not Electron-heavy
- **Team collaboration**: via Git PRs, not cloud accounts
