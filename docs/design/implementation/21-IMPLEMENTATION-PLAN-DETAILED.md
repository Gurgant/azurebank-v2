# AzureBank Implementation Plan - Detailed Roadmap

**Document Version**: 1.0
**Created**: 2026-01-12
**Status**: ACTIVE IMPLEMENTATION

---

## Executive Summary

This document provides a complete, step-by-step implementation roadmap for completing the AzureBank backend. The plan follows a dependency-ordered approach where foundational components are built first.

### Current State

| Layer | Status | Completion |
|-------|--------|------------|
| **AzureBank.Shared** | COMPLETE | 100% |
| **AzureBank.Infrastructure** | COMPLETE | 100% |
| **AzureBank.Api** | PARTIAL | ~25% |
| **AzureBank.Bff** | NOT STARTED | 0% |

### Implementation Order (Critical Path)

```
1. Exception Handlers ─→ 2. Mappers ─→ 3. Validators ─→ 4. Services ─→ 5. Controllers ─→ 6. Program.cs ─→ 7. BFF
```

**Why this order?**
- Exception Handlers: All code throws exceptions; handlers convert them to `ProblemDetails`
- Mappers: Services need to convert Entity ↔ DTO
- Validators: Controllers need to validate input before calling services
- Services: Business logic layer (depends on mappers)
- Controllers: API surface (depends on services, validators)
- Program.cs: Wire everything together
- BFF: After API is complete

---

## Phase 5.1: Exception Handlers (FOUNDATION)

### Purpose
Convert domain exceptions to RFC 7807 `ProblemDetails` responses. This is the foundation - all other code depends on proper error handling.

### Files to Create

```
AzureBank.Api/
└── Handlers/
    ├── AppExceptionHandler.cs         # Step 1
    ├── ValidationExceptionHandler.cs  # Step 2
    └── GlobalExceptionHandler.cs      # Step 3
```

### 5.1.1 AppExceptionHandler.cs

Handles all custom `AppException` types (NotFoundException, BusinessRuleException, etc.)

```csharp
using System.Diagnostics;
using AzureBank.Shared.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace AzureBank.Api.Handlers;

/// <summary>
/// Handles all AppException-derived exceptions and converts them to ProblemDetails.
/// Registered first in the exception handler chain (highest priority for domain exceptions).
/// </summary>
public class AppExceptionHandler : IExceptionHandler
{
    private readonly ILogger<AppExceptionHandler> _logger;

    public AppExceptionHandler(ILogger<AppExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not AppException appException)
            return false; // Let next handler deal with it

        _logger.LogWarning(
            exception,
            "Domain exception: {ErrorCode} - {Message}",
            appException.ErrorCode,
            appException.Message);

        var problemDetails = new ProblemDetails
        {
            Status = appException.StatusCode,
            Title = GetTitleForStatusCode(appException.StatusCode),
            Detail = appException.Message,
            Type = $"https://httpstatuses.com/{appException.StatusCode}",
            Instance = httpContext.Request.Path
        };

        // Add error code extension
        problemDetails.Extensions["errorCode"] = appException.ErrorCode;

        // Add correlation ID
        problemDetails.Extensions["traceId"] = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        // Add details if present (e.g., InsufficientFundsException details)
        if (appException.Details is { Count: > 0 })
        {
            foreach (var detail in appException.Details)
            {
                problemDetails.Extensions[detail.Key] = detail.Value;
            }
        }

        httpContext.Response.StatusCode = appException.StatusCode;
        await httpContext.Response.WriteAsJsonAsync(problemDetails, cancellationToken);

        return true;
    }

    private static string GetTitleForStatusCode(int statusCode) => statusCode switch
    {
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        409 => "Conflict",
        422 => "Unprocessable Entity",
        _ => "Error"
    };
}
```

### 5.1.2 ValidationExceptionHandler.cs

Handles FluentValidation exceptions.

