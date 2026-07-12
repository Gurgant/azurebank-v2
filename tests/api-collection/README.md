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
| `local` | `environments/local.bru` | Local development |
| `ci` | `environments/ci.bru` | CI/CD pipeline |

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

```bash
# Install CLI
npm install -g @usebruno/cli

# Run entire collection
bru run tests/api-collection --env local

# Run specific folder
bru run tests/api-collection/endpoints/auth --env local

# Run with JUnit output
bru run tests/api-collection --env local --reporter junit --output results.xml
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
