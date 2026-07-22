# Security Design

**Document Version**: 1.0
**Created**: 2026-01-08
**Status**: COMPLETE

---

## 1. Overview & Principles

### 1.1 Security Objectives

| Objective | Description |
|-----------|-------------|
| **Confidentiality** | User data accessible only to authorized parties |
| **Integrity** | Data cannot be tampered with undetected |
| **Availability** | Service remains accessible to legitimate users |
| **Non-Repudiation** | Actions are attributable and logged |

### 1.2 Security Principles

1. **Defense in Depth**: Multiple security layers, no single point of failure
2. **Principle of Least Privilege**: Minimal permissions for each operation
3. **Secure by Default**: All endpoints protected unless explicitly public
4. **Fail Securely**: Errors don't leak sensitive information
5. **Zero Trust**: Verify every request, trust nothing implicitly

### 1.3 Threat Model Summary

| Threat | Mitigation |
|--------|------------|
| XSS (Cross-Site Scripting) | BFF pattern - tokens never reach browser |
| CSRF (Cross-Site Request Forgery) | SameSite=Strict cookies |
| Session Hijacking | HTTP-only, Secure cookies + short expiry |
| SQL Injection | EF Core parameterized queries |
| Brute Force | Rate limiting + account lockout |
| Man-in-the-Middle | HTTPS/TLS enforcement |

---

## 2. Authentication Architecture: BFF Pattern

### 2.1 Why BFF Pattern

Traditional token storage approaches have security weaknesses:

| Approach | Vulnerability |
|----------|---------------|
| Token in localStorage | XSS can steal token |
| Token in Redux/memory | XSS can steal token, lost on refresh |
| Token in HTTP-only cookie | Token visible in Set-Cookie header, CSRF risk |

**BFF Solution**: Tokens NEVER reach the browser. Period.

