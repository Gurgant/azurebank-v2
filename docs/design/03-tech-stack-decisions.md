# Technology Stack Decisions
## Architecture Decision Records (ADRs)

**Document Version**: 1.0
**Created**: 2025-12-16
**Status**: DRAFT - Pending Phase 1 Completion

---

## ADR-001: Frontend Framework

### Status
ACCEPTED

### Context
Need to select a frontend framework for the banking application.

### Decision
**React 19.x** with TypeScript in strict mode.

### Rationale
- Explicitly required by technical test
- Industry-standard for enterprise applications
- Excellent TypeScript support
- Large ecosystem and community
- React 19 provides latest features and performance improvements

### Consequences
- Positive: Modern framework with hooks and concurrent features
- Positive: Excellent developer experience
- Negative: React 19 is latest, some libraries may lag behind

---

## ADR-002: UI Component Library

### Status
ACCEPTED

### Context
Need a consistent UI component library for rapid development.

### Decision
**@fluentui/react-components** (FluentUI v9)

### Rationale
- Required by prompt specification
- Microsoft's official design system
- Enterprise-grade components
- Excellent accessibility (WCAG 2.1 AA)
- Built-in theming support
- TypeScript-first design

### Consequences
- Positive: Professional, consistent look
- Positive: Accessibility built-in
- Positive: Aligns with Microsoft partner (Dev4Side)
- Negative: Learning curve for v9 API
- Negative: May need custom components for specific banking features

---

## ADR-003: State Management

### Status
ACCEPTED

### Context
Need to manage application state including authentication and API data.

### Decision
**Redux Toolkit** + **RTK Query** (hybrid approach)

### Rationale
- Required by prompt specification
- Redux Toolkit simplifies Redux boilerplate
- RTK Query provides automatic caching and loading states
- Clear separation: auth state vs. API data
- Excellent TypeScript integration

### State Architecture
```
Redux Store
├── authSlice (Redux Toolkit createSlice)
│   ├── token: string | null
│   ├── user: User | null
│   └── isAuthenticated: boolean
└── API (RTK Query createApi)
    ├── accountApi
    ├── transactionApi
    └── transferApi

Local State (React Hooks)
├── Form inputs
├── Modal visibility
└── UI toggles
```

### Consequences
- Positive: Minimal boilerplate with createSlice
- Positive: Automatic caching with RTK Query
- Positive: Consistent patterns across features
- Negative: Additional complexity over plain hooks

---

## ADR-004: Backend Framework

### Status
ACCEPTED

### Context
Need to build REST APIs for banking operations.

### Decision
**.NET 10** with **ASP.NET Core Web API**

### Rationale
- Explicitly required by technical test
- Enterprise-grade performance
- Excellent for financial applications
- Strong typing with C#
- Built-in dependency injection
- Mature ecosystem

### Consequences
- Positive: High performance
- Positive: Industry standard for finance
- Negative: .NET 10 is latest, documentation may be limited

---

## ADR-005: Database

### Status
ACCEPTED

### Context
Need a relational database for banking data.

### Decision
**Microsoft SQL Server** (latest version)

### Rationale
- Explicitly required by technical test
- Enterprise-grade reliability
- Excellent for financial applications
- Strong ACID compliance
- Good Entity Framework Core support

### Consequences
- Positive: Reliable, scalable
- Positive: Excellent tooling
- Negative: Requires SQL Server installation

---

## ADR-006: ORM

### Status
ACCEPTED

### Context
Need to interact with SQL Server from .NET.

### Decision
**Entity Framework Core** (latest version compatible with .NET 10)

### Rationale
- Standard ORM for .NET
- Code-first migrations
- LINQ support
- Parameterized queries prevent SQL injection

### Consequences
- Positive: Rapid development
- Positive: Type-safe queries
- Negative: Potential performance overhead (mitigate with proper query design)

---

## ADR-007: Authentication

### Status
ACCEPTED

### Context
Need secure user authentication.

### Decision
**JWT (JSON Web Tokens)** with Bearer authentication

### Rationale
- Explicitly required by technical test
- Stateless authentication
- Industry standard
- Works well with SPAs

### Token Strategy
```
Access Token:
├── Short-lived (15-30 minutes)
├── Contains: userId, email, exp
└── Stored in Redux state (memory)

Refresh Token (Optional):
├── Long-lived (7 days)
├── HTTP-only cookie
└── Enables silent refresh
```

