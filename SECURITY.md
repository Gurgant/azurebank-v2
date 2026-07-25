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
- Argon2id password hashing (ADR-0003)
- JWT with a 15-min access token, silently re-minted by the BFF from a 7-day rotating refresh token (ADR-0021) — an active session is bounded by the inactivity/absolute timeouts, not the 15-min JWT
- PIN-based step-up authentication for sensitive operations (ADR-0008)

### Data Protection
- TLS 1.3 for all connections
- Sensitive data encrypted at rest
- No secrets in source code

### Session Security
- `__Host-` prefixed, HTTP-only, Secure, SameSite=Strict session cookie in production (ADR-0018)
- Session cookie (no Expires) — lifetime enforced server-side: inactivity + absolute timeouts
- CSRF protection: SameSite=Strict backed by Fetch-Metadata rejection of cross-site state-changing requests
- Same-origin topology — the BFF registers no CORS; the JWT never reaches the browser (ADR-0001)

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

The BFF surface (`/bff/auth/*`) has no OpenAPI contract behind it, so every response is validated
fail-closed at runtime against Zod schemas that are also the source of its TypeScript types — the
type and the validator cannot disagree. On the API surface, the money responses are validated
fail-closed in production and the rest in development and test only. See ADR-0023.

## Dependencies

We use Central Package Management (ADR-0004) to maintain consistent, auditable dependencies. Security updates are applied promptly.

## See Also

- [Architecture Decision Records](docs/adr/)
- [OWASP Top 10](https://owasp.org/www-project-top-ten/)