### 2.2 Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                           BFF AUTHENTICATION FLOW                            │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│  BROWSER                    BFF SERVER (.NET 10)              BACKEND API   │
│  (React)                    (YARP Gateway)                    (REST)        │
│     │                              │                              │         │
│     │  POST /bff/auth/login        │                              │         │
│     │  {email, password}           │                              │         │
│     │ ────────────────────────────▶│                              │         │
│     │                              │                              │         │
│     │                              │  POST /api/auth/login        │         │
│     │                              │ ────────────────────────────▶│         │
│     │                              │                              │         │
│     │                              │  {token, user}               │         │
│     │                              │◀──────────────────────────── │         │
│     │                              │                              │         │
│     │                              │  ┌──────────────────────┐    │         │
│     │                              │  │ Store JWT in session │    │         │
│     │                              │  │ (Memory or Redis)    │    │         │
│     │                              │  └──────────────────────┘    │         │
│     │                              │                              │         │
│     │  Set-Cookie: .AzureBank.Sess │                              │         │
│     │  (HTTP-only, Secure,         │                              │         │
│     │   SameSite=Strict)           │                              │         │
│     │                              │                              │         │
│     │  Response: {user info}       │                              │         │
│     │  *** NO TOKENS! ***          │                              │         │
│     │◀──────────────────────────── │                              │         │
│     │                              │                              │         │
│     │                              │                              │         │
│     │  GET /api/accounts           │                              │         │
│     │  Cookie: .AzureBank.Session  │                              │         │
│     │ ────────────────────────────▶│                              │         │
│     │                              │                              │         │
│     │                              │  ┌──────────────────────┐    │         │
│     │                              │  │ Validate session     │    │         │
│     │                              │  │ Get JWT from store   │    │         │
│     │                              │  │ Add Bearer header    │    │         │
│     │                              │  └──────────────────────┘    │         │
│     │                              │                              │         │
│     │                              │  GET /api/accounts           │         │
│     │                              │  Authorization: Bearer eyJ...│         │
│     │                              │ ────────────────────────────▶│         │
│     │                              │                              │         │
│     │                              │  {accounts data}             │         │
│     │                              │◀──────────────────────────── │         │
│     │                              │                              │         │
│     │  {accounts data}             │                              │         │
│     │  *** NO TOKENS! ***          │                              │         │
│     │◀──────────────────────────── │                              │         │
│                                                                              │
└─────────────────────────────────────────────────────────────────────────────┘
```

### 2.3 Security Benefits

| Aspect | Benefit |
|--------|---------|
| **XSS Protection** | Tokens NEVER reach browser - nothing to steal |
| **CSRF Protection** | SameSite=Strict cookie policy |
| **Token Refresh** | Transparent to user, handled by BFF |
| **Session Persistence** | User stays logged in on page refresh |
| **Token Size** | No 4KB cookie limit - stored server-side |
| **Multi-domain APIs** | BFF proxies requests, adds Bearer token |

### 2.4 Solution Structure

```
AzureBank/
├── src/
│   ├── AzureBank.Web/                    # React 19 SPA (frontend/)
│   │
│   ├── AzureBank.Bff/                    # NEW - BFF Gateway
│   │   ├── Controllers/
│   │   │   └── AuthController.cs         # Login, Logout, Me, Session Status
│   │   ├── Middleware/
│   │   │   ├── SessionTimeoutMiddleware.cs
│   │   │   └── TokenManagementMiddleware.cs
│   │   ├── Services/
│   │   │   ├── SessionService.cs         # Session management
│   │   │   └── TokenService.cs           # JWT handling
│   │   └── Transforms/
│   │       └── BearerTokenTransform.cs   # YARP transform
│   │
│   ├── AzureBank.Api/                    # Backend API
│   │   ├── Controllers/
│   │   ├── Services/
│   │   └── Data/
│   │
│   └── AzureBank.Shared/                 # Shared Library
│       ├── Entities/
│       ├── DTOs/
│       └── Constants/
│
└── AzureBank.sln
```

---

## 3. Session Management

### 3.1 Session Configuration

```csharp
// appsettings.json (Production defaults)
{
  "Session": {
    "CookieName": ".AzureBank.Session",
    "AccessTokenMinutes": 15,
    "InactivityTimeoutMinutes": 30,
    "AbsoluteTimeoutMinutes": 60
  }
}