### Consequences
- Positive: Stateless, scalable
- Positive: No server-side session storage
- Negative: Token revocation is complex

---

## ADR-008: Password Hashing

### Status
ACCEPTED

### Context
Must store passwords securely.

### Decision
**Argon2id** (preferred) or **bcrypt** (fallback)

### Rationale
- Argon2id is OWASP recommended
- Memory-hard, resistant to GPU attacks
- If unavailable, bcrypt is acceptable

### Configuration
```
Argon2id:
├── Memory: 64 MB
├── Iterations: 3
├── Parallelism: 4
└── Salt: 16 bytes random

bcrypt:
└── Work Factor: 12
```

### Consequences
- Positive: Industry-best security
- Positive: Future-proof
- Negative: Slightly slower than MD5/SHA (intentional)

---

## ADR-009: API Mocking

### Status
ACCEPTED

### Context
Enable frontend-first development without backend dependency.

### Decision
**MSW (Mock Service Worker)**

### Rationale
- Intercepts network requests at service worker level
- Same code for tests and development
- Realistic API simulation
- No backend changes needed

### Development Workflow
```
Phase 1: Frontend Development
[React] → [RTK Query] → [MSW] → [Mock Responses]

Phase 2: Backend Integration
[React] → [RTK Query] → [.NET API] → [SQL Server]
```

### Consequences
- Positive: Parallel development
- Positive: Realistic mocking
- Positive: Easy to test
- Negative: Must maintain mock handlers

---

## ADR-010: Responsive Design

### Status
ACCEPTED

### Context
Need to support mobile and desktop users.

### Decision
**Mobile-first responsive design** using FluentUI's responsive utilities

### Breakpoints
```
Mobile:  0 - 479px
Tablet:  480 - 1023px
Desktop: 1024px+
```

### Strategy
- Design for mobile first
- Progressively enhance for larger screens
- Use FluentUI's responsive components
- Custom media query hooks where needed

### Consequences
- Positive: Works on all devices
- Positive: Better mobile experience
- Negative: Additional design/testing effort

---

## ADR-011: Project Structure

### Status
ACCEPTED

### Context
Need consistent, maintainable code organization.

### Decision
**Feature-based organization** for both frontend and backend

### Frontend Structure
```
src/
├── app/          # Store, hooks, router
├── features/     # Feature modules (auth, account, etc.)
├── components/   # Shared components
├── pages/        # Route pages
├── hooks/        # Custom hooks
├── types/        # TypeScript types
├── utils/        # Utilities
├── mocks/        # MSW handlers
└── theme/        # FluentUI theme
```

### Backend Structure
```
BankApp.API/
├── Controllers/  # API endpoints
├── Services/     # Business logic
├── Models/       # Entities, DTOs, Enums
├── Data/         # EF Core context
├── Middleware/   # Custom middleware
└── Extensions/   # Service extensions
```

### Consequences
- Positive: Clear organization
- Positive: Easy to navigate
- Positive: Scalable

---

## ADR-012: API Documentation

### Status
ACCEPTED

### Context
Need to document REST APIs for developers and testing.

### Decision
**Scalar** (NOT Swagger/Swashbuckle)

### Rationale
- Modern, cleaner UI than Swagger
- Better developer experience
- Built-in API testing
- Dark mode support
- Team member has experience with Scalar
- Actively maintained

### Implementation
```csharp
// Program.cs
builder.Services.AddOpenApi();

app.MapOpenApi();
app.MapScalarApiReference(options =>
{
    options.Title = "BankApp API";
    options.Theme = ScalarTheme.Purple;
});
```

### Consequences
- Positive: Superior UI/UX for API exploration
- Positive: Better testing experience
- Positive: Modern look aligned with FluentUI
- Negative: Less ubiquitous than Swagger (smaller community)

---

## ADR-013: HTTP Client (Frontend)

### Status
ACCEPTED

### Context
Need an HTTP client for API communication in React.

### Decision
**Axios** with RTK Query custom baseQuery

### Rationale
- Request/response interceptors for auth token injection
- Better error handling than fetch
- Request cancellation support
- Automatic JSON transformation
- Wide adoption and stability

### Implementation
```typescript
// Custom baseQuery with Axios
const axiosBaseQuery = ({ baseUrl }: { baseUrl: string }) =>
  async ({ url, method, data, params }) => {
    try {
      const result = await axios({ url: baseUrl + url, method, data, params });
      return { data: result.data };
    } catch (error) {
      return { error: { status: error.response?.status, data: error.response?.data } };
    }
  };
```