```csharp
using System.Diagnostics;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace AzureBank.Api.Handlers;

/// <summary>
/// Handles FluentValidation.ValidationException and converts to ProblemDetails.
/// Returns 400 Bad Request with field-level validation errors.
/// </summary>
public class ValidationExceptionHandler : IExceptionHandler
{
    private readonly ILogger<ValidationExceptionHandler> _logger;

    public ValidationExceptionHandler(ILogger<ValidationExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not ValidationException validationException)
            return false;

        _logger.LogInformation(
            "Validation failed: {ErrorCount} errors",
            validationException.Errors.Count());

        var problemDetails = new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Validation Failed",
            Detail = "One or more validation errors occurred.",
            Type = "https://httpstatuses.com/400",
            Instance = httpContext.Request.Path
        };

        // Add trace ID
        problemDetails.Extensions["traceId"] = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        // Group errors by property name
        var errors = validationException.Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(
                g => ToCamelCase(g.Key),
                g => g.Select(e => e.ErrorMessage).ToArray());

        problemDetails.Extensions["errors"] = errors;

        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        await httpContext.Response.WriteAsJsonAsync(problemDetails, cancellationToken);

        return true;
    }

    private static string ToCamelCase(string str)
    {
        if (string.IsNullOrEmpty(str) || char.IsLower(str[0]))
            return str;

        return char.ToLowerInvariant(str[0]) + str[1..];
    }
}
```

### 5.1.3 GlobalExceptionHandler.cs

Catch-all for unexpected exceptions.

```csharp
using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace AzureBank.Api.Handlers;

/// <summary>
/// Global fallback exception handler for unexpected errors.
/// Logs full details internally but returns sanitized response externally.
/// </summary>
public class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;
    private readonly IHostEnvironment _environment;

    public GlobalExceptionHandler(
        ILogger<GlobalExceptionHandler> logger,
        IHostEnvironment environment)
    {
        _logger = logger;
        _environment = environment;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        // Log full exception details
        _logger.LogError(
            exception,
            "Unhandled exception: {ExceptionType} - {Message}",
            exception.GetType().Name,
            exception.Message);

        var problemDetails = new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "Internal Server Error",
            Detail = _environment.IsDevelopment()
                ? exception.Message
                : "An unexpected error occurred. Please try again later.",
            Type = "https://httpstatuses.com/500",
            Instance = httpContext.Request.Path
        };

        problemDetails.Extensions["traceId"] = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        // Include stack trace in development only
        if (_environment.IsDevelopment())
        {
            problemDetails.Extensions["exception"] = exception.ToString();
        }

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await httpContext.Response.WriteAsJsonAsync(problemDetails, cancellationToken);

        return true;
    }
}
```

### Registration in Program.cs

```csharp
// Add exception handlers (order matters - first registered = first tried)
builder.Services.AddExceptionHandler<ValidationExceptionHandler>();
builder.Services.AddExceptionHandler<AppExceptionHandler>();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// In middleware pipeline (before controllers)
app.UseExceptionHandler();
```

---

## Phase 5.2: Mappers (Mapperly)

### Purpose
Convert between Entity and DTO types. Mapperly is a source generator (no runtime reflection).

### Files to Create

```
AzureBank.Api/
└── Mappers/
    ├── AccountMapper.cs
    ├── TransactionMapper.cs
    └── UserMapper.cs
```

### 5.2.1 AccountMapper.cs

```csharp
using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.Entities;
using Riok.Mapperly.Abstractions;

namespace AzureBank.Api.Mappers;

[Mapper]
public partial class AccountMapper
{
    public partial AccountResponse ToResponse(Account entity);
    public partial List<AccountResponse> ToResponseList(List<Account> entities);

    public partial AccountSummaryResponse ToSummary(Account entity);
    public partial List<AccountSummaryResponse> ToSummaryList(List<Account> entities);

    // Custom mapping for masked account number
    public AccountSummaryResponse ToSummaryWithMask(Account entity)
    {
        var summary = ToSummary(entity);
        return summary with
        {
            MaskedAccountNumber = MaskAccountNumber(entity.AccountNumber)
        };
    }

    private static string MaskAccountNumber(string accountNumber)
    {
        // AB-1234-5678-90 -> AB-****-****-90
        if (string.IsNullOrEmpty(accountNumber) || accountNumber.Length < 14)
            return accountNumber;

        return $"{accountNumber[..3]}****-****-{accountNumber[^2..]}";
    }
}
```

### 5.2.2 TransactionMapper.cs