// appsettings.Development.json (Testing values)
{
  "Session": {
    "AccessTokenMinutes": 5,
    "InactivityTimeoutMinutes": 10,
    "AbsoluteTimeoutMinutes": 20
  }
}
```

### 3.2 Session Cookie Configuration

```csharp
services.AddSession(options =>
{
    options.Cookie.Name = ".AzureBank.Session";
    options.Cookie.HttpOnly = true;           // Not accessible via JavaScript
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;  // HTTPS only
    options.Cookie.SameSite = SameSiteMode.Strict;  // CSRF protection
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.IsEssential = true;
});
```

### 3.3 Timeout Types

| Timeout Type | Production | Development | Description |
|--------------|------------|-------------|-------------|
| **Access Token** | 15 min | 5 min | JWT validity period |
| **Inactivity** | 30 min | 10 min | Session expires after idle |
| **Absolute** | 60 min | 20 min | Maximum session lifetime |

> The 15-min access token no longer bounds the session: the BFF silently re-mints it via
> refresh-token rotation (ADR-0021), so an active session slides within the inactivity/absolute
> budgets above. A refresh-token reuse/revocation (or logout) ends the session immediately.

### 3.4 Session Data Structure

```csharp
public class UserSession
{
    public string UserId { get; set; }
    public string AccessToken { get; set; }
    public DateTime TokenExpiry { get; set; }
    public DateTime SessionCreated { get; set; }
    public DateTime LastActivity { get; set; }
    public int AuthLevel { get; set; }  // Current authentication level
    public DateTime? PinVerifiedAt { get; set; }
}
```

### 3.5 Session Store (MVP vs Future)

| MVP | Future (Post-MVP) |
|-----|-------------------|
| In-Memory (IMemoryCache) | Redis |
| Single server only | Distributed/scalable |
| Session lost on restart | Persistent sessions |

> See [POST-MVP-ROADMAP.md](POST-MVP-ROADMAP.md) Section 1.1 for Redis migration plan.

---

## 4. Step-Up Authentication

### 4.1 Authentication Levels

| Level | Name | Required For | How to Achieve |
|-------|------|--------------|----------------|
| 0 | None | Public pages (login, register) | - |
| 1 | Session | View accounts, balances, history | Login with email/password |
| 2 | PIN | Transfers, view full account numbers | Enter 6-digit PIN |
| 3 | OTP | Large transfers (>=500), new payees | *Post-MVP* |
| 4 | Re-Auth | Password change, security settings | *Post-MVP* |

### 4.2 Step-Up Flow (MVP: Levels 1-2)

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                         STEP-UP AUTHENTICATION (MVP)                         │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│  User Action                   Required Level    Flow                        │
│  ─────────────────────────────────────────────────────────────────────────  │
│                                                                              │
│  View Dashboard                Level 1 (Session)  -> Already authenticated  │
│  View Account List             Level 1 (Session)  -> Already authenticated  │
│  View Transaction History      Level 1 (Session)  -> Already authenticated  │
│                                                                              │
│  ─────────────────────────────────────────────────────────────────────────  │
│                                                                              │
│  Make Transfer                 Level 2 (PIN)      -> Prompt for PIN         │
│  View Full Account Number      Level 2 (PIN)      -> Prompt for PIN         │
│  Add New Payee                 Level 2 (PIN)      -> Prompt for PIN         │
│                                                                              │
│  ─────────────────────────────────────────────────────────────────────────  │
│                                                                              │
│  PIN Verification Flow:                                                      │
│                                                                              │
│  ┌──────────┐    ┌──────────────┐    ┌──────────────┐    ┌──────────────┐  │
│  │  User    │    │  PIN Modal   │    │  BFF         │    │  API         │  │
│  │  Action  │───>│  Appears     │───>│  Verifies    │───>│  Returns     │  │
│  │          │    │              │    │  PIN         │    │  Result      │  │
│  └──────────┘    └──────────────┘    └──────────────┘    └──────────────┘  │
│                                                │                            │
│                                                v                            │
│                                       ┌──────────────┐                      │
│                                       │ Update       │                      │
│                                       │ AuthLevel=2  │                      │
│                                       │ PinVerified  │                      │
│                                       └──────────────┘                      │
│                                                                              │
└─────────────────────────────────────────────────────────────────────────────┘
```

### 4.3 PIN Security Configuration

```csharp
// appsettings.json
{
  "Security": {
    "PinValidityMinutes": 5,       // PIN auth expires after 5 min (prod)
    "MaxPinAttempts": 3,           // Lock after 3 failed attempts
    "LockoutMinutes": 15,          // Account locked for 15 min
    "NewPayeeDelayMinutes": 1440   // 24 hours before new payee can receive
  }
}

// appsettings.Development.json
{
  "Security": {
    "PinValidityMinutes": 10,
    "MaxPinAttempts": 10,
    "LockoutMinutes": 1,
    "NewPayeeDelayMinutes": 1      // 1 minute for testing!
  }
}
```

### 4.4 Endpoint Protection Matrix

