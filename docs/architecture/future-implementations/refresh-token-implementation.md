# Refresh Token Implementation Plan

**Status:** PLANNED (Not Implemented)
**Priority:** High
**Created:** January 2026

---

## Executive Summary

The `RefreshToken` entity and database configuration exist but are **never used**. Currently, users are forced to re-login every 15 minutes because there's no refresh mechanism. This document outlines what exists, what's missing, and the complete implementation plan.

---

## Current State Audit

### What EXISTS (Ready to Use)

| Component | Location | Status |
|-----------|----------|--------|
| `RefreshToken` Entity | `src/AzureBank.Shared/Entities/RefreshToken.cs` | ✅ Complete |
| EF Core Configuration | `src/AzureBank.Infrastructure/Data/Configurations/RefreshTokenConfiguration.cs` | ✅ Complete |
| Database Table | `RefreshTokens` | ✅ Ready (after migration) |
| JWT Options | `JwtOptions.RefreshTokenExpirationDays` | ✅ Configured (7 days) |

### What's MISSING (Needs Implementation)

| Component | Location | Status |
|-----------|----------|--------|
| Refresh Token Generation | `AuthService.cs` | ❌ Not implemented |
| Refresh Token Endpoint | `AuthController.cs` | ❌ Not implemented |
| Token Rotation Logic | `AuthService.cs` | ❌ Not implemented |
| Theft Detection | `AuthService.cs` | ❌ Not implemented |
| Logout Token Revocation | `AuthService.cs` | ❌ Does nothing |
| DTOs with RefreshToken | `LoginResponse.cs`, etc. | ❌ Missing property |

---

## Current Login Flow (BROKEN)

```
┌─────────────────────────────────────────────────────────────────────┐
│                      CURRENT LOGIN FLOW                             │
└─────────────────────────────────────────────────────────────────────┘

    User                        API                           DB
      │                          │                            │
      │─── POST /api/auth/login ─►│                            │
      │    {email, password}     │                            │
      │                          │── Verify password ─────────►│
      │                          │                            │
      │◄─── LoginResponse ───────│                            │
      │  {                       │                            │
      │    token: "eyJ...",      │  ⚠️ ONLY ACCESS TOKEN!     │
      │    expiresAt: "...",     │  No RefreshToken returned  │
      │    user: {...}           │                            │
      │  }                       │                            │
      │                          │                            │
      │       ... 15 minutes pass ...                         │
      │                          │                            │
      │─── GET /api/accounts ───►│                            │
      │    Authorization: Bearer │                            │
      │                          │                            │
      │◄─── 401 Unauthorized ────│  Token expired!            │
      │                          │  No way to refresh!        │
      │                          │  Must re-login!            │
```

**Problem:** User must re-login every 15 minutes. Terrible UX.

---

## Target Login Flow (CORRECT)

```
┌─────────────────────────────────────────────────────────────────────┐
│                    PROPER REFRESH TOKEN FLOW                        │
└─────────────────────────────────────────────────────────────────────┘

    Browser                     API                          DB
      │                          │                            │
      │─── POST /api/auth/login ─►│                            │
      │                          │                            │
      │◄─── LoginResponse ───────│── Store hash ─────────────►│
      │  {                       │   RefreshTokens table      │
      │    accessToken: "eyJ..", │                            │
      │    refreshToken: "abc..",│  ✅ Return PLAIN token     │
      │    expiresAt: ...        │  ✅ Store HASH in DB       │
      │  }                       │                            │
      │                          │                            │
      │  Store:                  │                            │
      │  - accessToken → memory  │  (XSS-safe)                │
      │  - refreshToken → cookie │  (HttpOnly, Secure)        │
      │                          │                            │
      │       ... access token expires ...                    │
      │                          │                            │
      │─── POST /api/auth/refresh─►│                            │
      │    {refreshToken: "abc"}  │                            │
      │                          │── Validate hash ──────────►│
      │                          │   Check: IsActive?         │
      │                          │                            │
      │                          │── ROTATE TOKEN ───────────►│
      │                          │   1. Mark old as replaced  │
      │                          │   2. Create new token      │
      │                          │                            │
      │◄─── New Token Pair ──────│                            │
      │  {                       │                            │
      │    accessToken: "eyJ..", │  ✅ New access token       │
      │    refreshToken: "xyz..",│  ✅ New refresh token      │
      │  }                       │                            │
```