```csharp
using AzureBank.Shared.DTOs.Transaction;
using AzureBank.Shared.Entities;
using Riok.Mapperly.Abstractions;

namespace AzureBank.Api.Mappers;

[Mapper]
public partial class TransactionMapper
{
    public partial TransactionResponse ToResponse(Transaction entity);
    public partial List<TransactionResponse> ToResponseList(List<Transaction> entities);
}
```

### 5.2.3 UserMapper.cs

```csharp
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.User;
using AzureBank.Shared.Entities;
using Riok.Mapperly.Abstractions;

namespace AzureBank.Api.Mappers;

[Mapper]
public partial class UserMapper
{
    public partial UserResponse ToResponse(ApplicationUser entity);

    [MapProperty(nameof(ApplicationUser.Id), nameof(UserLoginInfo.Id))]
    public partial UserLoginInfo ToLoginInfo(ApplicationUser entity);

    // Custom mapping with HasPin calculation
    public UserLoginInfo ToLoginInfoWithPin(ApplicationUser entity)
    {
        return new UserLoginInfo
        {
            Id = entity.Id,
            AzureTag = entity.AzureTag,
            Email = entity.Email ?? string.Empty,
            FirstName = entity.FirstName,
            LastName = entity.LastName,
            HasPin = !string.IsNullOrEmpty(entity.PinHash)
        };
    }

    public RecipientSearchResult ToSearchResult(ApplicationUser entity)
    {
        return new RecipientSearchResult
        {
            AzureTag = entity.AzureTag,
            DisplayName = $"{entity.FirstName} {entity.LastName[0]}."
        };
    }

    public RecipientLookupResponse ToLookupResponse(ApplicationUser? entity, string azureTag)
    {
        return new RecipientLookupResponse
        {
            AzureTag = azureTag,
            DisplayName = entity != null ? $"{entity.FirstName} {entity.LastName[0]}." : string.Empty,
            Exists = entity != null
        };
    }
}
```

---

## Phase 5.3: Validators (FluentValidation)

### Purpose
Validate request DTOs before they reach the service layer.

### Files to Create

```
AzureBank.Api/
└── Validators/
    ├── Auth/
    │   ├── LoginRequestValidator.cs
    │   └── RegisterRequestValidator.cs
    ├── Account/
    │   ├── CreateAccountRequestValidator.cs
    │   └── UpdateAccountRequestValidator.cs
    ├── Transaction/
    │   ├── DepositRequestValidator.cs
    │   └── WithdrawRequestValidator.cs
    └── Transfer/
        ├── TransferRequestValidator.cs
        └── InternalTransferRequestValidator.cs
```

### 5.3.1 LoginRequestValidator.cs

```csharp
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using FluentValidation;

namespace AzureBank.Api.Validators.Auth;

public class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required.")
            .EmailAddress().WithMessage("Invalid email format.")
            .MaximumLength(ValidationRules.EmailMaxLength);

        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("Password is required.");
    }
}
```

### 5.3.2 RegisterRequestValidator.cs

```csharp
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using FluentValidation;

namespace AzureBank.Api.Validators.Auth;

public class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.AzureTag)
            .NotEmpty().WithMessage("AzureTag is required.")
            .Length(ValidationRules.AzureTagMinLength, ValidationRules.AzureTagMaxLength)
            .Matches(ValidationRules.AzureTagPattern)
            .WithMessage(ValidationRules.AzureTagPatternMessage);

        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required.")
            .EmailAddress().WithMessage("Invalid email format.")
            .MaximumLength(ValidationRules.EmailMaxLength);

        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("Password is required.")
            .Length(ValidationRules.PasswordMinLength, ValidationRules.PasswordMaxLength)
            .Matches(ValidationRules.PasswordPattern)
            .WithMessage(ValidationRules.PasswordPatternMessage);

        RuleFor(x => x.FirstName)
            .NotEmpty().WithMessage("First name is required.")
            .Length(ValidationRules.FirstNameMinLength, ValidationRules.FirstNameMaxLength)
            .Matches(ValidationRules.NamePattern)
            .WithMessage(ValidationRules.NamePatternMessage);

        RuleFor(x => x.LastName)
            .NotEmpty().WithMessage("Last name is required.")
            .Length(ValidationRules.LastNameMinLength, ValidationRules.LastNameMaxLength)
            .Matches(ValidationRules.NamePattern)
            .WithMessage(ValidationRules.NamePatternMessage);
    }
}
```