| Endpoint | Method | Auth Level | Notes |
|----------|--------|------------|-------|
| `/bff/auth/login` | POST | 0 | Public |
| `/bff/auth/logout` | POST | 1 | Session required |
| `/bff/auth/me` | GET | 1 | Session required |
| `/bff/auth/verify-pin` | POST | 1 | Session required |
| `/api/accounts` | GET | 1 | List accounts |
| `/api/accounts` | POST | 1 | Create account |
| `/api/accounts/{id}` | GET | 1 | Account details |
| `/api/accounts/{id}/full-number` | GET | 2 | PIN required |
| `/api/transactions` | GET | 1 | Transaction history |
| `/api/transactions/deposit` | POST | 1 | Deposit (own account) |
| `/api/transactions/withdraw` | POST | 1 | Withdraw (own account) |
| `/api/transfers` | POST | 2 | PIN required |
| `/api/transfers/internal` | POST | 2 | PIN required |
| `/api/payees` | POST | 2 | PIN required |

---

## 5. Authorization Model

### 5.1 Authorization Strategy

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                         AUTHORIZATION LAYERS                                 │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│  Layer 1: Authentication Check                                               │
│  |-- Is user authenticated? (JWT valid?)                                    │
│  |-- [Authorize] attribute on controller/action                             │
│                                                                              │
│  Layer 2: Role-Based (RBAC)                                                 │
│  |-- User role (MVP: single role "User")                                    │
│  |-- [Authorize(Roles = "User")] - future Admin role                        │
│                                                                              │
│  Layer 3: Resource-Based                                                    │
│  |-- User can only access their own resources                               │
│  |-- Check: account.UserId == currentUser.Id                                │
│  |-- Implemented in service layer                                           │
│                                                                              │
│  Layer 4: Step-Up Auth                                                      │
│  |-- Check AuthLevel in session                                             │
│  |-- Prompt for PIN if Level 2 required                                     │
│                                                                              │
└─────────────────────────────────────────────────────────────────────────────┘
```

### 5.2 Resource Authorization Implementation

```csharp
public class AccountService
{
    public async Task<Account> GetAccountAsync(Guid accountId, Guid userId)
    {
        var account = await _context.Accounts.FindAsync(accountId);

        if (account == null)
            throw new NotFoundException("Account not found");

        // Resource-based authorization
        if (account.UserId != userId)
            throw new AuthorizationException("Access denied to this account");

        return account;
    }
}
```

### 5.3 JWT Claims

```csharp
public class JwtClaims
{
    public const string UserId = "sub";           // Subject (user ID)
    public const string Email = "email";
    public const string AzureTag = "azure_tag";
    public const string Role = "role";
    public const string Jti = "jti";              // JWT ID (for revocation)
}

// Example JWT payload
{
  "sub": "550e8400-e29b-41d4-a716-446655440000",
  "email": "user@example.com",
  "azure_tag": "johndoe",
  "role": "User",
  "jti": "unique-token-id",
  "iat": 1704672000,
  "exp": 1704672900
}
```

---

## 6. Input Validation

### 6.1 Validation Strategy

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                         VALIDATION LAYERS                                    │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│  Layer 1: Frontend Validation (UX)                                          │
│  |-- Immediate feedback to user                                             │
│  |-- Zod schema validation                                                  │
│  |-- NOT a security measure (can be bypassed)                               │
│                                                                              │
│  Layer 2: API Validation (Security)                                         │
│  |-- FluentValidation rules                                                 │
│  |-- Executed before controller action                                      │
│  |-- Returns 400 with validation errors                                     │
│                                                                              │
│  Layer 3: Business Rule Validation                                          │
│  |-- Service layer checks                                                   │
│  |-- e.g., sufficient balance, account ownership                            │
│  |-- Returns appropriate business exceptions                                │
│                                                                              │
│  Layer 4: Database Constraints                                              │
│  |-- NOT NULL, CHECK, UNIQUE constraints                                    │
│  |-- Last line of defense                                                   │
│                                                                              │
└─────────────────────────────────────────────────────────────────────────────┘
```

### 6.2 FluentValidation Examples

