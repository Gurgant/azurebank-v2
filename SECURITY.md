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
- JWT with short expiration (15 min access, 60 min refresh)
- PIN-based step-up authentication for sensitive operations (ADR-0008)

### Data Protection
- TLS 1.3 for all connections
- Sensitive data encrypted at rest
- No secrets in source code

### Session Security
- HTTP-only, Secure, SameSite cookies
- Session timeout after inactivity
- CSRF protection via SameSite cookies

## Dependencies

We use Central Package Management (ADR-0004) to maintain consistent, auditable dependencies. Security updates are applied promptly.

## See Also

- [Architecture Decision Records](docs/adr/)
- [OWASP Top 10](https://owasp.org/www-project-top-ten/)