---

## RefreshToken Entity (Already Exists)

Location: `src/AzureBank.Shared/Entities/RefreshToken.cs`

```csharp
public class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    /// <summary>
    /// SHA256 hash of the token - NEVER store plain text!
    /// Plain token is returned to client once, then only hash is stored
    /// </summary>
    public required string TokenHash { get; set; }

    /// <summary>
    /// Banking standard: 1 hour max
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Set when token is revoked (logout, rotation, suspicious activity)
    /// </summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// Token rotation: points to the new token that replaced this one
    /// Used for theft detection - if revoked token is reused, revoke ALL user tokens
    /// </summary>
    public Guid? ReplacedByTokenId { get; set; }

    /// <summary>
    /// Client IP address - validate on refresh for theft detection
    /// </summary>
    public required string IpAddress { get; set; }

    /// <summary>
    /// Browser/client identifier
    /// </summary>
    public required string UserAgent { get; set; }

    // Navigation properties
    public required ApplicationUser User { get; set; }
    public RefreshToken? ReplacedByToken { get; set; }

    // Computed properties (not stored in DB)
    [NotMapped]
    public bool IsExpired => DateTime.UtcNow >= ExpiresAt;

    [NotMapped]
    public bool IsRevoked => RevokedAt.HasValue;

    [NotMapped]
    public bool IsActive => !IsRevoked && !IsExpired;
}
```

### Security Features Built Into Entity

| Feature | Property | Purpose |
|---------|----------|---------|
| Token Hashing | `TokenHash` | If DB compromised, tokens are useless |
| Token Rotation | `ReplacedByTokenId` | Old token points to new token |
| Theft Detection | `ReplacedByTokenId` | If old token reused → revoke ALL |
| IP Tracking | `IpAddress` | Detect different client |
| User Agent Tracking | `UserAgent` | Detect different browser |
| Revocation | `RevokedAt` | For logout, suspicious activity |
| Expiration | `ExpiresAt` | 1 hour max (banking standard) |

---

## Database Configuration (Already Exists)

Location: `src/AzureBank.Infrastructure/Data/Configurations/RefreshTokenConfiguration.cs`

### Key Indexes

```csharp
// Unique token lookup (find by hash)
builder.HasIndex(r => r.TokenHash).IsUnique();

// Expired token cleanup
builder.HasIndex(r => r.ExpiresAt);

// User token lookup (find all user tokens)
builder.HasIndex(r => r.UserId);

// Active token lookup (compound index)
builder.HasIndex(r => new { r.UserId, r.RevokedAt, r.ExpiresAt })
    .HasDatabaseName("IX_RefreshTokens_UserId_Active");
```

### Relationships

```csharp
// User owns tokens (cascade delete on user deletion)
builder.HasOne(r => r.User)
    .WithMany()
    .HasForeignKey(r => r.UserId)
    .OnDelete(DeleteBehavior.Cascade);

// Self-referencing for token rotation chain
builder.HasOne(r => r.ReplacedByToken)
    .WithOne()
    .HasForeignKey<RefreshToken>(r => r.ReplacedByTokenId)
    .OnDelete(DeleteBehavior.Restrict);
```

---

## Implementation Plan

### Phase 1: DTOs

#### 1.1 Create RefreshRequest DTO

Location: `src/AzureBank.Shared/DTOs/Auth/RefreshRequest.cs`

```csharp
namespace AzureBank.Shared.DTOs.Auth;

public class RefreshRequest
{
    /// <summary>
    /// The refresh token received from login or previous refresh
    /// </summary>
    public required string RefreshToken { get; set; }
}
```