```csharp
public class TransferRequestValidator : AbstractValidator<TransferRequest>
{
    public TransferRequestValidator()
    {
        RuleFor(x => x.FromAccountId)
            .NotEmpty()
            .WithMessage("Source account is required");

        RuleFor(x => x.Amount)
            .GreaterThan(0)
            .WithMessage("Amount must be positive")
            .LessThanOrEqualTo(100000)
            .WithMessage("Maximum transfer amount is 100,000 EUR");

        RuleFor(x => x.Description)
            .MaximumLength(500)
            .WithMessage("Description cannot exceed 500 characters")
            .Must(NotContainHtml)
            .WithMessage("Description cannot contain HTML");
    }

    private bool NotContainHtml(string value)
    {
        if (string.IsNullOrEmpty(value)) return true;
        return !Regex.IsMatch(value, @"<[^>]+>");
    }
}
```

### 6.3 Sensitive Input Handling

| Input Type | Validation Rules |
|------------|-----------------|
| Email | RFC 5322 format, max 254 chars, normalized lowercase |
| Password | Min 8 chars, 1 upper, 1 lower, 1 digit, max 128 chars |
| PIN | Exactly 6 digits, no sequences (123456), no repeats (111111) |
| AzureTag | 3-30 chars, alphanumeric + underscore, lowercase |
| Amount | Positive, max 2 decimals, max 100,000 EUR |
| Account Number | Format: AB-XXXX-XXXX-XX, Luhn checksum valid |

---

## 7. Password Security

### 7.1 Argon2id Configuration

```csharp
public class PasswordHasher : IPasswordHasher
{
    // OWASP recommended parameters for Argon2id
    private const int MemorySize = 65536;     // 64 MB
    private const int Iterations = 3;          // Time cost
    private const int Parallelism = 4;         // Parallel threads
    private const int SaltLength = 16;         // 128 bits
    private const int HashLength = 32;         // 256 bits

    public string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLength);

        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = MemorySize,
            Iterations = Iterations,
            DegreeOfParallelism = Parallelism
        };

        var hash = argon2.GetBytes(HashLength);

        // Format: $argon2id$v=19$m=65536,t=3,p=4$<salt>$<hash>
        return FormatHash(salt, hash);
    }
}
```

### 7.2 Password Policy

| Requirement | Specification |
|-------------|---------------|
| Minimum Length | 8 characters |
| Maximum Length | 128 characters |
| Uppercase | At least 1 |
| Lowercase | At least 1 |
| Digit | At least 1 |
| Special Character | Optional (recommended) |
| Common Passwords | Blocked (checked against list) |

### 7.3 Account Lockout

```csharp
{
  "Security": {
    "MaxLoginAttempts": 5,
    "LockoutMinutes": 15,
    "PasswordHistoryCount": 5  // Cannot reuse last 5 passwords
  }
}
```

---

## 8. Transport Security

### 8.1 HTTPS Enforcement

```csharp
// Program.cs
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}
app.UseHttpsRedirection();
```

### 8.2 HSTS Configuration

```csharp
services.AddHsts(options =>
{
    options.Preload = true;
    options.IncludeSubDomains = true;
    options.MaxAge = TimeSpan.FromDays(365);
});
```

### 8.3 TLS Requirements

| Environment | Minimum TLS | Recommended |
|-------------|-------------|-------------|
| Development | TLS 1.2 | TLS 1.3 |
| Production | TLS 1.2 | TLS 1.3 |

---

## 9. Security Headers

### 9.1 Header Configuration

```csharp
// SecurityHeadersMiddleware.cs
public async Task InvokeAsync(HttpContext context)
{
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("X-Frame-Options", "DENY");
    context.Response.Headers.Append("X-XSS-Protection", "0");  // Deprecated, use CSP
    context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
    context.Response.Headers.Append("Permissions-Policy",
        "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()");

    await _next(context);
}
```

### 9.2 Content Security Policy

