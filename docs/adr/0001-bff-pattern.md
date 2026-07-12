# ADR-0001: Backend-For-Frontend (BFF) Pattern

**Status**: Accepted

**Date**: 2026-01-12

**Decision Makers**: Architecture Team

---

## Context

AzureBank requires a secure architecture for serving web and mobile clients. The frontend applications need to communicate with backend APIs while maintaining security best practices for authentication tokens.

Traditional approaches expose JWT tokens to the browser via localStorage or sessionStorage, making them vulnerable to XSS attacks.

## Decision Drivers

- **Security**: Tokens must not be accessible to JavaScript
- **Session Management**: Need centralized session handling
- **Rate Limiting**: Require gateway-level request throttling
- **Cross-Cutting Concerns**: Security headers, logging, CORS in one place
- **Future Scalability**: Support multiple frontend applications

## Considered Options

1. **Direct API Access**: Frontend communicates directly with API using localStorage tokens
2. **API Gateway (Kong/AWS)**: External gateway service with token relay
3. **Backend-For-Frontend (BFF)**: Custom gateway with session management

## Decision

Implement a **Backend-For-Frontend (BFF) pattern** using a dedicated ASP.NET Core service (`AzureBank.Bff`) that:

1. Handles user authentication and stores JWT tokens server-side
2. Issues HTTP-only session cookies to clients
3. Proxies API requests with automatic token injection
4. Provides rate limiting and security headers

## Rationale

### Why BFF over Direct API Access?

| Concern | Direct API | BFF Pattern |
|---------|------------|-------------|
| Token Storage | Browser (vulnerable) | Server (secure) |
| XSS Token Theft | High risk | Not possible |
| Token Refresh | Client-managed | Server-managed |
| Rate Limiting | Per-endpoint | Centralized |

### Why Custom BFF over External Gateway?

- **Full Control**: Custom session management logic
- **No Vendor Lock-in**: Standard .NET technologies
- **Cost**: No additional infrastructure costs
- **Team Expertise**: .NET skills already available
- **Integration**: Seamless with existing authentication

## Consequences

### Positive

- JWT tokens never exposed to browser JavaScript
- Centralized session management with configurable timeouts
- Single point for security headers and rate limiting
- Simplified frontend - no token management needed
- Easy to add new cross-cutting concerns

### Negative

- Additional service to deploy and maintain
- Slight latency increase (extra hop)
- Session state requires consideration for horizontal scaling
- Learning curve for YARP reverse proxy

### Neutral

- Need to implement session storage (in-memory initially, Redis for production)
- CORS configuration moves to BFF layer

## Validation

Success criteria:
- Zero token exposure incidents
- Session timeout working as configured
- Rate limiting protecting against abuse
- All security headers present in responses

## Related

- [ADR-0002: YARP Proxy Selection](./0002-yarp-proxy.md)
- [Architecture Overview](../architecture/overview.md)
- [BFF Documentation](../../src/AzureBank.Bff/README.md)

---

## References

- [Microsoft BFF Pattern](https://docs.microsoft.com/en-us/azure/architecture/patterns/backends-for-frontends)
- [OWASP Token Storage](https://cheatsheetseries.owasp.org/cheatsheets/JSON_Web_Token_for_Java_Cheat_Sheet.html#token-storage-on-client-side)