#### 1.2 Create TokenPairResponse DTO

Location: `src/AzureBank.Shared/DTOs/Auth/TokenPairResponse.cs`

```csharp
namespace AzureBank.Shared.DTOs.Auth;

public class TokenPairResponse
{
    public required string AccessToken { get; set; }
    public required string RefreshToken { get; set; }
    public required DateTime AccessTokenExpiresAt { get; set; }
    public required DateTime RefreshTokenExpiresAt { get; set; }
}
```

#### 1.3 Update LoginResponse

Location: `src/AzureBank.Shared/DTOs/Auth/LoginResponse.cs`

```csharp
public class LoginResponse
{
    public required string AccessToken { get; set; }      // Renamed from Token
    public required string RefreshToken { get; set; }     // ADD THIS
    public required DateTime ExpiresAt { get; set; }
    public required DateTime RefreshTokenExpiresAt { get; set; }  // ADD THIS
    public required UserLoginInfo User { get; set; }
}
```

---

### Phase 2: IAuthService Interface

Location: `src/AzureBank.Api/Services/Interfaces/IAuthService.cs`

Add these methods:

```csharp
/// <summary>
/// Refreshes access token using a valid refresh token.
/// Implements token rotation - old token is revoked, new one issued.
/// </summary>
/// <param name="refreshToken">The plain refresh token from client</param>
/// <param name="ipAddress">Client IP for security tracking</param>
/// <param name="userAgent">Client User-Agent for security tracking</param>
/// <returns>New token pair (access + refresh)</returns>
/// <exception cref="AuthenticationException">If refresh token is invalid, expired, or revoked</exception>
Task<TokenPairResponse> RefreshTokenAsync(string refreshToken, string ipAddress, string userAgent);

/// <summary>
/// Revokes a specific refresh token (single device logout).
/// </summary>
Task RevokeRefreshTokenAsync(string refreshToken);

/// <summary>
/// Revokes ALL refresh tokens for a user (logout everywhere, password change, suspicious activity).
/// </summary>
Task RevokeAllUserTokensAsync(Guid userId);
```

---

### Phase 3: AuthService Implementation

Location: `src/AzureBank.Api/Services/Implementations/AuthService.cs`

#### 3.1 Add Helper Method: Generate Refresh Token

```csharp
private async Task<(string PlainToken, RefreshToken Entity)> CreateRefreshTokenAsync(
    Guid userId,
    string ipAddress,
    string userAgent)
{
    // Generate cryptographically secure random token
    var plainToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

    // Hash it for storage (never store plain!)
    var tokenHash = ComputeSha256Hash(plainToken);

    var refreshToken = new RefreshToken
    {
        UserId = userId,
        TokenHash = tokenHash,
        ExpiresAt = DateTime.UtcNow.AddDays(_jwtOptions.RefreshTokenExpirationDays),
        CreatedAt = DateTime.UtcNow,
        IpAddress = ipAddress,
        UserAgent = userAgent,
        User = null! // Will be set by EF Core
    };

    _context.Set<RefreshToken>().Add(refreshToken);
    await _context.SaveChangesAsync();

    return (plainToken, refreshToken);
}

private static string ComputeSha256Hash(string input)
{
    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
    return Convert.ToBase64String(bytes);
}
```

#### 3.2 Modify LoginAsync