### 5.3.3 DepositRequestValidator.cs

```csharp
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Transaction;
using FluentValidation;

namespace AzureBank.Api.Validators.Transaction;

public class DepositRequestValidator : AbstractValidator<DepositRequest>
{
    public DepositRequestValidator()
    {
        RuleFor(x => x.AccountId)
            .NotEmpty().WithMessage(ValidationRules.AccountNotEmptyGuid);

        RuleFor(x => x.Amount)
            .GreaterThanOrEqualTo(ValidationRules.TransactionMinAmount)
            .WithMessage($"Amount must be at least {ValidationRules.TransactionMinAmount:C}.")
            .LessThanOrEqualTo(ValidationRules.TransactionMaxAmount)
            .WithMessage($"Amount cannot exceed {ValidationRules.TransactionMaxAmount:C}.");

        RuleFor(x => x.Description)
            .MaximumLength(ValidationRules.TransactionDescriptionMaxLength)
            .WithMessage(ValidationRules.DescriptionMaxLengthMessage)
            .When(x => !string.IsNullOrEmpty(x.Description));
    }
}
```

### 5.3.4 TransferRequestValidator.cs

```csharp
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Transfer;
using FluentValidation;

namespace AzureBank.Api.Validators.Transfer;

public class TransferRequestValidator : AbstractValidator<TransferRequest>
{
    public TransferRequestValidator()
    {
        RuleFor(x => x.FromAccountId)
            .NotEmpty().WithMessage(ValidationRules.AccountNotEmptyGuid);

        RuleFor(x => x.RecipientAzureTag)
            .NotEmpty().WithMessage("Recipient AzureTag is required.")
            .Matches(ValidationRules.AzureTagPattern)
            .WithMessage(ValidationRules.AzureTagPatternMessage);

        RuleFor(x => x.Amount)
            .GreaterThanOrEqualTo(ValidationRules.TransactionMinAmount)
            .WithMessage($"Amount must be at least {ValidationRules.TransactionMinAmount:C}.")
            .LessThanOrEqualTo(ValidationRules.TransactionMaxAmount)
            .WithMessage($"Amount cannot exceed {ValidationRules.TransactionMaxAmount:C}.");

        RuleFor(x => x.Description)
            .MaximumLength(ValidationRules.TransactionDescriptionMaxLength)
            .When(x => !string.IsNullOrEmpty(x.Description));
    }
}
```

### Registration in Program.cs

```csharp
// Add FluentValidation validators from assembly
builder.Services.AddValidatorsFromAssemblyContaining<Program>();
```

---

## Phase 5.4: Service Implementations

### Files to Create

```
AzureBank.Api/
└── Services/
    └── Implementations/
        ├── JwtService.cs
        ├── AuthService.cs
        ├── AccountService.cs
        ├── TransactionService.cs
        ├── TransferService.cs
        └── UserService.cs
```

### Key Implementation Patterns

1. **Constructor Injection** - All dependencies via DI
2. **Throw domain exceptions** - Let IExceptionHandler convert to ProblemDetails
3. **Resource authorization** - Check userId matches resource owner
4. **Optimistic concurrency** - Use RowVersion for Account updates
5. **Transaction scope** - Use EF transactions for multi-entity updates

### 5.4.1 JwtService.cs

```csharp
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Options;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AzureBank.Api.Services.Implementations;

public class JwtService : IJwtService
{
    private readonly JwtOptions _options;
    private readonly ILogger<JwtService> _logger;

    public JwtService(IOptions<JwtOptions> options, ILogger<JwtService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public TokenResponse GenerateToken(ApplicationUser user)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expiresAt = DateTime.UtcNow.AddMinutes(_options.ExpirationMinutes);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            new Claim("azure_tag", user.AzureTag),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(JwtRegisteredClaimNames.Iat, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString())
        };

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            expires: expiresAt,
            signingCredentials: credentials);

        var accessToken = new JwtSecurityTokenHandler().WriteToken(token);

        _logger.LogInformation("Generated JWT for user {UserId}", user.Id);

        return new TokenResponse
        {
            AccessToken = accessToken,
            ExpiresIn = _options.ExpirationMinutes * 60,
            TokenType = "Bearer",
            ExpiresAt = expiresAt
        };
    }

    public ClaimsPrincipal? ValidateToken(string token)
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        var key = Encoding.UTF8.GetBytes(_options.Secret);

        try
        {
            var principal = tokenHandler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = _options.Issuer,
                ValidAudience = _options.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(key),
                ClockSkew = TimeSpan.Zero
            }, out _);

            return principal;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Token validation failed");
            return null;
        }
    }
}
```

