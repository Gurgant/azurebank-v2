# Security Policy

This is a solo portfolio project, not a service with users: it is not deployed, holds no real data,
and has no supported version or response-time promise. To report a vulnerability, use GitHub's
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
- **A 15-minute access token, silently re-minted** by the BFF from a 7-day rotating refresh token.
  Short-lived so a leaked token is nearly worthless; re-minted server-side so the user never sees
  an expiry. An active session is bounded by inactivity and absolute timeouts, not by the token
  (ADR-0021).
- **Refresh tokens rotate on every use, and a reuse revokes the whole family** — a replayed
  refresh token is the signature of theft, so the response is to end every session descended from
  it rather than to serve the request (ADR-0021).
- **A PIN for every move of money and for closing an account, on two rails.** ~~on three rails. A
  withdrawal carries the PIN in its request body.~~ *(Struck 2026-09-21: ADR-0056 moved the
  withdrawal onto the mint rail, and the body-PIN rail it was the last user of no longer exists.)*
  A transfer, an account closure and a withdrawal first mint a one-shot authorisation with the PIN,
  bound to exactly that operation and spent once (ADR-0042, ADR-0049, ADR-0056). Only the
  account-number reveal still uses an elevation held in the BFF session (ADR-0008). Through
  the BFF, none of them is granted by the bearer token alone; presented to the API directly
  together with the BFF's service key, which since ADR-0055 only the BFF's host holds, a bearer
  token reads the full number with no PIN — measured 2026-09-15, see "Where each guarantee stops
  without the BFF" below. _(This used to end "None of them is granted by the bearer token alone",
  which was true of the money rails and not of the reveal; until 2026-09-24 it then said the bearer
  token alone read the number, which ADR-0055 ended on 2026-09-19.)_
- **PINs are hashed with Argon2id and peppered first** with a server-side secret held outside the
  database: six digits is a space you can exhaust instantly, so a stolen database must not be
  enough (ADR-0011).
  Three wrong attempts lock the PIN, counted atomically in SQL so parallel guesses cannot race
  past the limit (ADR-0010).

### Data Protection
- **No secrets in source code.** Every key comes from user-secrets locally, and the API refuses to
  start when one fails its check (`ValidateOnStart`) — except the JWT signing key, which has no
  check. Measured on 2026-09-11: with `Jwt:Secret` empty, or 12 characters long, the API started
  and answered its first registration with a 500 when it came to sign the token.
- **The PIN pepper lives outside the database** (ADR-0011), and the audit trail's chain and anchor
  keys are separate secrets from each other and from everything else (ADR-0044).
- **Nothing here configures a TLS version or encryption at rest.** The project is not deployed, so
  neither is claimed. _(This section used to list "TLS 1.3 for all connections" and "Sensitive
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
that key, which is the BFF's host and nobody else; in production the API has no public address
either. The last row's residual is closed by this and not by a PIN check inside the API, which
ADR-0055 records as decided against.

Two origins answer `/api/*`: the BFF on :5000, which the browser uses, and the API on :7215,
which accepts a bearer token from anyone holding one — its own login answers with the JWT. Several
of the controls on this page live in the BFF process, and a caller who presents a token to the API
directly never meets them. The table says which. Measured on 2026-09-15 with both hosts running
`main` (f1b3509) and the same requests sent to each origin; the values are what came back, not what
the code reads as.

| Control | Enforced in | What a bearer caller on the API gets | ADR |
|---|---|---|---|
| Anti-harvest limit on the handle lookup (`lookup` policy, 20 per 60 s per user) | BFF | 21 × `GET /api/users/{handle}`: BFF 200 ×20 then 429; API 200 ×21 | 0014 |
| Login attempt limiter (`auth` policy, 10 per 60 s per IP) | BFF | 11 wrong passwords: BFF 401 ×9 then 429 ×2, because the probe's own BFF sign-in just before them had taken the first of the ten permits the IP shares; API 401 ×11 — the API's own control is the silent lockout, which answered the next correct password with 429 `ACCOUNT_LOCKED` | 0012, 0013 |
| Cross-site state changes refused on Fetch-Metadata | BFF | `POST /api/accounts` with `Sec-Fetch-Site: cross-site`: BFF 403; API 201, account created — moot there: a bearer is presented, not ambient, so there is no cross-site request to refuse | 0018 |
| Raw auth entries closed (`/api/auth/login`, `/register` and `/refresh` answer 404 through the proxy) | BFF | login 200 with a bearer for anyone with the password; refresh is live (401 on a bogus token) | 0038 |
| Security headers: CSP, `nosniff`, `X-Frame-Options: DENY`, Referrer-Policy, Permissions-Policy, `X-XSS-Protection: 0` | BFF | none of them; neither host sends HSTS or COOP on the http development profile | 0018, 0054 |
| Global limit (300 per 60 s per IP) | BFF | 120 × `GET /api/accounts`: 200 ×120 on both origins; the API registers no rate limiter at all | 0013 |
| Session inactivity and absolute caps | BFF | not measured; the API's only bound is the token's fifteen minutes and a refresh endpoint it answers directly | 0021, 0026 |
| PIN on a transfer | API | `POST /api/transfers` without an authorisation: 401 on both origins, the same problem body | 0041, 0042 |
| **PIN on the account-number reveal** | **BFF only** | `GET /api/accounts/{id}/full-number` with no PIN ever entered: **API 200 with the full number**; BFF 403 with `X-Auth-Level-Required: 2` | 0008, 0020, 0041 |

The last row is the one to read twice. A browser has no bearer token to present to the API's own
origin — the JWT never reaches the SPA and the BFF clears any inbound `Authorization` before
proxying (ADR-0001, ADR-0038, ADR-0041) — so every reveal it can ask for goes through the BFF and
its level-2 gate. But a bearer token is a credential the API hands to whoever logs in, and a holder
of one, a leaked access token inside its fifteen minutes or an operator with `curl`, calls the API
directly and reads the unmasked number with no PIN. That is the shape ADR-0041 closed for transfers
by moving the check into the API; the reveal is the route it left on the session model on purpose,
and it now carries this measurement as a residual. Withdrawals, closures, idempotency and the PIN
lockout were not sent in this pass: their checks run inside the API's own services (ADR-0042,
ADR-0049, ADR-0009, ADR-0010), so the origin does not change them, but that is a reading of the
code, not a row of this table.

### Browser-side invariants

These are properties of the shipped SPA, not aspirations. Each is stated as a prohibition because
each is easier to violate by accident than to add deliberately, and because a reviewer can check
them in a minute.

- **No tokens, session identifiers, PINs or personal data in web storage** — not in
  `localStorage`, not in `sessionStorage`, not in IndexedDB, and not in a persisted Redux store.
  The `__Host-` session cookie described above is the deliberate exception and the only one: it is
  `HttpOnly`, so the page cannot read it, which is exactly why it is the right place for that
  state.
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

Package versions are pinned in one place through Central Package Management (ADR-0004), CodeQL
analyses every pull request, and Dependabot alerts are on (since 2026-09-25): GitHub reports a
dependency with a published advisory. Nothing updates a package on its own; a fix goes through a
pull request like any other change. _(Until 2026-09-24 this said "Security updates are applied
promptly", which nothing enforced.)_

## See Also

- [Architecture Decision Records](docs/adr/)
- [OWASP Top 10](https://owasp.org/www-project-top-ten/)