```csharp
public async Task<LoginResponse> LoginAsync(LoginRequest request, string ipAddress, string userAgent)
{
    var user = await _userManager.FindByEmailAsync(request.Email);

    if (user == null || !await _userManager.CheckPasswordAsync(user, request.Password))
    {
        _logger.LogWarning("Failed login attempt for email {Email}", request.Email);
        throw new AuthenticationException("Invalid email or password.");
    }

    var roles = await _userManager.GetRolesAsync(user);
    var (accessToken, accessExpiresAt) = _jwtService.GenerateToken(user, roles);

    // NEW: Create refresh token
    var (refreshToken, refreshEntity) = await CreateRefreshTokenAsync(user.Id, ipAddress, userAgent);

    _logger.LogInformation("User {UserId} logged in successfully", user.Id);

    return new LoginResponse
    {
        AccessToken = accessToken,
        RefreshToken = refreshToken,  // NEW
        ExpiresAt = accessExpiresAt,
        RefreshTokenExpiresAt = refreshEntity.ExpiresAt,  // NEW
        User = _userMapper.ToLoginInfo(user)
    };
}
```

#### 3.3 Implement RefreshTokenAsync

```csharp
public async Task<TokenPairResponse> RefreshTokenAsync(string refreshToken, string ipAddress, string userAgent)
{
    var tokenHash = ComputeSha256Hash(refreshToken);

    var storedToken = await _context.Set<RefreshToken>()
        .Include(r => r.User)
        .FirstOrDefaultAsync(r => r.TokenHash == tokenHash);

    if (storedToken == null)
    {
        _logger.LogWarning("Refresh token not found");
        throw new AuthenticationException("Invalid refresh token.");
    }

    // Check if token was already used (theft detection!)
    if (storedToken.ReplacedByTokenId.HasValue)
    {
        _logger.LogWarning("Refresh token reuse detected for user {UserId} - revoking all tokens", storedToken.UserId);
        await RevokeAllUserTokensAsync(storedToken.UserId);
        throw new AuthenticationException("Token reuse detected. All sessions revoked.");
    }

    if (!storedToken.IsActive)
    {
        _logger.LogWarning("Inactive refresh token used for user {UserId}", storedToken.UserId);
        throw new AuthenticationException("Refresh token is no longer valid.");
    }

    // Optional: Check IP/UserAgent for suspicious activity
    if (storedToken.IpAddress != ipAddress)
    {
        _logger.LogWarning("Refresh token used from different IP. Original: {Original}, Current: {Current}",
            storedToken.IpAddress, ipAddress);
        // Could throw here for strict security, or just log
    }

    var user = storedToken.User;
    var roles = await _userManager.GetRolesAsync(user);

    // Generate new access token
    var (accessToken, accessExpiresAt) = _jwtService.GenerateToken(user, roles);

    // Rotate refresh token
    var (newRefreshToken, newRefreshEntity) = await CreateRefreshTokenAsync(user.Id, ipAddress, userAgent);

    // Mark old token as replaced (for theft detection)
    storedToken.RevokedAt = DateTime.UtcNow;
    storedToken.ReplacedByTokenId = newRefreshEntity.Id;
    await _context.SaveChangesAsync();

    _logger.LogInformation("Token refreshed for user {UserId}", user.Id);

    return new TokenPairResponse
    {
        AccessToken = accessToken,
        RefreshToken = newRefreshToken,
        AccessTokenExpiresAt = accessExpiresAt,
        RefreshTokenExpiresAt = newRefreshEntity.ExpiresAt
    };
}
```

#### 3.4 Implement RevokeRefreshTokenAsync

```csharp
public async Task RevokeRefreshTokenAsync(string refreshToken)
{
    var tokenHash = ComputeSha256Hash(refreshToken);

    var storedToken = await _context.Set<RefreshToken>()
        .FirstOrDefaultAsync(r => r.TokenHash == tokenHash);

    if (storedToken != null && storedToken.IsActive)
    {
        storedToken.RevokedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        _logger.LogInformation("Refresh token revoked for user {UserId}", storedToken.UserId);
    }
}
```

#### 3.5 Implement RevokeAllUserTokensAsync

```csharp
public async Task RevokeAllUserTokensAsync(Guid userId)
{
    var activeTokens = await _context.Set<RefreshToken>()
        .Where(r => r.UserId == userId && r.RevokedAt == null)
        .ToListAsync();

    foreach (var token in activeTokens)
    {
        token.RevokedAt = DateTime.UtcNow;
    }

    await _context.SaveChangesAsync();
    _logger.LogInformation("All refresh tokens revoked for user {UserId}. Count: {Count}", userId, activeTokens.Count);
}
```