### 5.4.2 AuthService.cs (Skeleton)

```csharp
using AzureBank.Api.Mappers;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Exceptions;
using AzureBank.Shared.Services.Interfaces;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AzureBank.Api.Services.Implementations;

public class AuthService : IAuthService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly AzureBankDbContext _context;
    private readonly IJwtService _jwtService;
    private readonly IPasswordHasher _passwordHasher;
    private readonly UserMapper _userMapper;
    private readonly AccountMapper _accountMapper;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        UserManager<ApplicationUser> userManager,
        AzureBankDbContext context,
        IJwtService jwtService,
        IPasswordHasher passwordHasher,
        ILogger<AuthService> logger)
    {
        _userManager = userManager;
        _context = context;
        _jwtService = jwtService;
        _passwordHasher = passwordHasher;
        _userMapper = new UserMapper();
        _accountMapper = new AccountMapper();
        _logger = logger;
    }

    public async Task<LoginResponse> LoginAsync(LoginRequest request)
    {
        var user = await _userManager.FindByEmailAsync(request.Email);

        if (user == null || !await _userManager.CheckPasswordAsync(user, request.Password))
        {
            // Prevent user enumeration - same message for both cases
            throw new AuthenticationException("Invalid email or password.");
        }

        var token = _jwtService.GenerateToken(user);

        _logger.LogInformation("User {UserId} logged in successfully", user.Id);

        return new LoginResponse
        {
            Token = token.AccessToken,
            ExpiresAt = token.ExpiresAt,
            User = _userMapper.ToLoginInfoWithPin(user)
        };
    }

    public async Task<RegisterResponse> RegisterAsync(RegisterRequest request)
    {
        // Check for existing email
        if (await _userManager.FindByEmailAsync(request.Email) != null)
        {
            throw new ConflictException("Email is already registered.", "DUPLICATE_EMAIL");
        }

        // Check for existing AzureTag
        if (await _context.Users.AnyAsync(u => u.AzureTag == request.AzureTag.ToLower()))
        {
            throw new ConflictException("AzureTag is already taken.", "DUPLICATE_AZURE_TAG");
        }

        var user = new ApplicationUser
        {
            UserName = request.AzureTag.ToLower(),
            Email = request.Email,
            AzureTag = request.AzureTag.ToLower(),
            FirstName = request.FirstName,
            LastName = request.LastName,
            EmailConfirmed = true // Skip email verification for MVP
        };

        var result = await _userManager.CreateAsync(user, request.Password);

        if (!result.Succeeded)
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            _logger.LogWarning("User registration failed: {Errors}", errors);
            throw new BusinessRuleException($"Registration failed: {errors}");
        }

        // Create default primary account
        var account = new Account
        {
            UserId = user.Id,
            AccountNumber = GenerateAccountNumber(),
            Name = "Primary Account",
            Type = Shared.Enums.AccountType.Checking,
            Balance = 0,
            IsPrimary = true,
            User = user
        };

        _context.Accounts.Add(account);
        await _context.SaveChangesAsync();

        var token = _jwtService.GenerateToken(user);

        _logger.LogInformation("User {UserId} registered successfully with account {AccountId}", user.Id, account.Id);

        return new RegisterResponse
        {
            User = _userMapper.ToLoginInfoWithPin(user),
            Account = _accountMapper.ToResponse(account),
            Token = token
        };
    }

    public async Task<UserLoginInfo> GetCurrentUserAsync(Guid userId)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());

        if (user == null)
        {
            throw new NotFoundException("User", userId);
        }

        return _userMapper.ToLoginInfoWithPin(user);
    }

    public async Task LogoutAsync(Guid userId)
    {
        // For MVP: Just log the logout
        // Future: Invalidate refresh tokens
        _logger.LogInformation("User {UserId} logged out", userId);
        await Task.CompletedTask;
    }

    public async Task<bool> VerifyPinAsync(Guid userId, string pin)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());

        if (user == null || string.IsNullOrEmpty(user.PinHash))
        {
            return false;
        }

        return _passwordHasher.VerifyPin(user.PinHash, pin);
    }

    public async Task SetPinAsync(Guid userId, SetPinRequest request)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());

        if (user == null)
        {
            throw new NotFoundException("User", userId);
        }

        user.PinHash = _passwordHasher.HashPin(request.Pin);
        await _userManager.UpdateAsync(user);

        _logger.LogInformation("User {UserId} set their PIN", userId);
    }

    private static string GenerateAccountNumber()
    {
        var random = new Random();
        return $"AB-{random.Next(1000, 9999)}-{random.Next(1000, 9999)}-{random.Next(10, 99)}";
    }
}
```

