# AzureBank BFF Layer - Complete Implementation Plan

## Document Information
- **Created**: 2026-01-21
- **Purpose**: Complete BFF implementation while maintaining API independence
- **Key Constraint**: API must work BOTH with and without BFF

---

# PART 1: COMPREHENSIVE AUDIT REPORT

## 1.1 Current Architecture State

### Port Configuration
| Service | HTTP | HTTPS | Status |
|---------|------|-------|--------|
| API | 5068 | 7215 | Working |
| BFF | 5000 | 5001 | Working |
| Frontend | 5173 | - | Vite Dev |

### Communication Flow (Current)
```
┌─────────────────────────────────────────────────────────────────────────────┐
│                              BROWSER                                         │
│                                                                              │
│   ┌─────────────────────┐              ┌─────────────────────┐              │
│   │  WITHOUT BFF        │              │  WITH BFF           │              │
│   │  (Direct API)       │              │  (Recommended)      │              │
│   │                     │              │                     │              │
│   │  JWT in localStorage│              │  Session Cookie     │              │
│   │  XSS vulnerable     │              │  HttpOnly, Secure   │              │
│   └──────────┬──────────┘              └──────────┬──────────┘              │
└──────────────┼──────────────────────────────────────┼────────────────────────┘
               │                                      │
               │ Authorization: Bearer <jwt>          │ Cookie: .AzureBank.Session
               │                                      │
               ▼                                      ▼
┌──────────────────────────┐              ┌──────────────────────────────────┐
│      API (7215)          │              │        BFF (5001)                │
│                          │              │                                   │
│  - JWT Validation        │              │  - Session Store (In-Memory)     │
│  - Business Logic        │              │  - JWT stored server-side        │
│  - Database Access       │              │  - YARP Reverse Proxy            │
│                          │              │  - Bearer Token Injection        │
│  Works independently!    │              │                                   │
└──────────────────────────┘              └─────────────┬─────────────────────┘
                                                        │
                                                        │ Authorization: Bearer <jwt>
                                                        ▼
                                          ┌──────────────────────────────────┐
                                          │        API (7215)                │
                                          │                                   │
                                          │  Same validation, same logic     │
                                          │  Doesn't know request came       │
                                          │  from BFF or direct client       │
                                          └──────────────────────────────────┘
```

## 1.2 What EXISTS (Complete Audit)

### API Layer (AzureBank.Api)
| Component | Status | File | Notes |
|-----------|--------|------|-------|
| JWT Generation | ✅ Complete | `Services/JwtService.cs` | 15 min expiry |
| JWT Validation | ✅ Complete | `Extensions/ServiceCollectionExtensions.cs` | Zero clock skew |
| Login Endpoint | ✅ Complete | `Controllers/AuthController.cs` | Returns JWT + user |
| Register Endpoint | ✅ Complete | `Controllers/AuthController.cs` | Returns JWT + user |
| PIN Verification | ✅ Complete | `Controllers/AuthController.cs` | Step-up auth |
| Logout Endpoint | ⚠️ Partial | `Controllers/AuthController.cs` | No token revocation |
| Refresh Token Entity | ✅ Complete | `Shared/Entities/RefreshToken.cs` | Ready for use |
| Refresh Token Table | ✅ Complete | `Infrastructure/Migrations` | DB ready |
| **Refresh Endpoint** | ❌ Missing | - | Critical gap |
| **Token Revocation** | ❌ Missing | - | Logout incomplete |

