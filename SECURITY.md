# Security Policy

## Supported Versions

| Version | Supported          |
| ------- | ------------------ |
| 1.x     | :white_check_mark: |

## Reporting a Vulnerability

We take security seriously. If you discover a security vulnerability, please report it responsibly.

### How to Report

1. **Do NOT** open a public GitHub issue
2. Email security concerns to: [security@azurebank.example.com]
3. Include:
   - Description of the vulnerability
   - Steps to reproduce
   - Potential impact
   - Any suggested fixes

### What to Expect

- Acknowledgment within 48 hours
- Regular updates on progress
- Credit in security advisory (if desired)

### Scope

In scope:
- Authentication/authorization bypasses
- SQL injection, XSS, CSRF
- Sensitive data exposure
- Cryptographic weaknesses

Out of scope:
- Denial of service attacks
- Social engineering
- Physical security

## Security Measures

### Authentication
- **Argon2id password hashing** — memory-hard, so GPU and ASIC attacks lose most of their
  advantage over a defender's commodity hardware (ADR-0003).
- **A 15-minute access token, silently re-minted** by the BFF from a 7-day rotating refresh token.
  Short-lived so a leaked token is nearly worthless; re-minted server-side so the user never sees
  an expiry. An active session is bounded by inactivity and absolute timeouts, not by the token
  (ADR-0021).
- **Refresh tokens rotate on every use, and a reuse revokes the whole family** — a replayed
  refresh token is the signature of theft, so the response is to end every session descended from
  it rather than to serve the request (ADR-0021).
- **PIN step-up for sensitive operations**, with the elevation held in the BFF session rather than
  in the token, so it cannot be replayed from a captured bearer (ADR-0008).
- **PINs are peppered before hashing** with a server-side secret held outside the database: six
  digits is a space you can exhaust instantly, so a stolen database must not be enough (ADR-0011).
  Three wrong attempts lock the PIN, counted atomically in SQL so parallel guesses cannot race
  past the limit (ADR-0010).

### Data Protection
- TLS 1.3 for all connections
- Sensitive data encrypted at rest
- No secrets in source code

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