---

## Phase 5.5: Controllers

### Controller Pattern

```csharp
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class ExampleController : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<T>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<T>>> Method([FromBody] Request request)
    {
        // 1. Validate manually (FluentValidation)
        // 2. Call service
        // 3. Return ApiResponse wrapper
        // NO try-catch - IExceptionHandler handles all exceptions
    }
}
```

### 5.5.1 AuthController.cs

```csharp
using AzureBank.Api.Services.Interfaces;
using AzureBank.Api.Validators.Auth;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace AzureBank.Api.Controllers;

[ApiController]
[Route("api/auth")]
[Produces("application/json")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly IValidator<LoginRequest> _loginValidator;
    private readonly IValidator<RegisterRequest> _registerValidator;

    public AuthController(
        IAuthService authService,
        IValidator<LoginRequest> loginValidator,
        IValidator<RegisterRequest> registerValidator)
    {
        _authService = authService;
        _loginValidator = loginValidator;
        _registerValidator = registerValidator;
    }

    /// <summary>
    /// Authenticate user and receive JWT token.
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<LoginResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ApiResponse<LoginResponse>>> Login([FromBody] LoginRequest request)
    {
        await _loginValidator.ValidateAndThrowAsync(request);

        var result = await _authService.LoginAsync(request);
        return Ok(ApiResponse<LoginResponse>.Success(result, "Login successful"));
    }

    /// <summary>
    /// Register a new user account.
    /// </summary>
    [HttpPost("register")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<RegisterResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<RegisterResponse>>> Register([FromBody] RegisterRequest request)
    {
        await _registerValidator.ValidateAndThrowAsync(request);

        var result = await _authService.RegisterAsync(request);
        return StatusCode(StatusCodes.Status201Created,
            ApiResponse<RegisterResponse>.Success(result, "Registration successful"));
    }

    /// <summary>
    /// Get current authenticated user information.
    /// </summary>
    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType(typeof(ApiResponse<UserLoginInfo>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ApiResponse<UserLoginInfo>>> GetCurrentUser()
    {
        var userId = GetCurrentUserId();
        var result = await _authService.GetCurrentUserAsync(userId);
        return Ok(ApiResponse<UserLoginInfo>.Success(result));
    }

    /// <summary>
    /// Logout and invalidate session.
    /// </summary>
    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse>> Logout()
    {
        var userId = GetCurrentUserId();
        await _authService.LogoutAsync(userId);
        return Ok(ApiResponse.Success("Logged out successfully"));
    }

    private Guid GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value;
        return Guid.Parse(userIdClaim!);
    }
}
```

---

## Phase 5.6: Middleware

### CorrelationIdMiddleware.cs

```csharp
namespace AzureBank.Api.Middleware;

/// <summary>
/// Ensures every request has a correlation ID for tracing.
/// Reads from X-Correlation-ID header or generates a new one.
/// </summary>
public class CorrelationIdMiddleware
{
    private const string CorrelationIdHeader = "X-Correlation-ID";
    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[CorrelationIdHeader].FirstOrDefault()
            ?? Guid.NewGuid().ToString();

        context.Items["CorrelationId"] = correlationId;
        context.Response.Headers[CorrelationIdHeader] = correlationId;

        using (Serilog.Context.LogContext.PushProperty("CorrelationId", correlationId))
        {
            await _next(context);
        }
    }
}

public static class CorrelationIdMiddlewareExtensions
{
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<CorrelationIdMiddleware>();
    }
}
```

---

## Phase 5.7: Program.cs Updates

