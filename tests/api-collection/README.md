# Bruno API Collection - AzureBank

29 requests that walk the API in order, each with its tests: register and sign in, set a PIN,
accounts, a deposit, a withdrawal, transfers, idempotency, user lookup. Bruno is an open-source
API client that keeps a collection as plain text files. The `Contract tests` workflow runs this
one against a real API and SQL Server on every pull request that targets `main`, on every push to
`main`, and by hand.

## Running Tests

```bash
# The CLI, at the version the workflow pins.
npm install -g @usebruno/cli@4.1.0

# Everything below runs from the collection root, which is where bru insists on being.
cd tests/api-collection

# The whole collection. -r or it sends nothing.
bru run . -r --env local --env-var serviceKey="$YOUR_KEY" --insecure

# One folder. No -r here: its requests are that folder's direct children.
bru run endpoints/auth --env local --env-var serviceKey="$YOUR_KEY" --insecure

# The whole collection, with a JUnit report.
bru run . -r --env local --env-var serviceKey="$YOUR_KEY" --insecure --reporter-junit results.xml
```

`$YOUR_KEY` is the API's `ServiceCredential:BffKey`, which the
[local setup](../../docs/engineering-practices.md#local-setup)'s recipe generates. `--env local`
calls `https://localhost:7215`, the API's `https` profile. Three things in those lines are not
optional:

- **`cd` first.** `bru run` works only from the collection root: run from the repository root it
  answers `You can run only at the root of a collection`, having sent nothing.
- **`-r`.** Recursion is on only when no path argument is given, so `bru run .` lists the root
  folder, finds no `.bru` there, since every request lives in a subfolder, and reports
  `Requests 0 / Tests 0/0 / Status PASS`: a green run that executed nothing.
- **`--env-var serviceKey`.** `local.bru` ships it empty on purpose, so without it every request
  answers `401 SERVICE_CREDENTIAL_REQUIRED`.

`--insecure` is for ASP.NET's development certificate on `https://localhost:7215`. Node does not
read the Windows certificate store, so trusting that certificate with `dotnet dev-certs https
--trust` is not enough. Without the flag: `self-signed certificate; if the root CA is installed
locally, try running Node.js with --use-system-ca`, which is the other way out, for a run that
keeps verification on.

`--reporter-junit results.xml` writes the report into `tests/api-collection/`, and `.gitignore`
does not cover that name: keep it out of a commit.

The desktop app is `choco install bruno` on Windows, `brew install bruno` on macOS, or a download
from https://www.usebruno.com/downloads. In it: **Open Collection**, choose `tests/api-collection/`,
select an environment, run a request or a folder. With `local` it starts from the same empty
`serviceKey`; a way to supply the key in the app without saving it into that tracked file was
not measured.

## Environments

| Environment | File | Purpose |
|-------------|------|---------|
| `local` | `environments/local.bru` | Local development, against `https://localhost:7215`. `serviceKey` ships EMPTY and **stays** empty: this file is tracked and nothing in `.gitignore` covers it, so a key written here is a key committed. Pass it per run instead (above) |
| `ci` | `environments/ci.bru` | The `Contract tests` workflow, which runs `--env ci`. Self-contained: the throwaway key and the `http://localhost:5068` that workflow starts the API on. Nothing in it is a real secret |

`BrunoEnvironmentSecretTests` fails the build if that value stops being empty in `local.bru`, or if
this file starts telling you to write it there.

## What a request needs

- **The service key.** The API serves only the BFF (ADR-0055), so every request carries
  `X-AzureBank-Service-Key`, which `collection.bru` reads from `serviceKey`.
- **The token-road marker, on the token endpoints.** The six token endpoints (login, register,
  refresh, revoke, logout and the public demo's claim) and the BFF's session-stamp feed answer
  404 unless the request carries exactly one `X-AzureBank-Token-Road`, the marker the BFF's own
  client sends, and comes from the machine the API listens on (ADR-0057 §4.2). So the requests
  that call them (register, login, revoke, logout and the transfers folder's register-recipient)
  send `X-AzureBank-Token-Road: bff` themselves. It is not a secret: the API checks that exactly
  one arrived and that it says `bff`, compared exactly. No request here calls the claim or the
  feed.
- **Two 401s can answer a request here**, and `errorCode` tells them apart:
  `SERVICE_CREDENTIAL_REQUIRED` is the API refusing the caller, `INVALID_CREDENTIALS` is the
  credentials.
- **The API redirects nothing to HTTPS.** On the `https` profile
  `GET http://localhost:5068/health/live` answers 200. With a redirect in place it answered 307
  to `https://localhost:7215/health/live`. Both were measured on 2026-09-25, when the redirect
  was removed.

## Collection Structure

```
api-collection/
├── bruno.json          # Collection config
├── collection.bru      # The X-AzureBank-Service-Key header every request sends
├── environments/       # local.bru, ci.bru
└── endpoints/          # in RUN order: folder seq, then request seq
    ├── auth/           # 1: register, login, get-me, set-pin, verify-pin, revoke, logout
    ├── accounts/       # 2
    ├── transactions/   # 3: the withdrawal presents an authorisation it mints first (ADR-0056)
    ├── transfers/      # 4: so does each transfer (ADR-0042)
    ├── idempotency/    # 5: a deposit replayed, a key reused (422), missing or invalid (400)
    └── users/          # 6
```

Each folder holds a `folder.bru` with its `seq`: without one, Bruno runs folders alphabetically,
`accounts` before `auth`. The PIN that `set-pin` sets is the one every mint spends: the
withdrawal's and both transfers'. Requests hand values on in variables they set themselves
(`authToken`, `refreshToken`, `accountId`, `transactionId` and others). The environment file
supplies the rest: `baseUrl`, the test user's details and, in `ci.bru`, the `serviceKey`.

## Adding New Tests

Create a `.bru` file in the folder it belongs to, on the pattern of the requests beside it, with
the next `seq`. `BrunoCollectionRunsTests`, in the backend's test project, holds these rules
without a server:

- A variable is written before it is read, in run order. A variable read before anything writes
  it interpolates to nothing.
- No `require(...)`: Bruno's sandbox has no Node modules, and such a request is never sent. Use
  `{{$randomUUID}}`, or `bru.interpolate('{{$randomUUID}}')` when the value has to be stored.
- A post-response script publishes a variable only inside `if (res.getStatus() === 200) {`, with
  the status the request expects, and `vars:post-response` is not used: a failed request must
  publish nothing, or it replaces a good value with an empty one.
- Login signs in with the address register created, read back from register's answer.

## CI/CD Integration

`.github/workflows/contract-tests.yml` starts SQL Server in a container, resets and seeds the
database, starts the API on `http://localhost:5068` and runs, from the collection root, with
`@usebruno/cli@4.1.0`:

```bash
bru run . -r --env ci --reporter-junit ../../bruno-results.xml
```

The step can fail, and the step after it refuses a report with fewer than 100 test cases, so a
run that sent nothing cannot look like a run that found nothing. Measured on 2026-09-28 with that
command line and Bruno CLI 4.1.0, against the API started as the workflow starts it: 29 requests,
82 tests, 63 assertions, all green, and 145 test cases in the JUnit report.