```csharp
// For BFF serving React app
context.Response.Headers.Append("Content-Security-Policy",
    "default-src 'self'; " +
    "script-src 'self'; " +
    "style-src 'self' 'unsafe-inline'; " +  // FluentUI requires inline styles
    "img-src 'self' data:; " +
    "font-src 'self'; " +
    "connect-src 'self'; " +
    "frame-ancestors 'none'; " +
    "form-action 'self';"
);
```

### 9.3 Headers Summary

| Header | Value | Purpose |
|--------|-------|---------|
| `X-Content-Type-Options` | nosniff | Prevent MIME sniffing |
| `X-Frame-Options` | DENY | Prevent clickjacking |
| `Referrer-Policy` | strict-origin-when-cross-origin | Control referrer info |
| `Content-Security-Policy` | (see above) | XSS protection |
| `Strict-Transport-Security` | max-age=31536000; includeSubDomains; preload | HTTPS enforcement |
| `Permissions-Policy` | (restricted) | Disable unnecessary APIs |

---

## 10. Rate Limiting

### 10.1 Rate Limit Configuration

```csharp
// Program.cs
builder.Services.AddRateLimiter(options =>
{
    // Global rate limit
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1)
            }));

    // Login-specific rate limit (stricter)
    options.AddPolicy("login", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(5)
            }));

    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});
```

### 10.2 Rate Limits by Endpoint

| Endpoint Type | Limit | Window | Notes |
|---------------|-------|--------|-------|
| Login/Register | 5 requests | 5 minutes | Per IP |
| PIN Verification | 3 requests | 5 minutes | Per user |
| Transfers | 10 requests | 1 minute | Per user |
| General API | 100 requests | 1 minute | Per user/IP |

### 10.3 Rate Limit Response

```json
HTTP/1.1 429 Too Many Requests
Retry-After: 60

{
  "type": "RATE_LIMIT_EXCEEDED",
  "message": "Too many requests. Please try again later.",
  "correlationId": "abc-123",
  "statusCode": 429
}
```

---

## 11. Audit Logging

### 11.1 Security Events to Log

| Event | Level | Details |
|-------|-------|---------|
| Login Success | Info | UserId, IP, UserAgent |
| Login Failure | Warning | Email (hashed), IP, Reason |
| Logout | Info | UserId, SessionDuration |
| PIN Verified | Info | UserId, Purpose |
| PIN Failed | Warning | UserId, AttemptCount |
| Transfer Initiated | Info | UserId, Amount, Recipient |
| Transfer Completed | Info | TransactionId, Amount |
| Transfer Failed | Warning | UserId, Reason |
| Account Created | Info | UserId, AccountType |
| Password Changed | Info | UserId (no password logged!) |
| Rate Limit Hit | Warning | IP, Endpoint |

### 11.2 Serilog Configuration

```csharp
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithCorrelationId()
    .Enrich.WithClientIp()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {CorrelationId} {Message:lj}{NewLine}{Exception}")
    .WriteTo.File("logs/security-.log",
        rollingInterval: RollingInterval.Day,
        fileSizeLimitBytes: 10_000_000,
        retainedFileCountLimit: 30)
    .CreateLogger();
```

### 11.3 PII Handling in Logs

| Data Type | Logging Policy |
|-----------|---------------|
| User ID | Logged (UUID, not PII) |
| Email | Hash or mask (j***@example.com) |
| Password | NEVER logged |
| PIN | NEVER logged |
| IP Address | Logged (consider privacy regulations) |
| Account Number | Masked (AB-****-****-90) |
| Transaction Amount | Logged |

---

## 12. CORS Configuration

### 12.1 CORS Policy (BFF)

```csharp
// BFF serves React app - no external CORS needed
// API only accepts requests from BFF - no CORS needed

// If API needs direct access (not recommended):
builder.Services.AddCors(options =>
{
    options.AddPolicy("BffOnly", builder =>
    {
        builder
            .WithOrigins("https://bff.azurebank.local")  // Only BFF
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});
```

### 12.2 Why CORS is Minimal