### Consequences
- Positive: Interceptors for global auth handling
- Positive: Better error objects
- Positive: Works seamlessly with RTK Query
- Negative: Additional dependency (vs native fetch)

---

## ADR-014: Build Tool (Frontend)

### Status
ACCEPTED

### Context
Need a build tool and dev server for React application.

### Decision
**Vite** (NOT Create React App)

### Rationale
- Significantly faster HMR (Hot Module Replacement)
- Native ES modules support
- Better TypeScript performance
- Modern defaults
- Create React App is deprecated

### Consequences
- Positive: Fast development experience
- Positive: Quick builds
- Positive: Modern tooling
- Negative: Different config than CRA (minor)

---

## ADR-015: Request Validation (Backend)

### Status
ACCEPTED

### Context
Need to validate incoming API requests.

### Decision
**FluentValidation**

### Rationale
- Fluent, readable validation rules
- Separation of validation logic from DTOs
- Easy to test
- Integration with ASP.NET Core pipeline
- Strong community

### Implementation
```csharp
public class DepositRequestValidator : AbstractValidator<DepositRequest>
{
    public DepositRequestValidator()
    {
        RuleFor(x => x.Amount)
            .GreaterThan(0).WithMessage("Amount must be positive");
        RuleFor(x => x.AccountId)
            .NotEmpty().WithMessage("Account ID is required");
    }
}
```

### Consequences
- Positive: Clean, testable validation
- Positive: Clear error messages
- Positive: Reusable rules

---

## ADR-016: Logging (Backend)

### Status
ACCEPTED

### Context
Need structured logging for debugging and monitoring.

### Decision
**Serilog** with structured logging

### Rationale
- Structured log events (not just strings)
- Multiple sinks (Console, File, Seq, etc.)
- Enrichers for context
- Wide adoption in .NET
- Performance optimized

### Implementation
```csharp
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File("logs/bankapp-.txt", rollingInterval: RollingInterval.Day)
    .Enrich.FromLogContext()
    .CreateLogger();
```

### Consequences
- Positive: Structured, queryable logs
- Positive: Flexible output options
- Positive: Context enrichment

---

## Summary Table

| Area | Technology | Version | Status |
|------|------------|---------|--------|
| Frontend Framework | React | 19.x | ACCEPTED |
| Language (FE) | TypeScript | 5.x Strict | ACCEPTED |
| UI Library | FluentUI | v9 | ACCEPTED |
| State Management | Redux Toolkit + RTK Query | 2.x | ACCEPTED |
| HTTP Client | Axios | 1.x | ACCEPTED |
| Build Tool | Vite | 5.x | ACCEPTED |
| API Mocking | MSW | 2.x | ACCEPTED |
| Backend Framework | .NET | 10 | ACCEPTED |
| Database | SQL Server | 2022+ | ACCEPTED |
| ORM | Entity Framework Core | 9.x | ACCEPTED |
| Authentication | JWT | - | ACCEPTED |
| Password Hashing | Argon2id | - | ACCEPTED |
| API Documentation | Scalar | Latest | ACCEPTED |
| Validation | FluentValidation | Latest | ACCEPTED |
| Logging | Serilog | Latest | ACCEPTED |
| API Style | REST | JSON | ACCEPTED |

---

## ADR-017: OpenTelemetry

### Status
ACCEPTED

### Context
Need distributed tracing and metrics for observability and debugging in production.

### Decision
**OpenTelemetry** for distributed tracing and metrics

### Rationale
- Industry standard for observability (CNCF graduated project)
- Vendor-agnostic (export to Jaeger, Zipkin, Application Insights, etc.)
- Auto-instrumentation for ASP.NET Core, EF Core, HttpClient
- Custom span/metric creation for business operations
- Native .NET support

### Implementation
```
OpenTelemetry Stack:
├── Tracing
│   ├── ASP.NET Core requests
│   ├── EF Core queries
│   ├── HttpClient calls
│   └── Custom business spans (deposits, withdrawals, transfers)
├── Metrics
│   ├── Request duration histograms
│   ├── Transaction counters
│   └── Runtime metrics (GC, threads)
└── Exporters
    ├── Console (development)
    └── OTLP (production - Jaeger/Grafana)
```

### Consequences
- Positive: Full visibility into request flow
- Positive: Performance bottleneck identification
- Positive: Correlation across services
- Negative: Additional infrastructure for production (collector, storage)