### BFF Layer (AzureBank.Bff)
| Component | Status | File | Notes |
|-----------|--------|------|-------|
| Session Management | ✅ Complete | `Services/SessionService.cs` | Crypto-secure IDs |
| Session Store | ⚠️ In-Memory | `Services/InMemoryTokenStore.cs` | Lost on restart |
| YARP Proxy | ✅ Complete | `Program.cs` | Routes `/api/*` |
| Bearer Token Injection | ✅ Complete | `Transforms/BearerTokenTransformProvider.cs` | Works |
| Login via BFF | ✅ Complete | `Controllers/BffAuthController.cs` | Creates session |
| Logout via BFF | ✅ Complete | `Controllers/BffAuthController.cs` | Clears session |
| PIN Verification | ✅ Complete | `Controllers/BffAuthController.cs` | AuthLevel 2 |
| Security Headers | ✅ Complete | `Middleware/SecurityHeadersMiddleware.cs` | OWASP |
| Session Activity | ✅ Complete | `Middleware/SessionActivityMiddleware.cs` | Updates LastActivity |
| AuthLevel Middleware | ✅ Complete | `Middleware/AuthLevelMiddleware.cs` | Enforces PIN |
| Rate Limiting | ✅ Complete | `Program.cs` | 100 req/min |
| **Refresh Session** | ⚠️ Defined | `Services/SessionService.cs` | Never called |
| **Distributed Store** | ❌ Missing | - | Need Redis/Garnet |
| **Health Checks** | ❌ Missing | - | No monitoring |
| **Resilience** | ❌ Missing | - | No Polly |

### Infrastructure
| Component | Status | Notes |
|-----------|--------|-------|
| Docker | ❌ None | No containerization |
| Docker Compose | ❌ None | No orchestration |
| Kubernetes | ❌ None | No K8s manifests |
| Redis/Garnet | ❌ None | No distributed cache |
| Testcontainers | ✅ Available | For testing only |

## 1.3 Critical Gaps Analysis

### Gap 1: Token Refresh (CRITICAL)
```
CURRENT STATE:
- API JWT expires in 15 minutes
- BFF session timeout is 60 minutes
- No refresh mechanism exists
- User forced to re-login after 15 minutes

IMPACT:
- Poor user experience
- Frequent re-authentication
- Session/token expiry mismatch
```

### Gap 2: Session Persistence (CRITICAL)
```
CURRENT STATE:
- InMemoryTokenStore uses ConcurrentDictionary
- All sessions lost on BFF restart
- Cannot scale horizontally (multiple BFF instances)

IMPACT:
- Users logged out on deployment
- No high availability
- No horizontal scaling
```

### Gap 3: Token Revocation (MEDIUM)
```
CURRENT STATE:
- Logout endpoint exists but is placeholder
- JWT remains valid until expiry
- No blacklist/revocation mechanism

IMPACT:
- Compromised tokens stay valid
- Logout is not truly secure
```

---

# PART 2: IMPLEMENTATION PLAN

## Design Principle: API Independence
```
The API MUST continue to work independently:
- Direct API calls with JWT remain valid
- BFF is an OPTIONAL security layer
- No breaking changes to API contracts
- Feature flags for BFF-specific behavior
```

---

## PHASE 1: API Enhancements (No BFF Changes)
**Goal**: Add refresh token support to API without breaking existing clients

### Sub-Phase 1.1: Implement Refresh Token Service

#### Step 1.1.1: Create IRefreshTokenService Interface
**File**: `src/AzureBank.Api/Services/Interfaces/IRefreshTokenService.cs`
```csharp
public interface IRefreshTokenService
{
    Task<(string Token, DateTime ExpiresAt)> GenerateRefreshTokenAsync(
        Guid userId, string ipAddress, string userAgent);

    Task<(string AccessToken, string RefreshToken, DateTime AccessExpires, DateTime RefreshExpires)?>
        RefreshTokensAsync(string refreshToken, string ipAddress, string userAgent);

    Task RevokeTokenAsync(string refreshToken);
    Task RevokeAllUserTokensAsync(Guid userId);
}
```

#### Step 1.1.2: Implement RefreshTokenService
**File**: `src/AzureBank.Api/Services/Implementations/RefreshTokenService.cs`