#### 3.6 Fix LogoutAsync

```csharp
public async Task LogoutAsync(Guid userId, string? refreshToken = null)
{
    if (!string.IsNullOrEmpty(refreshToken))
    {
        // Revoke specific token (single device logout)
        await RevokeRefreshTokenAsync(refreshToken);
    }
    else
    {
        // Revoke all tokens (logout everywhere)
        await RevokeAllUserTokensAsync(userId);
    }

    _logger.LogInformation("User {UserId} logged out", userId);
}
```

---

### Phase 4: AuthController Endpoint

Location: `src/AzureBank.Api/Controllers/AuthController.cs`

#### 4.1 Add Refresh Endpoint

```csharp
/// <summary>
/// Refresh access token using a valid refresh token.
/// Implements token rotation for security.
/// </summary>
[EndpointSummary("Refresh token")]
[HttpPost("refresh")]
[AllowAnonymous]  // Uses refresh token, not access token
[ProducesResponseType(typeof(ApiResponse<TokenPairResponse>), StatusCodes.Status200OK)]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
public async Task<ActionResult<ApiResponse<TokenPairResponse>>> Refresh([FromBody] RefreshRequest request)
{
    var ipAddress = GetClientIpAddress();
    var userAgent = GetClientUserAgent();

    var result = await _authService.RefreshTokenAsync(request.RefreshToken, ipAddress, userAgent);
    return Ok(ApiResponse<TokenPairResponse>.Success(result, "Token refreshed"));
}

private string GetClientIpAddress()
{
    return HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

private string GetClientUserAgent()
{
    return Request.Headers.UserAgent.ToString();
}
```

#### 4.2 Update Login Endpoint

```csharp
[HttpPost("login")]
[AllowAnonymous]
public async Task<ActionResult<ApiResponse<LoginResponse>>> Login([FromBody] LoginRequest request)
{
    await _loginValidator.ValidateAndThrowAsync(request);

    var ipAddress = GetClientIpAddress();
    var userAgent = GetClientUserAgent();

    var result = await _authService.LoginAsync(request, ipAddress, userAgent);  // Pass IP/UserAgent
    return Ok(ApiResponse<LoginResponse>.Success(result, "Login successful"));
}
```

#### 4.3 Update Logout Endpoint

```csharp
[HttpPost("logout")]
[Authorize]
public async Task<ActionResult<ApiResponse>> Logout([FromBody] LogoutRequest? request = null)
{
    var userId = GetCurrentUserId();
    await _authService.LogoutAsync(userId, request?.RefreshToken);
    return Ok(ApiResponse.Success("Logged out successfully"));
}
```

---

### Phase 5: Validator

Location: `src/AzureBank.Api/Validators/Auth/RefreshRequestValidator.cs`

```csharp
using AzureBank.Shared.DTOs.Auth;
using FluentValidation;

namespace AzureBank.Api.Validators.Auth;

public class RefreshRequestValidator : AbstractValidator<RefreshRequest>
{
    public RefreshRequestValidator()
    {
        RuleFor(x => x.RefreshToken)
            .NotEmpty().WithMessage("Refresh token is required.")
            .MinimumLength(20).WithMessage("Invalid refresh token format.");
    }
}
```

---

### Phase 6: Background Cleanup Job (Optional)

Location: `src/AzureBank.Api/Services/Background/RefreshTokenCleanupService.cs`

```csharp
public class RefreshTokenCleanupService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<RefreshTokenCleanupService> _logger;
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(1);

    public RefreshTokenCleanupService(IServiceProvider serviceProvider, ILogger<RefreshTokenCleanupService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

                var cutoff = DateTime.UtcNow.AddDays(-7); // Keep 7 days of history

                var deletedCount = await context.Set<RefreshToken>()
                    .Where(r => r.ExpiresAt < cutoff)
                    .ExecuteDeleteAsync(stoppingToken);

                if (deletedCount > 0)
                {
                    _logger.LogInformation("Cleaned up {Count} expired refresh tokens", deletedCount);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during refresh token cleanup");
            }

            await Task.Delay(_cleanupInterval, stoppingToken);
        }
    }
}
```

