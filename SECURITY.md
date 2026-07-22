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

## Dependencies

We use Central Package Management (ADR-0004) to maintain consistent, auditable dependencies. Security updates are applied promptly.

## See Also

- [Architecture Decision Records](docs/adr/)
- [OWASP Top 10](https://owasp.org/www-project-top-ten/)