**Sub-steps**:
1. Inject `AzureBankDbContext`, `IJwtService`, `IOptions<JwtOptions>`
2. Implement `GenerateRefreshTokenAsync`:
   - Generate 32-byte random token
   - Hash with SHA256 before storing
   - Save to database with IP/UserAgent
   - Return plain token (only time it's visible)
3. Implement `RefreshTokensAsync`:
   - Hash incoming token
   - Find in database by hash
   - Validate: not expired, not revoked, IP matches (optional)
   - Rotate: revoke old, create new
   - Generate new access token
   - Detect theft: if revoked token reused → revoke ALL user tokens
4. Implement `RevokeTokenAsync`:
   - Find by hash, set `RevokedAt`
5. Implement `RevokeAllUserTokensAsync`:
   - Revoke all active tokens for user (security breach response)

#### Step 1.1.3: Register Service in DI
**File**: `src/AzureBank.Api/Extensions/ServiceCollectionExtensions.cs`
```csharp
services.AddScoped<IRefreshTokenService, RefreshTokenService>();
```

### Sub-Phase 1.2: Add Refresh Endpoint to AuthController

#### Step 1.2.1: Create DTOs
**File**: `src/AzureBank.Api/DTOs/Auth/RefreshTokenRequest.cs`
```csharp
public record RefreshTokenRequest(string RefreshToken);
```

**File**: `src/AzureBank.Api/DTOs/Auth/RefreshTokenResponse.cs`
```csharp
public record RefreshTokenResponse(
    string AccessToken,
    string RefreshToken,
    DateTime AccessTokenExpiresAt,
    DateTime RefreshTokenExpiresAt);
```

#### Step 1.2.2: Add Refresh Endpoint
**File**: `src/AzureBank.Api/Controllers/AuthController.cs`

```csharp
[HttpPost("refresh")]
[AllowAnonymous]  // Token itself is the auth
public async Task<IActionResult> RefreshToken([FromBody] RefreshTokenRequest request)
{
    var ipAddress = GetClientIpAddress();
    var userAgent = Request.Headers.UserAgent.ToString();

    var result = await _refreshTokenService.RefreshTokensAsync(
        request.RefreshToken, ipAddress, userAgent);

    if (result is null)
        return Unauthorized(new { message = "Invalid or expired refresh token" });

    return Ok(new ApiResponse<RefreshTokenResponse>
    {
        Data = new RefreshTokenResponse(
            result.Value.AccessToken,
            result.Value.RefreshToken,
            result.Value.AccessExpires,
            result.Value.RefreshExpires),
        Message = "Token refreshed successfully"
    });
}
```

#### Step 1.2.3: Modify Login to Return Refresh Token
**File**: `src/AzureBank.Api/Controllers/AuthController.cs`

Update `Login` method:
```csharp
// After generating access token...
var (refreshToken, refreshExpires) = await _refreshTokenService
    .GenerateRefreshTokenAsync(user.Id, ipAddress, userAgent);

return Ok(new ApiResponse<LoginResponse>
{
    Data = new LoginResponse
    {
        Token = accessToken,
        RefreshToken = refreshToken,           // NEW
        ExpiresAt = accessExpires,
        RefreshTokenExpiresAt = refreshExpires, // NEW
        User = userInfo
    }
});
```

#### Step 1.2.4: Update LoginResponse DTO
**File**: `src/AzureBank.Api/DTOs/Auth/LoginResponse.cs`
```csharp
public class LoginResponse
{
    public required string Token { get; set; }
    public string? RefreshToken { get; set; }           // NEW (nullable for backward compat)
    public required DateTime ExpiresAt { get; set; }
    public DateTime? RefreshTokenExpiresAt { get; set; } // NEW
    public required UserLoginInfo User { get; set; }
}
```

### Sub-Phase 1.3: Implement Token Revocation on Logout

#### Step 1.3.1: Update Logout Endpoint
**File**: `src/AzureBank.Api/Controllers/AuthController.cs`

```csharp
[HttpPost("logout")]
[Authorize]
public async Task<IActionResult> Logout()
{
    var userId = GetCurrentUserId();
    if (userId is null)
        return Unauthorized();

    // Revoke all refresh tokens for this user
    await _refreshTokenService.RevokeAllUserTokensAsync(userId.Value);

    _logger.LogInformation("User {UserId} logged out, all tokens revoked", userId);

    return Ok(new ApiResponse<object>
    {
        Message = "Logged out successfully"
    });
}
```

### Sub-Phase 1.4: Testing Phase 1

#### Step 1.4.1: Manual API Testing
```bash
# 1. Login - should return refresh token
POST https://localhost:7215/api/auth/login
Content-Type: application/json
{"email": "test@example.com", "password": "Test123!"}

# Expected: accessToken + refreshToken in response

# 2. Refresh - should return new tokens
POST https://localhost:7215/api/auth/refresh
Content-Type: application/json
{"refreshToken": "<refresh_token_from_login>"}

# Expected: new accessToken + new refreshToken

# 3. Use old refresh token (should fail - rotation)
POST https://localhost:7215/api/auth/refresh
{"refreshToken": "<old_refresh_token>"}

# Expected: 401 Unauthorized
```

#### Step 1.4.2: Verify Backward Compatibility
```bash
# Direct API still works with just access token
GET https://localhost:7215/api/accounts
Authorization: Bearer <access_token>

# Expected: 200 OK with accounts
```

---

## PHASE 2: BFF Token Refresh Integration
**Goal**: BFF automatically refreshes tokens, transparent to browser

### Sub-Phase 2.1: Update BFF Session Model

#### Step 2.1.1: Add RefreshToken to UserSession
**File**: `src/AzureBank.Bff/Models/UserSession.cs`

```csharp
public class UserSession
{
    // ... existing properties ...

    /// <summary>
    /// Refresh token for obtaining new access tokens.
    /// Stored server-side, never exposed to browser.
    /// </summary>
    public string? RefreshToken { get; set; }

    /// <summary>
    /// When the refresh token expires (typically 60 min for banking).
    /// </summary>
    public DateTime? RefreshTokenExpiry { get; set; }

    /// <summary>
    /// Check if we should proactively refresh (80% of token lifetime).
    /// </summary>
    public bool ShouldRefresh =>
        TokenExpiry.AddMinutes(-3) <= DateTime.UtcNow; // Refresh 3 min before expiry
}
```

### Sub-Phase 2.2: Create Token Refresh Service

#### Step 2.2.1: Create Interface
**File**: `src/AzureBank.Bff/Services/Interfaces/ITokenRefreshService.cs`

```csharp
public interface ITokenRefreshService
{
    /// <summary>
    /// Attempt to refresh tokens for a session.
    /// Returns true if successful, false if refresh failed (user must re-login).
    /// </summary>
    Task<bool> TryRefreshSessionAsync(string sessionId);
}
```

#### Step 2.2.2: Implement TokenRefreshService
**File**: `src/AzureBank.Bff/Services/Implementations/TokenRefreshService.cs`

```csharp
public class TokenRefreshService : ITokenRefreshService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISessionService _sessionService;
    private readonly ILogger<TokenRefreshService> _logger;

    public async Task<bool> TryRefreshSessionAsync(string sessionId)
    {
        var session = _sessionService.GetSession(sessionId);
        if (session?.RefreshToken is null)
        {
            _logger.LogWarning("No refresh token for session {SessionId}",
                sessionId[..Math.Min(8, sessionId.Length)]);
            return false;
        }

        try
        {
            var client = _httpClientFactory.CreateClient("BackendApi");
            var response = await client.PostAsJsonAsync("/api/auth/refresh",
                new { refreshToken = session.RefreshToken });

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Token refresh failed for session {SessionId}: {Status}",
                    sessionId[..8], response.StatusCode);
                return false;
            }

            var result = await response.Content
                .ReadFromJsonAsync<ApiResponse<RefreshTokenResponse>>();

            // Update session with new tokens
            _sessionService.RefreshSession(
                sessionId,
                result!.Data!.AccessToken,
                result.Data.AccessTokenExpiresAt);

            // Also update refresh token (rotation)
            session.RefreshToken = result.Data.RefreshToken;
            session.RefreshTokenExpiry = result.Data.RefreshTokenExpiresAt;

            _logger.LogDebug("Session {SessionId} tokens refreshed successfully",
                sessionId[..8]);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing tokens for session {SessionId}",
                sessionId[..8]);
            return false;
        }
    }
}
```

#### Step 2.2.3: Register Service
**File**: `src/AzureBank.Bff/Program.cs`
```csharp
builder.Services.AddSingleton<ITokenRefreshService, TokenRefreshService>();
```

### Sub-Phase 2.3: Integrate Refresh into YARP Transform

#### Step 2.3.1: Update BearerTokenTransformProvider
**File**: `src/AzureBank.Bff/Transforms/BearerTokenTransformProvider.cs`

```csharp
public void Apply(TransformBuilderContext context)
{
    context.AddRequestTransform(async transformContext =>
    {
        var sessionService = transformContext.HttpContext.RequestServices
            .GetRequiredService<ISessionService>();
        var refreshService = transformContext.HttpContext.RequestServices
            .GetRequiredService<ITokenRefreshService>();
        var sessionOptions = transformContext.HttpContext.RequestServices
            .GetRequiredService<IOptions<BffSessionOptions>>();

        var cookieName = sessionOptions.Value.CookieName;

        if (transformContext.HttpContext.Request.Cookies
            .TryGetValue(cookieName, out var sessionId))
        {
            var session = sessionService.GetSession(sessionId);

            if (session != null)
            {
                // Check if token needs refresh (proactive refresh)
                if (session.ShouldRefresh && session.RefreshToken != null)
                {
                    await refreshService.TryRefreshSessionAsync(sessionId);
                    // Re-fetch session after refresh
                    session = sessionService.GetSession(sessionId);
                }

                if (session != null && sessionService.TryGetToken(sessionId, out var token))
                {
                    transformContext.ProxyRequest.Headers.Authorization =
                        new AuthenticationHeaderValue("Bearer", token);
                }
            }
        }
    });
}
```

### Sub-Phase 2.4: Update BFF Login to Store Refresh Token

#### Step 2.4.1: Update BffAuthController Login
**File**: `src/AzureBank.Bff/Controllers/BffAuthController.cs`

```csharp
[HttpPost("login")]
public async Task<IActionResult> Login([FromBody] LoginRequest request)
{
    // ... existing code to call API ...

    var loginResponse = apiResponse!.Data!;

    // Create session with refresh token
    var sessionId = _sessionService.CreateSession(
        loginResponse.Token,
        loginResponse.ExpiresAt,
        loginResponse.User,
        loginResponse.RefreshToken,           // NEW
        loginResponse.RefreshTokenExpiresAt); // NEW

    // ... rest of method ...
}
```

#### Step 2.4.2: Update SessionService.CreateSession
**File**: `src/AzureBank.Bff/Services/Implementations/SessionService.cs`

```csharp
public string CreateSession(
    string accessToken,
    DateTime tokenExpiry,
    UserLoginInfo userInfo,
    string? refreshToken = null,          // NEW
    DateTime? refreshTokenExpiry = null)  // NEW
{
    var sessionId = GenerateSecureSessionId();
    var now = DateTime.UtcNow;

    var session = new UserSession
    {
        SessionId = sessionId,
        AccessToken = accessToken,
        TokenExpiry = tokenExpiry,
        SessionCreated = now,
        LastActivity = now,
        AuthLevel = 1,
        RefreshToken = refreshToken,           // NEW
        RefreshTokenExpiry = refreshTokenExpiry, // NEW
        UserInfo = new UserSessionInfo { /* ... */ }
    };

    _tokenStore.StoreSessionAsync(session).GetAwaiter().GetResult();
    return sessionId;
}
```

### Sub-Phase 2.5: Testing Phase 2

#### Step 2.5.1: BFF Token Refresh Test
```bash
# 1. Login via BFF
POST https://localhost:5001/bff/auth/login
Content-Type: application/json
{"email": "test@example.com", "password": "Test123!"}

# Save the session cookie

# 2. Wait 12 minutes (token expires at 15)

# 3. Make API request via BFF
GET https://localhost:5001/api/accounts
Cookie: .AzureBank.Session=<session_id>

# Expected: 200 OK (token was auto-refreshed)

# 4. Check session status
GET https://localhost:5001/bff/auth/session-status
Cookie: .AzureBank.Session=<session_id>

# Expected: Shows new token expiry time
```

---

## PHASE 3: Distributed Session Store (Garnet)
**Goal**: Sessions persist across restarts and scale horizontally

### Sub-Phase 3.1: Infrastructure Setup

#### Step 3.1.1: Create Docker Compose
**File**: `docker/docker-compose.yml`

```yaml
version: '3.8'

services:
  garnet:
    image: ghcr.io/microsoft/garnet:latest
    container_name: azurebank-garnet
    ports:
      - "6379:6379"
    volumes:
      - garnet-data:/data
    command: ["--checkpointdir", "/data/checkpoints", "--logdir", "/data/logs"]
    healthcheck:
      test: ["CMD", "redis-cli", "ping"]
      interval: 10s
      timeout: 5s
      retries: 3

volumes:
  garnet-data:
```

#### Step 3.1.2: Add Garnet Package
**File**: `src/AzureBank.Bff/AzureBank.Bff.csproj`
```xml
<PackageReference Include="StackExchange.Redis" Version="2.7.33" />
```

### Sub-Phase 3.2: Implement GarnetTokenStore

#### Step 3.2.1: Create GarnetTokenStore
**File**: `src/AzureBank.Bff/Services/Implementations/GarnetTokenStore.cs`

```csharp
public class GarnetTokenStore : ITokenStoreService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IOptions<BffSessionOptions> _options;
    private readonly ILogger<GarnetTokenStore> _logger;
    private readonly string _keyPrefix = "bff:session:";

    public GarnetTokenStore(
        IConnectionMultiplexer redis,
        IOptions<BffSessionOptions> options,
        ILogger<GarnetTokenStore> logger)
    {
        _redis = redis;
        _options = options;
        _logger = logger;
    }

    public async Task<UserSession?> GetSessionAsync(string sessionId)
    {
        var db = _redis.GetDatabase();
        var key = _keyPrefix + sessionId;

        var data = await db.StringGetAsync(key);
        if (data.IsNullOrEmpty)
            return null;

        var session = JsonSerializer.Deserialize<UserSession>(data!);

        // Validate session
        if (session != null && IsSessionValid(session))
        {
            return session;
        }

        // Remove invalid session
        await db.KeyDeleteAsync(key);
        return null;
    }

    public async Task StoreSessionAsync(UserSession session)
    {
        var db = _redis.GetDatabase();
        var key = _keyPrefix + session.SessionId;

        var ttl = TimeSpan.FromMinutes(_options.Value.AbsoluteTimeoutMinutes);
        var json = JsonSerializer.Serialize(session);

        await db.StringSetAsync(key, json, ttl);
        _logger.LogDebug("Session stored: {SessionId}", session.SessionId[..8]);
    }

    public async Task UpdateSessionAsync(UserSession session)
    {
        await StoreSessionAsync(session); // Garnet handles TTL refresh
    }

    public async Task RemoveSessionAsync(string sessionId)
    {
        var db = _redis.GetDatabase();
        var key = _keyPrefix + sessionId;
        await db.KeyDeleteAsync(key);
        _logger.LogDebug("Session removed: {SessionId}", sessionId[..8]);
    }

    public async Task CleanupExpiredSessionsAsync()
    {
        // Garnet handles expiry via TTL - no cleanup needed
        _logger.LogDebug("Cleanup skipped - Garnet handles TTL expiry");
        await Task.CompletedTask;
    }

    private bool IsSessionValid(UserSession session)
    {
        var now = DateTime.UtcNow;
        var options = _options.Value;

        // Check absolute timeout
        if (now >= session.SessionCreated.AddMinutes(options.AbsoluteTimeoutMinutes))
            return false;

        // Check inactivity timeout
        if (now >= session.LastActivity.AddMinutes(options.InactivityTimeoutMinutes))
            return false;

        return true;
    }
}
```

### Sub-Phase 3.3: Configuration & Registration

#### Step 3.3.1: Add Configuration
**File**: `src/AzureBank.Bff/appsettings.json`
```json
{
  "Garnet": {
    "ConnectionString": "localhost:6379",
    "InstanceName": "AzureBank:"
  }
}
```

#### Step 3.3.2: Update Program.cs
**File**: `src/AzureBank.Bff/Program.cs`

```csharp
// Session storage - use Garnet in production, InMemory for dev
if (builder.Environment.IsDevelopment() &&
    string.IsNullOrEmpty(builder.Configuration["Garnet:ConnectionString"]))
{
    builder.Services.AddSingleton<ITokenStoreService, InMemoryTokenStore>();
}
else
{
    builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
    {
        var config = builder.Configuration["Garnet:ConnectionString"] ?? "localhost:6379";
        return ConnectionMultiplexer.Connect(config);
    });
    builder.Services.AddSingleton<ITokenStoreService, GarnetTokenStore>();
}
```

### Sub-Phase 3.4: Testing Phase 3

#### Step 3.4.1: Garnet Persistence Test
```bash
# 1. Start Garnet
docker-compose -f docker/docker-compose.yml up -d

# 2. Start BFF (with Garnet connection)
dotnet run --project src/AzureBank.Bff

# 3. Login and get session
POST https://localhost:5001/bff/auth/login

# 4. Verify session exists in Garnet
redis-cli GET "bff:session:<session_id>"

# 5. Restart BFF
# Ctrl+C, then dotnet run again

# 6. Session should still work
GET https://localhost:5001/bff/auth/me
Cookie: .AzureBank.Session=<session_id>

# Expected: 200 OK (session persisted!)
```

---

## PHASE 4: Production Hardening

### Sub-Phase 4.1: Health Checks

#### Step 4.1.1: Add Health Check Packages
```xml
<PackageReference Include="AspNetCore.HealthChecks.Redis" Version="8.0.1" />
<PackageReference Include="AspNetCore.HealthChecks.Uris" Version="8.0.1" />
```

#### Step 4.1.2: Configure Health Checks
**File**: `src/AzureBank.Bff/Program.cs`

```csharp
builder.Services.AddHealthChecks()
    .AddRedis(builder.Configuration["Garnet:ConnectionString"] ?? "localhost:6379",
        name: "garnet", tags: new[] { "ready" })
    .AddUrlGroup(new Uri(builder.Configuration["BackendApi:BaseUrl"] + "/health"),
        name: "backend-api", tags: new[] { "ready" });

// In app configuration:
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false // Just check if app is running
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});
```

### Sub-Phase 4.2: Resilience with Polly

#### Step 4.2.1: Add Polly Package
```xml
<PackageReference Include="Microsoft.Extensions.Http.Polly" Version="8.0.0" />
```

#### Step 4.2.2: Configure Resilient HttpClient
**File**: `src/AzureBank.Bff/Program.cs`

```csharp
builder.Services.AddHttpClient("BackendApi", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["BackendApi:BaseUrl"]!);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
})
.AddTransientHttpErrorPolicy(policy =>
    policy.WaitAndRetryAsync(3, retryAttempt =>
        TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))))
.AddTransientHttpErrorPolicy(policy =>
    policy.CircuitBreakerAsync(5, TimeSpan.FromSeconds(30)));
```

### Sub-Phase 4.3: API Health Check Endpoint

#### Step 4.3.1: Add Health Check to API
**File**: `src/AzureBank.Api/Program.cs`

```csharp
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AzureBankDbContext>("database");

// In app configuration:
app.MapHealthChecks("/health");
```

---

## PHASE 5: Testing & Validation

### Sub-Phase 5.1: Integration Tests

#### Step 5.1.1: Test Scenarios Checklist

| Scenario | Expected | Validates |
|----------|----------|-----------|
| Login via API (direct) | JWT returned | API independence |
| Login via BFF | Cookie returned, no JWT | BFF security |
| API call with expired token | 401 Unauthorized | Token validation |
| BFF call with expiring token | 200 OK (auto-refresh) | Token refresh |
| BFF restart with Garnet | Sessions persist | Distributed store |
| API down, BFF request | 503 with retry | Resilience |
| Multiple BFF instances | Sessions shared | Horizontal scaling |

### Sub-Phase 5.2: Load Testing

```bash
# Use k6 or similar
k6 run --vus 100 --duration 5m load-test.js
```

---

# PART 3: SUMMARY CHECKLIST

## Phase 1: API Enhancements
- [ ] Create IRefreshTokenService interface
- [ ] Implement RefreshTokenService
- [ ] Add POST /api/auth/refresh endpoint
- [ ] Update login to return refresh token
- [ ] Implement logout token revocation
- [ ] Test API still works independently

## Phase 2: BFF Token Refresh
- [ ] Add RefreshToken to UserSession model
- [ ] Create ITokenRefreshService
- [ ] Implement TokenRefreshService
- [ ] Update BearerTokenTransformProvider for proactive refresh
- [ ] Update BFF login to store refresh token
- [ ] Test automatic token refresh

## Phase 3: Distributed Session Store
- [ ] Create docker-compose.yml with Garnet
- [ ] Add StackExchange.Redis package
- [ ] Implement GarnetTokenStore
- [ ] Add configuration for connection string
- [ ] Update Program.cs for conditional registration
- [ ] Test session persistence across restarts

## Phase 4: Production Hardening
- [ ] Add health check packages
- [ ] Configure /health/live and /health/ready
- [ ] Add Polly for retry and circuit breaker
- [ ] Add API health check endpoint
- [ ] Configure structured logging to files

## Phase 5: Validation
- [ ] Run all integration tests
- [ ] Verify API works without BFF
- [ ] Verify BFF provides enhanced security
- [ ] Load test with multiple instances
- [ ] Document deployment procedures

---

# APPENDIX A: File Changes Summary

## New Files to Create
```
src/AzureBank.Api/
├── Services/
│   ├── Interfaces/IRefreshTokenService.cs
│   └── Implementations/RefreshTokenService.cs
├── DTOs/Auth/
│   ├── RefreshTokenRequest.cs
│   └── RefreshTokenResponse.cs

src/AzureBank.Bff/
├── Services/
│   ├── Interfaces/ITokenRefreshService.cs
│   └── Implementations/
│       ├── TokenRefreshService.cs
│       └── GarnetTokenStore.cs

docker/
└── docker-compose.yml
```

## Files to Modify
```
src/AzureBank.Api/
├── Controllers/AuthController.cs        (add refresh endpoint, update logout)
├── DTOs/Auth/LoginResponse.cs           (add RefreshToken fields)
├── Extensions/ServiceCollectionExtensions.cs (register service)
├── Program.cs                           (add health checks)

src/AzureBank.Bff/
├── Models/UserSession.cs                (add RefreshToken fields)
├── Services/
│   ├── Interfaces/ISessionService.cs    (update CreateSession signature)
│   └── Implementations/SessionService.cs (implement refresh storage)
├── Transforms/BearerTokenTransformProvider.cs (add proactive refresh)
├── Controllers/BffAuthController.cs     (store refresh token on login)
├── Program.cs                           (register services, health checks)
├── appsettings.json                     (add Garnet config)
└── AzureBank.Bff.csproj                 (add packages)
```

---

**End of BFF Completion Plan**