---

## ADR-018: Object Mapping

### Status
ACCEPTED

### Context
Need to map between entities and DTOs efficiently.

### Decision
**Mapperly** (NOT AutoMapper)

### Rationale
- **License**: MIT (free forever) vs AutoMapper's paid LTS
- **Performance**: Compile-time source generation (zero reflection)
- **AOT Support**: Full Native AOT compatibility
- **Type Safety**: Compile-time errors for mapping issues
- **.NET 10**: Excellent support with latest features

### Comparison
| Criteria | Mapperly | AutoMapper |
|----------|----------|------------|
| Performance | Compile-time, ~0 overhead | Runtime reflection |
| License | MIT (free) | Paid for LTS support |
| AOT | Fully supported | Limited |
| Type Safety | Compile-time | Runtime |
| Learning Curve | Simple | More complex |

### Implementation
```csharp
[Mapper]
public partial class AccountMapper
{
    public partial AccountDto ToDto(Account account);
    public partial Account ToEntity(CreateAccountRequest request);
}
```

### Consequences
- Positive: Best performance possible
- Positive: No license concerns
- Positive: Compile-time safety
- Negative: Less flexible than runtime mapping (acceptable trade-off)

---

## ADR-019: Global Exception Handler

### Status
ACCEPTED

### Context
Need consistent error responses across all API endpoints.

### Decision
**Custom Global Exception Middleware** with exception hierarchy

### Rationale
- Single point of error handling
- Consistent error response format
- Separation of internal (logged) vs external (returned) errors
- Correlation ID propagation
- Never expose stack traces to clients

### Exception Hierarchy
```
AppException (base)
├── NotFoundException (404)
├── BusinessRuleException (400)
│   └── InsufficientFundsException
├── AuthenticationException (401)
├── AuthorizationException (403)
├── ConflictException (409) - e.g., duplicate AzureTag
└── RateLimitException (429)
```

### Response Format
```json
{
  "type": "INSUFFICIENT_FUNDS",
  "message": "Insufficient funds. Available: €100.00",
  "correlationId": "abc-123-def",
  "statusCode": 400
}
```

### Consequences
- Positive: Consistent API error responses
- Positive: Clear error codes for frontend handling
- Positive: Full logging with correlation
- Negative: Additional exception classes to maintain

---

## ADR-020: Correlation ID

### Status
ACCEPTED

### Context
Need to track requests across logs, traces, and error responses.

### Decision
**Correlation ID Middleware** with header propagation

### Rationale
- Links all logs for a single request
- Enables distributed tracing correlation
- Included in error responses for user support
- Follows W3C Trace Context when available

### Flow
```
Client Request
    └── X-Correlation-ID header (optional)
          ↓
    CorrelationIdMiddleware
    ├── Use existing or generate new
    ├── Push to LogContext (Serilog)
    ├── Link to OpenTelemetry TraceId
    └── Add to Response header
          ↓
    All logs include CorrelationId
          ↓
    Error responses include CorrelationId
```

### Implementation
```csharp
public class CorrelationIdMiddleware
{
    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers["X-Correlation-ID"].FirstOrDefault()
            ?? Activity.Current?.TraceId.ToString()
            ?? Guid.NewGuid().ToString();

        context.Response.Headers["X-Correlation-ID"] = correlationId;

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await _next(context);
        }
    }
}
```

### Consequences
- Positive: Easy debugging across distributed logs
- Positive: User support can reference correlation ID
- Positive: Integrates with OpenTelemetry traces
- Negative: Minor overhead (negligible)

---

## Summary Table

| Area | Technology | Version | Status |
|------|------------|---------|--------|
| Frontend Framework | React | 19.x | ACCEPTED |
| Language (FE) | TypeScript | 5.x Strict | ACCEPTED |
| UI Library | FluentUI | v9 | ACCEPTED |
| State Management | Redux Toolkit + RTK Query | 2.x | ACCEPTED |
| HTTP Client | Axios | 1.x | ACCEPTED |
| Build Tool | Vite | 5.x | ACCEPTED |
| API Mocking | MSW | 2.x | ACCEPTED |
| Backend Framework | .NET | 10 | ACCEPTED |
| Database | SQL Server | 2022+ | ACCEPTED |
| ORM | Entity Framework Core | 9.x | ACCEPTED |
| Authentication | JWT | - | ACCEPTED |
| Password Hashing | Argon2id | - | ACCEPTED |
| API Documentation | Scalar | Latest | ACCEPTED |
| Validation | FluentValidation | Latest | ACCEPTED |
| Logging | Serilog | Latest | ACCEPTED |
| API Style | REST | JSON | ACCEPTED |
| Observability | OpenTelemetry | 1.x | ACCEPTED |
| Object Mapping | Mapperly | 3.x | ACCEPTED |
| Error Handling | Global Exception Middleware | - | ACCEPTED |
| Request Tracking | Correlation ID | - | ACCEPTED |