Register in `Program.cs`:
```csharp
builder.Services.AddHostedService<RefreshTokenCleanupService>();
```

---

## Token Rotation & Theft Detection

### How It Works

The `ReplacedByTokenId` field enables automatic theft detection:

```
Normal Flow:
┌─────────────────────────────────────────────────────────────────┐
│ 1. User has Token A                                             │
│ 2. User refreshes → A marked as replaced by B                   │
│ 3. User now uses Token B                                        │
│ 4. User refreshes → B marked as replaced by C                   │
│ 5. User now uses Token C                                        │
└─────────────────────────────────────────────────────────────────┘

Theft Scenario:
┌─────────────────────────────────────────────────────────────────┐
│ 1. User has Token A                                             │
│ 2. Attacker steals Token A                                      │
│ 3. User refreshes → A replaced by B (A.ReplacedByTokenId = B)   │
│ 4. Attacker tries to use A                                      │
│ 5. Server sees: A was already replaced! THEFT DETECTED!         │
│ 6. Server revokes ALL user tokens (A, B, everything)            │
│ 7. Both user and attacker logged out                            │
│ 8. User must re-authenticate (attacker cannot)                  │
└─────────────────────────────────────────────────────────────────┘
```

### Why This Is Secure

1. **Token is hashed** - If DB is compromised, attacker can't use tokens
2. **Token rotation** - Each refresh invalidates old token
3. **Reuse detection** - Using old token triggers security response
4. **IP tracking** - Can detect token used from different location
5. **User Agent tracking** - Can detect token used from different browser

---

## Client-Side Implementation (Without BFF)

### Token Storage Strategy

| Token | Storage | Why |
|-------|---------|-----|
| Access Token | Memory (JS variable) | Short-lived, needs frequent access, XSS-safe |
| Refresh Token | HttpOnly Cookie | Longer-lived, protected from XSS, auto-sent |

### React Example

```typescript
// auth.ts
let accessToken: string | null = null;
let accessTokenExpiresAt: Date | null = null;

export async function login(email: string, password: string) {
  const response = await fetch('/api/auth/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: 'include',  // Include cookies
    body: JSON.stringify({ email, password })
  });

  const data = await response.json();

  // Store access token in memory (XSS-safe)
  accessToken = data.data.accessToken;
  accessTokenExpiresAt = new Date(data.data.expiresAt);

  // Refresh token is set as HttpOnly cookie by server
  // (can't access it from JS - that's the point!)

  return data.data.user;
}

export async function getAccessToken(): Promise<string | null> {
  // Check if token is expired or about to expire (5 min buffer)
  if (!accessToken || !accessTokenExpiresAt ||
      accessTokenExpiresAt.getTime() - Date.now() < 5 * 60 * 1000) {
    await refreshToken();
  }
  return accessToken;
}

async function refreshToken() {
  const response = await fetch('/api/auth/refresh', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: 'include',  // Send refresh token cookie
    body: JSON.stringify({})  // Refresh token comes from cookie
  });

  if (!response.ok) {
    // Refresh failed - user must re-login
    accessToken = null;
    accessTokenExpiresAt = null;
    window.location.href = '/login';
    return;
  }

  const data = await response.json();
  accessToken = data.data.accessToken;
  accessTokenExpiresAt = new Date(data.data.accessTokenExpiresAt);
}

// Axios interceptor for automatic token refresh
axios.interceptors.request.use(async (config) => {
  const token = await getAccessToken();
  if (token) {
    config.headers.Authorization = `Bearer ${token}`;
  }
  return config;
});
```

---

