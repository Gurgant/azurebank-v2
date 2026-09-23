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
| `ci` | `environments/ci.bru` | The manual `Contract tests` workflow, which runs `--env ci`. Self-contained: the throwaway key and the `http://localhost:5068` that workflow starts the API on. Nothing in it is a real secret. |

The API serves only the BFF (ADR-0055), so every request here carries
`X-AzureBank-Service-Key`, which `collection.bru` reads from `serviceKey`. Supply your own
`ServiceCredential:BffKey` — the root README's recipe generates it — on the command line:

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

**Measured on 2026-09-19 and again on 2026-09-20** with Bruno CLI 4.1.0 against the running API,
because the wiring above had been written and not run: with the command above, `register` answered
**201**; with `local.bru`'s empty `serviceKey`, every request answered **401**.

`baseUrl` was `http://localhost:5068` until 2026-09-20 — CI's address, from when CI ran `--env
local`. The local dev profile also listens on HTTPS, so `UseHttpsRedirection` answered **307** to
every request there, including the ones this README told you to make. CI has its own environment
now, so this one names the address a developer actually has.

~~One defect is left, older than all of this and not fixed here: `login` posts `{{testEmail}}`
(`test@example.com`), but `register` creates `test.{{$timestamp}}@example.com` and never writes it
back, so every request after `register` answers 401 `INVALID_CREDENTIALS` — the collection's own
credentials, not the service key.~~ *(Struck 2026-09-23: fixed on 2026-09-21 by `c51b266`, where
`register` began publishing the address it registered as `testEmail` — and this paragraph was left
behind. Every run of the collection measured on 2026-09-23 passed its login.)* Telling those two
401s apart is still what `errorCode` is for: `SERVICE_CREDENTIAL_REQUIRED` is this API refusing
the caller, `INVALID_CREDENTIALS` is the credentials.

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
    │   └── logout.bru
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

Regenerated from the directory on 2026-09-23, in run order. The tree it replaced had been drawn
by hand and no longer matched the directory: it listed `users/search-users.bru` - a request for
`/api/users/search`, the route ADR-0014 deleted, and a file that no longer exists - and it was
missing `collection.bru`, every `folder.bru`, the whole `idempotency/` folder, three of the five
transfer requests and `users/user-not-found.bru`.

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

Measured by the `Contract tests` workflow under `--env ci` on 2026-09-23 (run 35926861629, Bruno
CLI 4.1.0): the whole collection is **28 requests, 79 tests, 62 assertions, all green**. It was 78
tests (run 35873554844) until login gained the test that its token is the object register answers.

~~Measured with these exact lines on 2026-09-21 against the running API: the whole collection is
27 requests, 76 tests, 59 assertions, all green.~~ True under `--env local` - and `--env ci`, the
environment the workflow runs, had never been run at all. Its first dispatch (run 35872727972,
2026-09-23) found two requests red that `local` hid: the withdrawal, still sending its PIN in the
body after ADR-0056 moved it to a mint, and register, asserting a literal first name that only
`local` sends. `endpoints/auth` alone measured 6 requests, 16 tests, 13 assertions on 2026-09-21
under `--env local`, and was not re-measured by folder since.

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

What the repository actually runs is `.github/workflows/contract-tests.yml`. Its shape, and the
reason for each part:

```yaml
- name: Install Bruno CLI
  run: npm install -g @usebruno/cli

- name: Run Bruno tests
  working-directory: tests/api-collection     # bru runs only from the collection root
  run: |
    bru run . -r --env ci \                   # -r, or it sends nothing and still passes
      --reporter-junit ../../bruno-results.xml
```

That step used to end in `|| true`, and its command line had no `-r`. Both are gone: a job that
runs the collection and cannot fail is the same silence one layer down, and the step that follows
it now refuses a report with too few test cases, so a run that did not happen cannot look like a
run that found nothing.

## Why Bruno?

- **Git-friendly**: Collections stored as plain text files
- **Offline-first**: No cloud sync, data stays local
- **Open source**: Free forever, MIT licensed
- **Fast**: Native app, not Electron-heavy
- **Team collaboration**: via Git PRs, not cloud accounts