---

## ADR-021: BFF Pattern for Authentication

### Status
ACCEPTED

### Context
Need secure token handling for a banking application. Traditional approaches (localStorage, memory, cookies) all have security vulnerabilities:
- localStorage: XSS can steal tokens
- Redux/memory: XSS can steal tokens, tokens lost on page refresh
- HTTP-only cookies: Token visible in Set-Cookie header, CSRF risk with cookies

### Decision
**Backend-for-Frontend (BFF) Pattern** with YARP reverse proxy

### Rationale
- **Tokens never reach browser**: JWT stored server-side in session, browser only receives session cookie
- **XSS protection**: Nothing to steal via JavaScript - tokens are not in browser
- **CSRF protection**: SameSite=Strict cookies prevent cross-site requests
- **Session persistence**: User stays logged in on page refresh
- **Industry standard**: Used by major banks and financial institutions
- **YARP**: Microsoft-supported reverse proxy for .NET, easy configuration

### Architecture
```
Browser (React)  -->  BFF (.NET + YARP)  -->  Backend API
     |                      |
     |  Session Cookie      |  JWT stored in session
     |  (HTTP-only,         |  (Memory or Redis)
     |   Secure,            |
     |   SameSite=Strict)   |  Adds Bearer token to
     |                      |  proxied requests
```

### Implementation
1. **AzureBank.Bff project**: New gateway project with YARP
2. **Session management**: In-memory for MVP, Redis for future
3. **Auth endpoints**: `/bff/auth/login`, `/bff/auth/logout`, `/bff/auth/me`
4. **Token injection**: YARP transform adds Bearer token to proxied requests

### MVP vs Future
| Aspect | MVP | Future |
|--------|-----|--------|
| Session Store | In-Memory | Redis |
| Step-Up Auth | PIN (Level 2) | OTP (Level 3), Re-Auth (Level 4) |

### Consequences
- Positive: Maximum security for banking application
- Positive: Transparent token refresh for users
- Positive: Session survives page refresh
- Negative: Additional BFF project to maintain
- Negative: Slightly more complex deployment (2 services instead of 1)

### References
- See [08-security-design.md](08-security-design.md) for full security architecture
- See [POST-MVP-ROADMAP.md](POST-MVP-ROADMAP.md) for Redis migration plan

---

## Summary Table

| Area | Technology | Version | Status |
|------|------------|---------|--------|
| Frontend Framework | React | 19.x | ACCEPTED |
| Language (FE) | TypeScript | 5.x Strict | ACCEPTED |
| UI Library | FluentUI | v9 | ACCEPTED |
| State Management | Redux Toolkit + RTK Query | 2.x | ACCEPTED |
| HTTP Client | Axios | 1.x | ACCEPTED |
| Build Tool | Vite | 5.x | ACCEPTED |
| API Mocking | MSW | 2.x | ACCEPTED |
| Backend Framework | .NET | 10 | ACCEPTED |
| Database | SQL Server | 2022+ | ACCEPTED |
| ORM | Entity Framework Core | 9.x | ACCEPTED |
| Authentication | JWT + BFF Pattern | - | ACCEPTED |
| Password Hashing | Argon2id | - | ACCEPTED |
| API Documentation | Scalar | Latest | ACCEPTED |
| Validation | FluentValidation | Latest | ACCEPTED |
| Logging | Serilog | Latest | ACCEPTED |
| API Style | REST | JSON | ACCEPTED |
| Observability | OpenTelemetry | 1.x | ACCEPTED |
| Object Mapping | Mapperly | 3.x | ACCEPTED |
| Error Handling | Global Exception Middleware | - | ACCEPTED |
| Request Tracking | Correlation ID | - | ACCEPTED |
| API Gateway | YARP | Latest | ACCEPTED |

---

**Status**: FINALIZED - All ADRs approved (ADR-001 to ADR-021)