### Complete Program.cs

```csharp
using AzureBank.Api.Handlers;
using AzureBank.Api.Middleware;
using AzureBank.Api.Services.Implementations;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Extensions;
using AzureBank.Shared.Options;
using AzureBank.Shared.Services.Interfaces;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .CreateBootstrapLogger();

builder.Host.UseSerilog();

// Add services
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();

// Add Infrastructure (DbContext, Identity)
builder.Services.AddInfrastructure(builder.Configuration);

// Add JWT Options
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));

// Add JWT Authentication
var jwtOptions = builder.Configuration.GetSection("Jwt").Get<JwtOptions>()!;
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidAudience = jwtOptions.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Secret)),
            ClockSkew = TimeSpan.Zero
        };
    });

// Add FluentValidation
builder.Services.AddValidatorsFromAssemblyContaining<Program>();

// Add Exception Handlers
builder.Services.AddExceptionHandler<ValidationExceptionHandler>();
builder.Services.AddExceptionHandler<AppExceptionHandler>();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// Add Application Services
builder.Services.AddScoped<IPasswordHasher, PasswordHasher>();
builder.Services.AddScoped<IJwtService, JwtService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IAccountService, AccountService>();
builder.Services.AddScoped<ITransactionService, TransactionService>();
builder.Services.AddScoped<ITransferService, TransferService>();
builder.Services.AddScoped<IUserService, UserService>();

// Add CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("Development", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// Configure pipeline
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseExceptionHandler();
app.UseCorrelationId();
app.UseSerilogRequestLogging();
app.UseHttpsRedirection();
app.UseCors("Development");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// Seed database in development
if (app.Environment.IsDevelopment())
{
    await app.SeedDatabaseAsync();
}

app.Run();
```

---

## Best Practices Summary

### 1. Exception Handling
- **DO**: Throw domain exceptions (`NotFoundException`, `BusinessRuleException`, etc.)
- **DO**: Let `IExceptionHandler` convert to `ProblemDetails`
- **DON'T**: Use try-catch in controllers
- **DON'T**: Create custom error response classes (use built-in `ProblemDetails`)

### 2. Validation
- **DO**: Use FluentValidation with manual validation (`ValidateAndThrowAsync`)
- **DO**: Keep validation rules in `ValidationRules` constants
- **DON'T**: Use `FluentValidation.AspNetCore` (deprecated)
- **DON'T**: Duplicate validation rules

### 3. Mapping
- **DO**: Use Mapperly (source generator, no reflection)
- **DO**: Create custom mapping methods for complex transformations
- **DON'T**: Use AutoMapper (runtime reflection overhead)

### 4. Services
- **DO**: Use constructor injection for all dependencies
- **DO**: Throw domain exceptions for business rule violations
- **DO**: Check resource ownership (userId matches)
- **DON'T**: Catch exceptions to return error results

### 5. Controllers
- **DO**: Return `ApiResponse<T>` for consistency
- **DO**: Use `[ProducesResponseType]` for API documentation
- **DO**: Extract userId from claims in base controller or extension
- **DON'T**: Contain business logic (delegate to services)

### 6. Security
- **DO**: Use JWT Bearer authentication
- **DO**: Hash PINs with Argon2id (19MB for PINs)
- **DO**: Validate all input at API boundary
- **DON'T**: Trust client-side validation alone

---

## Files to Delete

Before starting implementation, clean up template files:

```
AzureBank.Api/
├── Controllers/WeatherForecastController.cs  # DELETE
├── WeatherForecast.cs                        # DELETE
└── Services/Interfaces/IPasswordHasher.cs    # DELETE (duplicate - use Shared)

AzureBank.Shared/
├── Class1.cs                                 # DELETE (template)
└── TestException.cs                          # DELETE (if exists)
```

---

## Execution Order

1. Delete template files
2. Create Handlers folder and implement exception handlers
3. Create Mappers folder and implement mappers
4. Create Validators folders and implement validators
5. Implement services in order: JwtService → AuthService → AccountService → TransactionService → TransferService → UserService
6. Implement controllers
7. Add CorrelationIdMiddleware
8. Update Program.cs
9. Test all endpoints with Scalar

---

**Document Status**: COMPLETE
**Ready for**: Phase 5.1 Implementation
