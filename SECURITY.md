# Security Policy

This is a solo portfolio project, not a service with users: it is deployed only as a public demo,
with invented data and registration closed, and has no supported version or response-time promise.
_(Until 2026-10-06 this said "it is not deployed, holds no real data". The app had been on Azure
since 2026-10-03 ([infra/README.md](infra/README.md)), and the public demo was turned on on
2026-10-06; what it is, and what it keeps of a visitor, is under
[What the demo is, and is not](docs/testing/try-the-demo.md#what-the-demo-is-and-is-not).)_
To report a vulnerability, use GitHub's
**private vulnerability reporting** on this repository (Security → Report a vulnerability), not a
public issue. The most useful findings are about the cryptography, the authorisation rails and the
audit trail, because those are where the project makes its claims.

_(This section used to promise a 48-hour acknowledgement and give `security@azurebank.example.com`
as the address. `.example.com` is a domain reserved by RFC 2606, so mail to it reaches nobody.)_

## Security Measures

### Authentication
- **Passwords are hashed by ASP.NET Core Identity's default: PBKDF2 with HMAC-SHA512 and 100,000
  iterations** (the Identity V3 format). Measured, not read: every stored `PasswordHash` on a seeded
  store starts `AQAAAAIAAYag`, which decodes to exactly that. **That iteration count is below
  OWASP's current recommendation for this PRF (220,000)**, and PBKDF2 is not memory-hard; raising it
  is tracked as its own change, because the login path's timing defence is calibrated to this
  hasher (ADR-0012) and has to move with it.
  _(This line used to say "Argon2id password hashing (ADR-0003)". ADR-0003 decided that, and it
  was built for PINs, never for passwords — the API registers Identity with no custom password
  hasher. ADR-0003 now carries the correction.)_
- **A 15-minute access token, silently re-minted** by the BFF from the session's refresh token, its
  grant. Short-lived so a leaked token is nearly worthless; re-minted server-side so the user never
  sees an expiry. An active session is bounded by inactivity and absolute timeouts (ADR-0021), and
  by its grant: the grant lives 60 minutes from sign-in, fixed then and never extended, and no
  access token minted from it outlives it (ADR-0057).
- **One grant per session, which does not rotate, and which only the BFF's own client can
  present.** The API answers its six token endpoints (login, register, refresh, revoke, logout and
  the public demo's claim), and the session-stamp feed the BFF polls, with 404 unless the request
  comes over loopback with exactly one `X-AzureBank-Token-Road` header, besides the service key.
  _(Until 2026-10-04 this said five: the demo's claim, `POST /api/auth/demo/claim`, is the sixth,
  ADR-0063. It opens a session from no credential, so it rests on this rule alone; where
  `Demo:Enabled` is false, which is the default, it answers 404 to every caller that holds the
  key, and 401 to one that does not, as every operation does.)_
  A renewal only reads the
  grant, so a lost answer or a database outage has nothing to break. A grant whose session ended,
  presented again, is refused and recorded as a `RefreshTokenReuse` security event with an audit
  row. It revokes nothing: only code inside the API's own replica can present a grant, and
  revoking one user's tokens would not contain that code; the incident runbook does
  ([`docs/runbooks/refresh-token-reuse-recorded.md`](docs/runbooks/refresh-token-reuse-recorded.md),
  ADR-0057 §5.4).
  _(Until 2026-09-28 these two bullets said the refresh token lived 7 days, rotated on every use,
  and that a reuse revoked the whole family. Rotation turned a renewal whose answer was lost, to a
  database hang for instance, into a sign-out, and the family revoke could then sign out every
  other session of the user under a false reuse event.)_
- **A PIN for every move of money and for closing an account, on two rails.** ~~on three rails. A
  withdrawal carries the PIN in its request body.~~ *(Struck 2026-09-21: ADR-0056 moved the
  withdrawal onto the mint rail, and the body-PIN rail it was the last user of no longer exists.)*
  A transfer, an account closure and a withdrawal first mint a one-shot authorisation with the PIN,
  bound to exactly that operation and spent once (ADR-0042, ADR-0049, ADR-0056). Only the
  account-number reveal still uses an elevation held in the BFF session (ADR-0008). Through
  the BFF, none of them is granted by the bearer token alone; presented to the API directly
  together with the BFF's service key, which since ADR-0055 only the BFF presents in a deployment
  (the API keeps the same value to check it), a bearer token reads the full number with no PIN —
  as a bearer token alone did when measured on 2026-09-15, before the key existed; see "Where each
  guarantee stops without the BFF" below. _(This used to end "None of them is granted by the
  bearer token alone", which was true of the money rails and not of the reveal; until 2026-09-24
  it then said the bearer token alone read the number, which ADR-0055 ended on 2026-09-19.)_
- **PINs are hashed with Argon2id and peppered first** with a server-side secret held outside the
  database: six digits is a space you can exhaust instantly, so a stolen database must not be
  enough (ADR-0011).
  Three wrong attempts lock the PIN, counted atomically in SQL so parallel guesses cannot race
  past the limit (ADR-0010).

### Data Protection
- **No secrets in source code.** Every key comes from user-secrets locally, and the API refuses to
  start when one fails its check (`ValidateOnStart`), the JWT signing key included: at least 32
  bytes as UTF-8. So does the connection string, which must be there, parse and name a server.
  _(Until 2026-09-25 this said the JWT signing key had no check. Measured on 2026-09-11: with
  `Jwt:Secret` empty, or 12 characters long, the API started and answered its first registration
  with a 500 when it came to sign the token; measured again on 2026-09-25, both hosts as Production
  in containers, a 31-byte key and a missing connection string each started, and the first sign-in
  answered 500.)_
- **The PIN pepper lives outside the database** (ADR-0011), and the audit trail's chain and anchor
  keys are separate secrets from each other and from everything else (ADR-0044).
- **The public demo is served over HTTPS; no TLS version is claimed for it, and no encryption at
  rest.** The template turns plain HTTP off at the app's ingress (`allowInsecure: false`,
  `infra/main.bicep`), and on 2026-10-03 a request over `http://` was answered 301 to `https://`
  of the same name ([infra/README.md](infra/README.md#measured-on-azure), "Measured on Azure",
  step 21); so it was again on 2026-10-06, with the demo on. Which TLS versions that ingress
  accepts, and its certificate, are the platform's own: this project did not set them and did not
  read them. Towards the database the template sets a minimum of TLS 1.2 on the server
  (`minimalTlsVersion`; read back as 1.2 on 2026-10-03, the same section, step 4), and the app's
  connection string asks for encryption and for the server's certificate to be checked
  (`Encrypt=True;TrustServerCertificate=False`). Encryption at rest is left to the database
  service's own default: this project did not set it and did not read it, so none is claimed.
  _(Until 2026-10-06 this said "Nothing here configures a TLS version or encryption at rest. The
  project is not deployed, so neither is claimed." The public demo was turned on that day. Neither
  sentence had been exact since 2026-10-03: that day `infra/main.bicep` came to `main` with the
  settings named above, and the app was first deployed with it.)_
  _(This section used to list "TLS 1.3 for all connections" and "Sensitive
  data encrypted at rest"; no code or configuration in the repository does either.)_

### Session Security
- **`__Host-` prefixed, HttpOnly, Secure, SameSite=Strict session cookie** in production. The
  prefix is what makes the cookie unforgeable by a subdomain; HttpOnly is what makes it invisible
  to a script that gets injected (ADR-0018).
- **No `Expires` on the cookie** — the lifetime is enforced server-side by inactivity and absolute
  timeouts, so a copied cookie cannot outlive the session it came from.
- **CSRF defence in two layers**: `SameSite=Strict` plus Fetch-Metadata headers, which reject
  cross-site state-changing requests on the server rather than trusting the browser alone.
- **Same-origin topology, so there is no CORS to misconfigure.** The BFF registers none, and the
  JWT never reaches the browser at all (ADR-0001).

### Where each guarantee stops without the BFF

⚠️ **Since 2026-09-19 there is no "without the BFF" (ADR-0055).** The API refuses, before it looks
at a token, any request that does not carry the BFF's service credential — every request but two
exemptions, which carry nothing about a customer: `/health/*` in all environments, and `/openapi`
and `/scalar` in Development, where a developer opens the documentation in a browser. The
operations that documentation describes are not exempt. Measured that day with
the requests below sent straight to the API: `401 SERVICE_CREDENTIAL_REQUIRED` from the first one,
register and login included, so no bearer token is issued to begin with. What follows is kept as
measured on 2026-09-15 because it says what each control would be worth to a caller who ALSO held
that key, which in a deployment only the BFF presents (the API keeps the same value to check it);
in production the API has no public address either. _(Until 2026-09-25 this said the key was held
by "the BFF's host and nobody else".)_ The last row's residual is closed by this and not by a PIN
check inside the API, which ADR-0055 records as decided against.

Since 2026-09-28 (ADR-0057 §4.2) the key alone is not enough on the token endpoints. Login,
register, refresh, revoke, logout, the session-stamp feed and, since 2026-10-04, the public
demo's claim (ADR-0063) answer 404 unless the request also
comes over loopback and carries exactly one `X-AzureBank-Token-Road` header whose value is `bff`.
A caller holding only the key gets that 404 from any address, so it can neither sign in nor renew,
and the API issues it no bearer token. The marker is not a secret — its value is in this
repository — and the API listens on loopback only, so a caller that can reach the API at all and
holds the key can add it; the table below is what the controls are worth to that caller.

Two origins answer `/api/*`: the BFF on :5000, which the browser uses, and the API on :7215,
which on 2026-09-15 accepted a bearer token from anyone holding one — its own login answered with
the JWT. _(Until 2026-09-25 that said "accepts" and "answers": the API now refuses any caller
without the service key, as above.)_ Several of the controls on this page live in the BFF process,
and a caller who presents a token to the API directly never meets them. The table says which.
Measured on 2026-09-15 with both hosts running `main` (f1b3509) and the same requests sent to each
origin; the values are what came back, not what the code reads as. Four rows are the public
demo's (ADR-0063) and exist only where `Demo:Enabled` is true: those were measured on 2026-10-04
on the compose stack with the demo on, both hosts as Production, through the BFF's published
port, in two runs, and each says what was and was not sent to the API's own address. A value is
the one both runs gave unless its row names a run.

| Control | Enforced in | What a bearer caller on the API gets | ADR |
|---|---|---|---|
| Anti-harvest limit on the handle lookup (`lookup` policy, 20 per 60 s per user) | BFF | 21 × `GET /api/users/{handle}`: BFF 200 ×20 then 429; API 200 ×21 | 0014 |
| Login attempt limiter (`auth` policy, 10 per 60 s per IP) | BFF | 11 wrong passwords: BFF 401 ×9 then 429 ×2, because the probe's own BFF sign-in just before them had taken the first of the ten permits the IP shares; API 401 ×11 — the API's own control is the silent lockout, which answered the next correct password with 429 `ACCOUNT_LOCKED` | 0012, 0013 |
| Cross-site state changes refused on Fetch-Metadata | BFF | `POST /api/accounts` with `Sec-Fetch-Site: cross-site`: BFF 403; API 201, account created — moot there: a bearer is presented, not ambient, so there is no cross-site request to refuse | 0018 |
| Raw auth entries closed (`/api/auth/login`, `/register` and `/refresh` answer 404 through the proxy) | BFF | login 200 with a bearer for anyone with the password; refresh is live (401 on a bogus token). _Since 2026-09-28 (ADR-0057), with the key and no marker, or off loopback: 404 on both, and on revoke, logout and session-stamps. Since 2026-10-04 (ADR-0063) `/api/auth/demo/claim` is closed through the proxy too, with the demo off as with it on: on the compose stack with the demo on, sent with a live session, 404 with no body_ | 0038, 0063 |
| Security headers: CSP, `nosniff`, `X-Frame-Options: DENY`, Referrer-Policy, Permissions-Policy, `X-XSS-Protection: 0`, and `Strict-Transport-Security` in every environment but Development (since 2026-09-25) | BFF | none of them; neither host sends HSTS or COOP on the http development profile | 0018, 0054 |
| Global limit (300 per 60 s per IP) | BFF | 120 × `GET /api/accounts`: 200 ×120 on both origins; the API registers no rate limiter at all | 0013 |
| Session inactivity and absolute caps | BFF | not measured; the API's only bound is the token's fifteen minutes and a refresh endpoint it answers directly. _Since 2026-09-28 (ADR-0057) the API bounds the session too: the grant lives 60 minutes from sign-in, fixed and never extended, no access token minted from it outlives it, and refresh answers only the BFF's own client over loopback_ | 0021, 0026 |
| PIN on a transfer | API | `POST /api/transfers` without an authorisation: 401 on both origins, the same problem body | 0041, 0042 |
| The demo's caps on claiming a copy: 10 a minute for one client (the `auth` policy), `Demo:Claim:MaxPerClientPerDay` in 24 hours (10; 2 in these runs), and the pool | BFF for the `auth` policy and for naming the client; API for the day's count and the pool | Through the BFF: the eleventh sign-in of a minute 429 `RATE_LIMIT_EXCEEDED`; the third claim of one client 429 `DEMO_DAILY_LIMIT`, `retryAfterSeconds` 86394 in the first run and 86395 in the second, each with the same `Retry-After`; on a pool of one, the second claim 429 `DEMO_POOL_EMPTY`. The claim was not sent to the API's own address on the stack. A caller there, with the key and the marker, names the client's address itself, and each address it names is another client (`DemoClaimSqlServerTests`, on the API's own host: another address still claims): the day's cap is the BFF's to make true | 0063 |
| Registration closed on the demo | API and BFF, each by itself | BFF: `POST /bff/auth/register` 403 `REGISTRATION_CLOSED` for a valid body and for `{}`, the API not called. API, from inside the BFF's network namespace: 403 with the key and the marker, 404 without the marker, 401 without the key | 0063 |
| The demo's sign-in gate: only the owner of a claimed copy whose time is not over signs in | API | Through the BFF: 401 `INVALID_CREDENTIALS` for an owner taken out of its copy, for a copy past its lifetime with its right password and with a wrong one, and, in the second run, for a free copy's owner; in that run each was equal to a wrong password on a living copy in status, headers and body, but for the trace id, `Date` and the correlation id, and nothing was counted towards a lock. Not sent to the API's own address on the stack; the gate is in the API's sign-in, which is what the BFF calls | 0063 |
| A demo copy's budget of changes (`Demo:Copy:MaxWrites`, 200; 10 in these runs) | API | Through the BFF's proxy: ten deposits 201, the eleventh 429 `DEMO_COPY_LIMIT`, a read still 200, another copy's deposit 201. Not sent to the API's own address on the stack; the count is an API middleware, held on the API's own host by `DemoWriteBudgetSqlServerTests` | 0063 |
| **PIN on the account-number reveal** | **BFF only** | `GET /api/accounts/{id}/full-number` with no PIN ever entered: **API 200 with the full number**; BFF 403 with `X-Auth-Level-Required: 2` | 0008, 0020, 0041 |

The last row is the one to read twice. A browser has no bearer token to present to the API's own
origin — the JWT never reaches the SPA and the BFF clears any inbound `Authorization` before
proxying (ADR-0001, ADR-0038, ADR-0041) — so every reveal it can ask for goes through the BFF and
its level-2 gate. But a bearer token is a credential the API hands to whoever logs in, and on
2026-09-15 a holder of one, a leaked access token inside its fifteen minutes or an operator with
`curl`, could call the API directly and read the unmasked number with no PIN. That is the shape
ADR-0041 closed for transfers by moving the check into the API; the reveal is the route it left on
the session model on purpose, and it carried this measurement as a residual until 2026-09-19, when
the service key closed it: without the key the API answers `401 SERVICE_CREDENTIAL_REQUIRED` before
it looks at a token (ADR-0055). _(Until 2026-09-25 this paragraph said it in the present tense, as
if the residual were still open.)_ Withdrawals, closures, idempotency and the PIN
lockout were not sent in this pass: their checks run inside the API's own services (ADR-0042,
ADR-0049, ADR-0009, ADR-0010), so the origin does not change them, but that is a reading of the
code, not a row of this table.

### Browser-side invariants

These are properties of the shipped SPA, not aspirations. Each is stated as a prohibition because
each is easier to violate by accident than to add deliberately, and because a reviewer can check
them in a minute.

- **No tokens, session identifiers, PINs or personal data in web storage** — not in
  `localStorage`, not in `sessionStorage`, not in IndexedDB, and not in a persisted Redux store.
  The `__Host-` session cookie described above is the deliberate exception for session state: it
  is `HttpOnly`, so the page cannot read it, which is exactly why it is the right place for that
  state. **In demo mode there is a second exception, and it is one key.** Where a deployment runs
  with `Demo:Enabled` set, `localStorage["azurebank.demoCopy"]` keeps what signs in to the demo
  copy a visitor claimed, so that the visitor can come back to it: the copy's address, its
  generated password, the demo PIN, the handles of its two contacts and the instant it ends.
  None of it is a real person's, a token or a session identifier: a copy is a throwaway account
  of invented money that nobody registered for, and it is closed and deleted when its time is
  over. With the demo off the key is never read, and no screen sends the claim that writes it.
  [ADR-0063](docs/adr/0063-a-visitor-claims-a-prepared-copy-instead-of-registering.md#what-the-browser-keeps-in-demo-mode-added-2026-10-05)
  has the key's shape, when it is removed, and what a script that read it would gain. _(Until
  2026-10-05 this called the cookie "the deliberate exception and the only one".)_
- **The SPA never constructs an `Authorization` header.** Access tokens live server-side in the BFF
  and are attached by its proxy transform (ADR-0001, ADR-0021). Frontend code that builds a bearer
  header is a defect regardless of where it got the token.
- **No JS-readable claims cookie.** Session state reaches the SPA only as data from
  `/bff/auth/me`, never as a cookie the page can parse.
- **No client-side "encryption" of secrets.** Obfuscating a value the browser must also decrypt
  adds no security and hides the fact that the value should not be there.
- **No personal data in URLs.** Not in paths, not in query strings — URLs land in browser history,
  server logs and referrer headers. Identifiers in paths are opaque UUIDs, never emails or handles.
- **No `dangerouslySetInnerHTML`,** and no equivalent raw-HTML injection. Server strings render as
  text.
- **No unsubmitted financial intent survives a session boundary** (ADR-0019). A draft transfer is
  lost on expiry rather than resumed against a stale session.

### Runtime response validation

Two surfaces, two rules, and they are not the same rule:

- **`/bff/auth/*`** has no OpenAPI contract behind it, so **every response carrying a payload** is
  validated fail-closed at runtime, in production included — login, register, `me`,
  `session-status` and `verify-pin`. The Zod schemas are also the source of the TypeScript types,
  so the type and the validator cannot disagree. (`logout` and `set-pin` return no payload, so
  there is nothing to validate.)
- **`/api/*`** is validated fail-closed in production only on the **money** responses — the four
  mutation receipts, the accounts list and the transaction summary. Everything else on that
  surface validates in development and test only, deliberately: the contract there is already
  guarded by generated types, a drift gate and contract tests, and a wrong field on a transaction
  list should not take the page down.

See ADR-0023 for the reasoning and the CI gates that hold it.

## Dependencies

Package versions are pinned in one place through Central Package Management (ADR-0004), and CodeQL
analyses every pull request. Dependabot alerts are on (since 2026-09-25) and reach the frontend's
npm packages and the GitHub Actions the workflows use, but not the backend: GitHub's dependency
graph reads no version from `backend/Directory.Packages.props` and lists every NuGet package as
`>= 0`, so no advisory is matched against the backend. Nothing updates a package on its own; a fix
goes through a pull request like any other change. _(Until 2026-09-24 this said "Security updates
are applied promptly", which nothing enforced.)_

## See Also

- [Architecture Decision Records](docs/adr/)
- [OWASP Top 10](https://owasp.org/www-project-top-ten/)
