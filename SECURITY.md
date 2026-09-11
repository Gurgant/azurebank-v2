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
- **A PIN for every move of money and for closing an account, on three rails.** A withdrawal
  carries the PIN in its request body. A transfer and an account closure first mint a one-shot
  authorisation with the PIN, bound to exactly that operation and spent once (ADR-0042, ADR-0049).
  Only the account-number reveal still uses an elevation held in the BFF session (ADR-0008). None
  of them is granted by the bearer token alone.
- **PINs are hashed with Argon2id and peppered first** with a server-side secret held outside the
  database: six digits is a space you can exhaust instantly, so a stolen database must not be
  enough (ADR-0011).
  Three wrong attempts lock the PIN, counted atomically in SQL so parallel guesses cannot race
  past the limit (ADR-0010).

### Data Protection
- **No secrets in source code.** Every key comes from user-secrets locally, and the API refuses to
  start without them (`ValidateOnStart`).
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

We use Central Package Management (ADR-0004) to maintain consistent, auditable dependencies. Security updates are applied promptly.

## See Also

- [Architecture Decision Records](docs/adr/)
- [OWASP Top 10](https://owasp.org/www-project-top-ten/)
