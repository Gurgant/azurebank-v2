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
bru run . --env local --env-var serviceKey="$YOUR_KEY" --insecure
```

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

One defect is left, older than all of this and not fixed here: `login` posts `{{testEmail}}`
(`test@example.com`), but `register` creates `test.{{$timestamp}}@example.com` and never writes it
back, so every request after `register` answers 401 `INVALID_CREDENTIALS` — the collection's own
credentials, not the service key. Telling those two 401s apart is what `errorCode` is for:
`SERVICE_CREDENTIAL_REQUIRED` is this API refusing the caller, `INVALID_CREDENTIALS` is not.

## Collection Structure

```
api-collection/
├── bruno.json              # Collection config
├── environments/
│   ├── local.bru          # Local environment
│   └── ci.bru             # CI environment
└── endpoints/
    ├── auth/              # Authentication endpoints
    │   ├── register.bru
    │   ├── login.bru
    │   ├── get-me.bru
    │   ├── set-pin.bru
    │   ├── verify-pin.bru
    │   └── logout.bru
    ├── accounts/          # Account management
    │   ├── list-accounts.bru
    │   ├── create-account.bru
    │   ├── get-account.bru
    │   ├── get-balance.bru
    │   └── update-account.bru
    ├── transactions/      # Transactions
    │   ├── deposit.bru
    │   ├── withdraw.bru
    │   ├── list-transactions.bru
    │   └── get-transaction.bru
    ├── transfers/         # Money transfers
    │   ├── transfer-to-user.bru
    │   └── internal-transfer.bru
    └── users/             # User lookup
        ├── search-users.bru
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

# Run entire collection
bru run tests/api-collection --env local --env-var serviceKey="$YOUR_KEY" --insecure

# Run specific folder
bru run tests/api-collection/endpoints/auth --env local --env-var serviceKey="$YOUR_KEY" --insecure

# Run with JUnit output
bru run tests/api-collection --env local --env-var serviceKey="$YOUR_KEY" --reporter junit --output results.xml --insecure
```

## Test Workflow

The recommended order for running tests:

1. **Register** - Creates new user and gets token
2. **Set PIN** - Sets up PIN for withdrawals
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

```yaml
- name: Install Bruno CLI
  run: npm install -g @usebruno/cli

- name: Run API Tests
  run: bru run tests/api-collection --env ci --reporter junit --output test-results.xml
```

## Why Bruno?

- **Git-friendly**: Collections stored as plain text files
- **Offline-first**: No cloud sync, data stays local
- **Open source**: Free forever, MIT licensed
- **Fast**: Native app, not Electron-heavy
- **Team collaboration**: via Git PRs, not cloud accounts