## Testing Checklist

### Manual Testing

| Test Case | Expected Result |
|-----------|-----------------|
| Login | Returns accessToken + refreshToken |
| Access protected endpoint | 200 with valid access token |
| Wait 15+ min, access endpoint | 401 (access token expired) |
| Call /refresh with valid refresh token | New token pair returned |
| Access endpoint with new token | 200 OK |
| Use old refresh token again | 401 + ALL tokens revoked (theft detection) |
| Logout | Refresh token revoked |
| Try refresh after logout | 401 |

### Automated Tests

```csharp
[Fact]
public async Task Login_ReturnsRefreshToken()
{
    var response = await _client.PostAsync("/api/auth/login",
        JsonContent.Create(new { email = "test@test.com", password = "Test123!" }));

    var result = await response.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>();

    Assert.NotNull(result.Data.RefreshToken);
    Assert.True(result.Data.RefreshTokenExpiresAt > DateTime.UtcNow);
}

[Fact]
public async Task Refresh_WithValidToken_ReturnsNewTokenPair()
{
    // Login first
    var loginResponse = await Login();

    // Refresh
    var refreshResponse = await _client.PostAsync("/api/auth/refresh",
        JsonContent.Create(new { refreshToken = loginResponse.RefreshToken }));

    Assert.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);

    var result = await refreshResponse.Content.ReadFromJsonAsync<ApiResponse<TokenPairResponse>>();
    Assert.NotEqual(loginResponse.RefreshToken, result.Data.RefreshToken); // Token rotated
}

[Fact]
public async Task Refresh_WithUsedToken_RevokesAllTokens()
{
    var loginResponse = await Login();
    var originalRefreshToken = loginResponse.RefreshToken;

    // First refresh - should work
    var refresh1 = await Refresh(originalRefreshToken);
    Assert.Equal(HttpStatusCode.OK, refresh1.StatusCode);

    // Try to use original token again - THEFT DETECTED
    var refresh2 = await Refresh(originalRefreshToken);
    Assert.Equal(HttpStatusCode.Unauthorized, refresh2.StatusCode);

    // New token should also be revoked
    var newToken = (await refresh1.Content.ReadFromJsonAsync<ApiResponse<TokenPairResponse>>()).Data.RefreshToken;
    var refresh3 = await Refresh(newToken);
    Assert.Equal(HttpStatusCode.Unauthorized, refresh3.StatusCode);
}
```

---

## Dependencies

### NuGet Packages (Already Installed)
- `Microsoft.AspNetCore.Identity.EntityFrameworkCore` - User management
- `System.IdentityModel.Tokens.Jwt` - JWT generation
- `Microsoft.IdentityModel.Tokens` - Token validation

### No Additional Packages Required
- `System.Security.Cryptography` - Built into .NET (for SHA256, RandomNumberGenerator)

---

## Migration Required

After implementing, create migration:

```bash
dotnet ef migrations add AddRefreshTokens -p src/AzureBank.Infrastructure -s src/AzureBank.Api
dotnet ef database update -p src/AzureBank.Infrastructure -s src/AzureBank.Api
```

Note: The `RefreshTokenConfiguration` already exists, so this migration will create the `RefreshTokens` table.

---

## Summary

| Component | Exists | Needs Implementation |
|-----------|--------|---------------------|
| Entity | ✅ | - |
| DB Config | ✅ | - |
| DTOs | ⚠️ Partial | Add RefreshToken to responses |
| IAuthService | ⚠️ Partial | Add RefreshTokenAsync, RevokeAllUserTokensAsync |
| AuthService | ⚠️ Partial | Implement all refresh logic |
| AuthController | ⚠️ Partial | Add /refresh endpoint |
| Validator | ❌ | Create RefreshRequestValidator |
| Cleanup Job | ❌ | Optional - clean expired tokens |

**Estimated effort:** 4-6 hours for complete implementation with tests.

---

*Created: January 2026*
*Status: Planned for future implementation*