With BFF pattern:
1. React app is served BY the BFF (same origin)
2. All API calls go THROUGH the BFF (same origin)
3. API only accepts calls FROM BFF (internal network)
4. External CORS is unnecessary and disabled

---

## 13. Error Handling Security

### 13.1 Error Response Sanitization

```csharp
// Global Exception Handler
public class GlobalExceptionMiddleware
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            // Log full exception internally
            _logger.LogError(ex, "Unhandled exception: {Message}", ex.Message);

            // Return sanitized response externally
            var response = new ErrorResponse
            {
                Type = "INTERNAL_ERROR",
                Message = "An unexpected error occurred",  // Generic message
                CorrelationId = Activity.Current?.Id ?? context.TraceIdentifier,
                StatusCode = 500
            };

            // DO NOT include:
            // - Stack trace
            // - Exception type
            // - Database error details
            // - Internal file paths

            await context.Response.WriteAsJsonAsync(response);
        }
    }
}
```

### 13.2 Information Disclosure Prevention

| Scenario | Wrong Response | Correct Response |
|----------|---------------|------------------|
| User not found | "User with email x@y.com not found" | "Invalid credentials" |
| Wrong password | "Password is incorrect" | "Invalid credentials" |
| Account locked | "Account locked for 15 minutes" | "Invalid credentials" (after delay) |
| Internal error | Stack trace + SQL query | "An error occurred. Reference: ABC123" |

### 13.3 Timing Attack Prevention

```csharp
public async Task<bool> ValidateCredentialsAsync(string email, string password)
{
    var user = await _userRepository.FindByEmailAsync(email);

    if (user == null)
    {
        // Still hash to prevent timing attacks
        _passwordHasher.HashPassword("dummy-password");
        return false;
    }

    return _passwordHasher.VerifyPassword(user.PasswordHash, password);
}
```

---

## 14. Security Testing Checklist

### 14.1 Authentication Tests

- [ ] Login with valid credentials succeeds
- [ ] Login with invalid email fails (generic message)
- [ ] Login with invalid password fails (generic message)
- [ ] Account lockout after N failed attempts
- [ ] Session expires after inactivity timeout
- [ ] Session expires after absolute timeout
- [ ] Logout clears session cookie
- [ ] Cannot access protected endpoints without session

### 14.2 Authorization Tests

- [ ] User cannot access another user's accounts
- [ ] User cannot view another user's transactions
- [ ] User cannot transfer from another user's account
- [ ] PIN required for sensitive operations
- [ ] PIN expires after configured time

### 14.3 Input Validation Tests

- [ ] SQL injection attempts blocked
- [ ] XSS attempts sanitized
- [ ] Invalid email format rejected
- [ ] Negative amounts rejected
- [ ] Excessive amounts rejected
- [ ] Malformed JSON returns 400

### 14.4 Security Header Tests

- [ ] X-Content-Type-Options present
- [ ] X-Frame-Options present
- [ ] CSP header present
- [ ] HSTS header present (production)
- [ ] No server version headers

### 14.5 Rate Limiting Tests

- [ ] Login rate limit enforced
- [ ] API rate limit enforced
- [ ] Retry-After header returned on 429
- [ ] Rate limit resets after window

---

## 15. Implementation Checklist

### Phase 5 Tasks

- [ ] Create `AzureBank.Bff` project
- [ ] Configure YARP reverse proxy
- [ ] Implement session management
- [ ] Implement `AuthController` (BFF)
- [ ] Implement PIN verification
- [ ] Configure security headers middleware
- [ ] Configure rate limiting
- [ ] Update API to accept only BFF requests
- [ ] Security Specialist <-> Backend Lead Confrontation

---

**Document Status**: COMPLETE
**Phase 5**: Security Design - READY FOR IMPLEMENTATION
**Next Step**: Phase 5 Confrontation (Security Specialist <-> Backend Lead)
