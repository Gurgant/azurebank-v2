# Cross-Cutting Concerns Design
## AzureBank - Error Handling, Logging, Observability & Mapping

**Document Version**: 1.0
**Created**: 2026-01-08
**Author**: System Architect
**Status**: COMPLETE

---

## 1. Executive Summary

This document defines the design for cross-cutting concerns that span the entire application:

| Concern | Technology | Purpose |
|---------|------------|---------|
| Error Handling | Custom Middleware + Result Pattern | Consistent error responses |
| Logging | Serilog | Structured logging |
| Observability | OpenTelemetry | Distributed tracing, metrics |
| Object Mapping | Mapperly | High-performance DTO mapping |

---

## 2. Error & Exception Handling

### 2.1 Design Philosophy

**Principle**: Fail fast internally, fail gracefully externally.

```
┌─────────────────────────────────────────────────────────────────────┐
│                    ERROR HANDLING ARCHITECTURE                       │
├─────────────────────────────────────────────────────────────────────┤
│                                                                     │
│  HTTP Request                                                       │
│       │                                                             │
│       ▼                                                             │
│  ┌─────────────────────────────────────────────────────────────┐   │
│  │           Global Exception Middleware                        │   │
│  │  • Catches ALL unhandled exceptions                         │   │
│  │  • Logs full stack trace (internal)                         │   │
│  │  • Returns sanitized error (external)                       │   │
│  │  • Adds correlation ID to response                          │   │
│  └─────────────────────────────────────────────────────────────┘   │
│       │                                                             │
│       ▼                                                             │
│  ┌─────────────────────────────────────────────────────────────┐   │
│  │           Validation Middleware (FluentValidation)           │   │
│  │  • Validates request DTOs                                   │   │
│  │  • Returns 422 with field errors                            │   │
│  └─────────────────────────────────────────────────────────────┘   │
│       │                                                             │
│       ▼                                                             │
│  ┌─────────────────────────────────────────────────────────────┐   │
│  │           Controllers → Services → Repositories              │   │
│  │  • Use Result<T> pattern for expected failures              │   │
│  │  • Throw exceptions for unexpected failures                 │   │
│  └─────────────────────────────────────────────────────────────┘   │
│                                                                     │
└─────────────────────────────────────────────────────────────────────┘
```

### 2.2 Exception Types

```csharp
// ============================================
// CUSTOM EXCEPTION HIERARCHY
// ============================================

/// <summary>
/// Base exception for all application exceptions
/// </summary>
public abstract class AppException : Exception
{
    public string ErrorCode { get; }
    public int StatusCode { get; }

    protected AppException(string message, string errorCode, int statusCode)
        : base(message)
    {
        ErrorCode = errorCode;
        StatusCode = statusCode;
    }
}

/// <summary>
/// 404 - Resource not found
/// </summary>
public class NotFoundException : AppException
{
    public NotFoundException(string resource, string identifier)
        : base($"{resource} with identifier '{identifier}' was not found.",
               "NOT_FOUND", 404) { }
}

/// <summary>
/// 400 - Bad request / Business rule violation
/// </summary>
public class BusinessRuleException : AppException
{
    public BusinessRuleException(string message, string errorCode = "BUSINESS_RULE_VIOLATION")
        : base(message, errorCode, 400) { }
}

/// <summary>
/// 400 - Insufficient funds for transaction
/// </summary>
public class InsufficientFundsException : BusinessRuleException
{
    public decimal Available { get; }
    public decimal Requested { get; }

    public InsufficientFundsException(decimal available, decimal requested)
        : base($"Insufficient funds. Available: {available:C}, Requested: {requested:C}",
               "INSUFFICIENT_FUNDS")
    {
        Available = available;
        Requested = requested;
    }
}

/// <summary>
/// 401 - Authentication failed
/// </summary>
public class AuthenticationException : AppException
{
    public AuthenticationException(string message = "Authentication failed")
        : base(message, "AUTHENTICATION_FAILED", 401) { }
}

/// <summary>
/// 403 - Authorization failed
/// </summary>
public class AuthorizationException : AppException
{
    public AuthorizationException(string message = "Access denied")
        : base(message, "ACCESS_DENIED", 403) { }
}

/// <summary>
/// 409 - Conflict (e.g., duplicate AzureTag)
/// </summary>
public class ConflictException : AppException
{
    public ConflictException(string message, string errorCode = "CONFLICT")
        : base(message, errorCode, 409) { }
}

/// <summary>
/// 429 - Too many requests (rate limiting)
/// </summary>
public class RateLimitException : AppException
{
    public int RetryAfterSeconds { get; }

    public RateLimitException(int retryAfterSeconds = 60)
        : base($"Too many requests. Please retry after {retryAfterSeconds} seconds.",
               "RATE_LIMIT_EXCEEDED", 429)
    {
        RetryAfterSeconds = retryAfterSeconds;
    }
}
```

### 2.3 Global Exception Middleware

```csharp
// ============================================
// GLOBAL EXCEPTION HANDLER MIDDLEWARE
// ============================================

public class GlobalExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;

    public GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var correlationId = context.TraceIdentifier;
        var response = context.Response;
        response.ContentType = "application/json";

        var errorResponse = exception switch
        {
            AppException appEx => new ErrorResponse
            {
                Type = appEx.ErrorCode,
                Message = appEx.Message,
                CorrelationId = correlationId,
                StatusCode = appEx.StatusCode
            },

            FluentValidation.ValidationException valEx => new ErrorResponse
            {
                Type = "VALIDATION_ERROR",
                Message = "Validation failed",
                CorrelationId = correlationId,
                StatusCode = 422,
                Errors = valEx.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(
                        g => g.Key,
                        g => g.Select(e => e.ErrorMessage).ToArray()
                    )
            },

            _ => new ErrorResponse
            {
                Type = "INTERNAL_SERVER_ERROR",
                Message = "An unexpected error occurred. Please try again later.",
                CorrelationId = correlationId,
                StatusCode = 500
            }
        };

        // Log the exception with full details (internal)
        if (exception is AppException appException)
        {
            _logger.LogWarning(exception,
                "Application exception occurred. Code: {ErrorCode}, CorrelationId: {CorrelationId}",
                appException.ErrorCode, correlationId);
        }
        else
        {
            _logger.LogError(exception,
                "Unhandled exception occurred. CorrelationId: {CorrelationId}",
                correlationId);
        }

        response.StatusCode = errorResponse.StatusCode;
        await response.WriteAsJsonAsync(errorResponse);
    }
}

// ============================================
// ERROR RESPONSE DTO
// ============================================

public class ErrorResponse
{
    public string Type { get; set; } = null!;
    public string Message { get; set; } = null!;
    public string CorrelationId { get; set; } = null!;
    public int StatusCode { get; set; }
    public Dictionary<string, string[]>? Errors { get; set; }
}
```

### 2.4 Result Pattern for Expected Failures

```csharp
// ============================================
// RESULT PATTERN FOR SERVICE LAYER
// ============================================

public class Result<T>
{
    public bool IsSuccess { get; }
    public T? Value { get; }
    public string? Error { get; }
    public string? ErrorCode { get; }

    private Result(bool isSuccess, T? value, string? error, string? errorCode)
    {
        IsSuccess = isSuccess;
        Value = value;
        Error = error;
        ErrorCode = errorCode;
    }

    public static Result<T> Success(T value) => new(true, value, null, null);
    public static Result<T> Failure(string error, string errorCode = "ERROR") =>
        new(false, default, error, errorCode);

    public Result<TNew> Map<TNew>(Func<T, TNew> mapper) =>
        IsSuccess ? Result<TNew>.Success(mapper(Value!)) : Result<TNew>.Failure(Error!, ErrorCode!);
}

// Usage in service:
public async Task<Result<TransferResponse>> TransferAsync(TransferRequest request)
{
    var account = await _accountRepository.GetByIdAsync(request.FromAccountId);
    if (account == null)
        return Result<TransferResponse>.Failure("Account not found", "ACCOUNT_NOT_FOUND");

    if (account.Balance < request.Amount)
        return Result<TransferResponse>.Failure(
            $"Insufficient funds. Available: {account.Balance}",
            "INSUFFICIENT_FUNDS");

    // ... perform transfer
    return Result<TransferResponse>.Success(new TransferResponse { ... });
}

// Usage in controller:
[HttpPost("transfer")]
public async Task<IActionResult> Transfer([FromBody] TransferRequest request)
{
    var result = await _transferService.TransferAsync(request);

    if (!result.IsSuccess)
        return result.ErrorCode switch
        {
            "ACCOUNT_NOT_FOUND" => NotFound(new { error = result.Error }),
            "INSUFFICIENT_FUNDS" => BadRequest(new { error = result.Error }),
            _ => BadRequest(new { error = result.Error })
        };

    return Ok(result.Value);
}
```

### 2.5 Frontend Error Handling (RTK Query)

```typescript
// ============================================
// RTK QUERY ERROR MIDDLEWARE
// ============================================

// src/app/errorMiddleware.ts
import { isRejectedWithValue, Middleware } from '@reduxjs/toolkit';
import { logout } from '../features/auth/authSlice';

interface ApiError {
  type: string;
  message: string;
  correlationId: string;
  statusCode: number;
  errors?: Record<string, string[]>;
}

export const errorMiddleware: Middleware = (api) => (next) => (action) => {
  if (isRejectedWithValue(action)) {
    const payload = action.payload as { status: number; data: ApiError };

    // Handle 401 - Unauthorized (token expired)
    if (payload.status === 401) {
      api.dispatch(logout());
      // Optionally show toast: "Session expired. Please log in again."
    }

    // Handle 500 - Server Error
    if (payload.status >= 500) {
      console.error('Server Error:', payload.data);
      // Show global toast: "Something went wrong. Please try again."
    }

    // Handle 429 - Rate Limit
    if (payload.status === 429) {
      // Show toast: "Too many requests. Please slow down."
    }
  }

  return next(action);
};

// Add to store.ts
import { errorMiddleware } from './errorMiddleware';

export const store = configureStore({
  reducer: { ... },
  middleware: (getDefaultMiddleware) =>
    getDefaultMiddleware().concat(apiSlice.middleware, errorMiddleware),
});
```

---

## 3. Logging Strategy (Serilog)

### 3.1 Design Philosophy

**Principle**: Log enough to debug, not so much to drown.

| Log Level | When to Use | Examples |
|-----------|-------------|----------|
| **Fatal** | Application crash | Unrecoverable errors |
| **Error** | Unexpected failures | Unhandled exceptions, external service failures |
| **Warning** | Recoverable issues | Business rule violations, validation failures |
| **Information** | Key events | User login, transaction completed, API calls |
| **Debug** | Developer details | Method entry/exit, variable values |
| **Verbose** | Trace everything | Every step (development only) |

### 3.2 Serilog Configuration

```csharp
// ============================================
// SERILOG CONFIGURATION
// ============================================

// Program.cs
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
    .MinimumLevel.Override("System", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithEnvironmentName()
    .Enrich.WithProperty("Application", "AzureBank")
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
    .WriteTo.File(
        new CompactJsonFormatter(),
        "logs/azurebank-.json",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30)
    .CreateLogger();

builder.Host.UseSerilog();

// Configure request logging
app.UseSerilogRequestLogging(options =>
{
    options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";
    options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
    {
        diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value);
        diagnosticContext.Set("UserAgent", httpContext.Request.Headers["User-Agent"].FirstOrDefault());
        diagnosticContext.Set("UserId", httpContext.User?.FindFirst("sub")?.Value ?? "anonymous");
    };
});
```

### 3.3 Structured Logging Patterns

```csharp
// ============================================
// LOGGING PATTERNS
// ============================================

public class TransactionService
{
    private readonly ILogger<TransactionService> _logger;

    // DO: Use structured logging with named parameters
    public async Task<Transaction> DepositAsync(Guid accountId, decimal amount)
    {
        _logger.LogInformation(
            "Processing deposit. AccountId: {AccountId}, Amount: {Amount:C}",
            accountId, amount);

        try
        {
            var transaction = await _repository.CreateDepositAsync(accountId, amount);

            _logger.LogInformation(
                "Deposit completed. TransactionId: {TransactionId}, AccountId: {AccountId}, " +
                "Amount: {Amount:C}, NewBalance: {NewBalance:C}",
                transaction.Id, accountId, amount, transaction.BalanceAfter);

            return transaction;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Deposit failed. AccountId: {AccountId}, Amount: {Amount:C}",
                accountId, amount);
            throw;
        }
    }

    // DON'T: String interpolation (loses structure)
    // _logger.LogInformation($"Deposit for account {accountId}"); // BAD!
}

// ============================================
// SENSITIVE DATA HANDLING
// ============================================

// DO: Mask sensitive data
_logger.LogInformation("User logged in. Email: {Email}", MaskEmail(user.Email));
// Output: User logged in. Email: j***@example.com

// DON'T: Log passwords, tokens, or full account numbers
// _logger.LogInformation("Login attempt with password: {Password}", request.Password); // NEVER!

public static string MaskEmail(string email)
{
    var parts = email.Split('@');
    if (parts.Length != 2) return "***";
    return $"{parts[0][0]}***@{parts[1]}";
}

public static string MaskAccountNumber(string accountNumber)
{
    // AB-1234-5678-90 → AB-****-****-90
    if (accountNumber.Length < 6) return "****";
    return $"{accountNumber[..3]}****-****-{accountNumber[^2..]}";
}
```

### 3.4 Log Sinks Configuration

```csharp
// ============================================
// MULTIPLE SINKS FOR DIFFERENT ENVIRONMENTS
// ============================================

// appsettings.json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft": "Warning",
        "System": "Warning"
      }
    },
    "WriteTo": [
      {
        "Name": "Console",
        "Args": {
          "theme": "Serilog.Sinks.SystemConsole.Themes.AnsiConsoleTheme::Code, Serilog.Sinks.Console"
        }
      },
      {
        "Name": "File",
        "Args": {
          "path": "logs/azurebank-.log",
          "rollingInterval": "Day",
          "retainedFileCountLimit": 30,
          "formatter": "Serilog.Formatting.Compact.CompactJsonFormatter, Serilog.Formatting.Compact"
        }
      }
    ],
    "Enrich": ["FromLogContext", "WithMachineName", "WithEnvironmentName"]
  }
}

// Production: Add Seq or Application Insights
// .WriteTo.Seq("http://localhost:5341")
// .WriteTo.ApplicationInsights(connectionString, TelemetryConverter.Traces)
```

---

## 4. OpenTelemetry Observability

### 4.1 Design Philosophy

**The Three Pillars of Observability**:
1. **Traces** - Follow a request through the system
2. **Metrics** - Aggregate measurements over time
3. **Logs** - Structured event records (covered above)

```
┌─────────────────────────────────────────────────────────────────────┐
│                    OPENTELEMETRY ARCHITECTURE                        │
├─────────────────────────────────────────────────────────────────────┤
│                                                                     │
│  ┌──────────────┐     ┌──────────────┐     ┌──────────────┐        │
│  │   Frontend   │────▶│   Backend    │────▶│   Database   │        │
│  │    React     │     │   .NET API   │     │  SQL Server  │        │
│  └──────────────┘     └──────────────┘     └──────────────┘        │
│         │                    │                    │                 │
│         │                    │                    │                 │
│         ▼                    ▼                    ▼                 │
│  ┌─────────────────────────────────────────────────────────────┐   │
│  │              OpenTelemetry Collector                         │   │
│  │  • Receives traces, metrics, logs                           │   │
│  │  • Processes and exports to backends                        │   │
│  └─────────────────────────────────────────────────────────────┘   │
│         │                    │                    │                 │
│         ▼                    ▼                    ▼                 │
│  ┌───────────┐       ┌───────────┐       ┌───────────┐            │
│  │   Jaeger  │       │Prometheus │       │    Seq    │            │
│  │  Traces   │       │  Metrics  │       │   Logs    │            │
│  └───────────┘       └───────────┘       └───────────┘            │
│                                                                     │
└─────────────────────────────────────────────────────────────────────┘
```

### 4.2 OpenTelemetry Configuration (.NET)

```csharp
// ============================================
// OPENTELEMETRY SETUP
// ============================================

// Add NuGet packages:
// - OpenTelemetry
// - OpenTelemetry.Extensions.Hosting
// - OpenTelemetry.Instrumentation.AspNetCore
// - OpenTelemetry.Instrumentation.Http
// - OpenTelemetry.Instrumentation.SqlClient
// - OpenTelemetry.Instrumentation.EntityFrameworkCore
// - OpenTelemetry.Exporter.OpenTelemetryProtocol (for OTLP)
// - OpenTelemetry.Exporter.Console (for development)

// Program.cs
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var serviceName = "AzureBank.API";
var serviceVersion = "1.0.0";

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource
        .AddService(serviceName: serviceName, serviceVersion: serviceVersion)
        .AddAttributes(new Dictionary<string, object>
        {
            ["environment"] = builder.Environment.EnvironmentName,
            ["host.name"] = Environment.MachineName
        }))
    .WithTracing(tracing => tracing
        // Automatic instrumentation
        .AddAspNetCoreInstrumentation(options =>
        {
            options.RecordException = true;
            options.Filter = (httpContext) =>
                !httpContext.Request.Path.StartsWithSegments("/health");
        })
        .AddHttpClientInstrumentation()
        .AddSqlClientInstrumentation(options =>
        {
            options.SetDbStatementForText = true;
            options.RecordException = true;
        })
        .AddEntityFrameworkCoreInstrumentation()
        // Custom instrumentation source
        .AddSource("AzureBank.Transactions")
        // Exporters
        .AddConsoleExporter() // Development only
        .AddOtlpExporter(options =>
        {
            options.Endpoint = new Uri("http://localhost:4317");
        }))
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        // Custom metrics
        .AddMeter("AzureBank.Transactions")
        // Exporters
        .AddConsoleExporter() // Development only
        .AddOtlpExporter(options =>
        {
            options.Endpoint = new Uri("http://localhost:4317");
        }));
```

### 4.3 Custom Tracing

```csharp
// ============================================
// CUSTOM TRANSACTION TRACING
// ============================================

using System.Diagnostics;
using System.Diagnostics.Metrics;

public class TransactionService
{
    // Define activity source for custom spans
    private static readonly ActivitySource ActivitySource = new("AzureBank.Transactions");

    // Define meter for custom metrics
    private static readonly Meter Meter = new("AzureBank.Transactions", "1.0.0");
    private static readonly Counter<long> TransactionCounter = Meter.CreateCounter<long>(
        "azurebank.transactions.count",
        description: "Number of transactions processed");
    private static readonly Histogram<double> TransactionDuration = Meter.CreateHistogram<double>(
        "azurebank.transactions.duration",
        unit: "ms",
        description: "Duration of transaction processing");

    public async Task<Transaction> DepositAsync(Guid accountId, decimal amount)
    {
        using var activity = ActivitySource.StartActivity("Deposit", ActivityKind.Internal);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Add custom tags to the span
            activity?.SetTag("account.id", accountId.ToString());
            activity?.SetTag("transaction.type", "deposit");
            activity?.SetTag("transaction.amount", amount);

            var transaction = await ProcessDepositAsync(accountId, amount);

            // Record success
            activity?.SetStatus(ActivityStatusCode.Ok);
            activity?.SetTag("transaction.id", transaction.Id.ToString());

            // Record metrics
            TransactionCounter.Add(1, new KeyValuePair<string, object?>("type", "deposit"));
            TransactionDuration.Record(stopwatch.ElapsedMilliseconds,
                new KeyValuePair<string, object?>("type", "deposit"));

            return transaction;
        }
        catch (Exception ex)
        {
            // Record failure
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.RecordException(ex);

            TransactionCounter.Add(1,
                new KeyValuePair<string, object?>("type", "deposit"),
                new KeyValuePair<string, object?>("status", "failed"));

            throw;
        }
    }
}
```

### 4.4 Correlation ID Propagation

```csharp
// ============================================
// CORRELATION ID MIDDLEWARE
// ============================================

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
        // Get or create correlation ID
        var correlationId = context.Request.Headers[CorrelationIdHeader].FirstOrDefault()
            ?? Activity.Current?.TraceId.ToString()
            ?? Guid.NewGuid().ToString();

        // Set on response
        context.Response.Headers[CorrelationIdHeader] = correlationId;

        // Add to log context
        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await _next(context);
        }
    }
}

// Register in Program.cs
app.UseMiddleware<CorrelationIdMiddleware>();
```

### 4.5 Frontend Tracing (Optional)

```typescript
// ============================================
// FRONTEND OPENTELEMETRY (OPTIONAL)
// ============================================

// For full distributed tracing, add to frontend:
// npm install @opentelemetry/api @opentelemetry/sdk-trace-web @opentelemetry/instrumentation-fetch

import { WebTracerProvider } from '@opentelemetry/sdk-trace-web';
import { FetchInstrumentation } from '@opentelemetry/instrumentation-fetch';
import { registerInstrumentations } from '@opentelemetry/instrumentation';
import { OTLPTraceExporter } from '@opentelemetry/exporter-trace-otlp-http';

const provider = new WebTracerProvider();
provider.addSpanProcessor(
  new SimpleSpanProcessor(
    new OTLPTraceExporter({ url: 'http://localhost:4318/v1/traces' })
  )
);
provider.register();

registerInstrumentations({
  instrumentations: [new FetchInstrumentation()],
});
```

---

## 5. Object Mapping with Mapperly

### 5.1 Why Mapperly (Not AutoMapper)

| Criteria | Mapperly | AutoMapper | Winner |
|----------|----------|------------|--------|
| **Performance** | Compile-time, zero reflection | Runtime reflection | Mapperly |
| **License** | MIT (free forever) | Paid for LTS | Mapperly |
| **.NET 10 Support** | Excellent | Good | Mapperly |
| **AOT Compatible** | Yes | Limited | Mapperly |
| **Type Safety** | Compile-time errors | Runtime errors | Mapperly |
| **Learning Curve** | Simple attributes | More complex | Mapperly |

### 5.2 Mapperly Setup

```csharp
// ============================================
// MAPPERLY CONFIGURATION
// ============================================

// Add NuGet package:
// - Riok.Mapperly

// Create mapper class
using Riok.Mapperly.Abstractions;

[Mapper]
public partial class AccountMapper
{
    // Simple mapping (properties match)
    public partial AccountDto ToDto(Account account);

    // Collection mapping
    public partial IEnumerable<AccountDto> ToDtos(IEnumerable<Account> accounts);

    // Reverse mapping
    public partial Account ToEntity(CreateAccountRequest request);

    // Custom property mapping
    [MapProperty(nameof(Account.CreatedAt), nameof(AccountDto.CreatedDate))]
    public partial AccountDto ToDtoWithRename(Account account);

    // Ignore properties
    [MapperIgnoreSource(nameof(Account.PasswordHash))]
    public partial UserDto ToDto(User user);

    // Custom conversion
    [MapProperty(nameof(User.FirstName), nameof(UserDto.DisplayName), Use = nameof(CombineNames))]
    [MapProperty(nameof(User.LastName), nameof(UserDto.DisplayName), Use = nameof(CombineNames))]
    public partial UserDto ToUserDto(User user);

    private string CombineNames(string firstName, string lastName) => $"{firstName} {lastName}";
}

// Usage in service
public class AccountService
{
    private readonly AccountMapper _mapper = new();

    public async Task<AccountDto> GetAccountAsync(Guid id)
    {
        var account = await _repository.GetByIdAsync(id);
        return _mapper.ToDto(account);
    }
}
```

### 5.3 Complete Mapper Definitions

```csharp
// ============================================
// ALL MAPPERS FOR AZUREBANK
// ============================================

[Mapper]
public partial class UserMapper
{
    // Registration
    public partial User ToEntity(RegisterRequest request);

    // Response
    [MapperIgnoreSource(nameof(User.PasswordHash))]
    public partial UserDto ToDto(User user);

    // For recipient search (masked name)
    public RecipientDto ToRecipientDto(User user)
    {
        return new RecipientDto
        {
            AzureTag = user.AzureTag,
            DisplayName = $"{user.FirstName} {user.LastName[0]}."  // "John S."
        };
    }
}

[Mapper]
public partial class AccountMapper
{
    public partial AccountDto ToDto(Account account);
    public partial IEnumerable<AccountDto> ToDtos(IEnumerable<Account> accounts);

    [MapperIgnoreTarget(nameof(Account.Id))]
    [MapperIgnoreTarget(nameof(Account.AccountNumber))]
    [MapperIgnoreTarget(nameof(Account.Balance))]
    [MapperIgnoreTarget(nameof(Account.CreatedAt))]
    [MapperIgnoreTarget(nameof(Account.UpdatedAt))]
    public partial Account ToEntity(CreateAccountRequest request);
}

[Mapper]
public partial class TransactionMapper
{
    public partial TransactionDto ToDto(Transaction transaction);
    public partial IEnumerable<TransactionDto> ToDtos(IEnumerable<Transaction> transactions);

    // For paginated response
    public PaginatedResponse<TransactionDto> ToPaginatedDto(
        IEnumerable<Transaction> transactions,
        int page,
        int pageSize,
        int totalItems)
    {
        return new PaginatedResponse<TransactionDto>
        {
            Data = ToDtos(transactions).ToList(),
            Page = page,
            PageSize = pageSize,
            TotalItems = totalItems,
            TotalPages = (int)Math.Ceiling(totalItems / (double)pageSize)
        };
    }
}
```

### 5.4 Register Mappers with DI

```csharp
// ============================================
// DEPENDENCY INJECTION REGISTRATION
// ============================================

// Extensions/MapperExtensions.cs
public static class MapperExtensions
{
    public static IServiceCollection AddMappers(this IServiceCollection services)
    {
        // Mapperly generates static methods, but we can wrap in interfaces for DI
        services.AddSingleton<UserMapper>();
        services.AddSingleton<AccountMapper>();
        services.AddSingleton<TransactionMapper>();

        return services;
    }
}

// Program.cs
builder.Services.AddMappers();
```

---

## 6. Updated Tech Stack

### 6.1 New ADRs to Add

| ADR | Technology | Purpose |
|-----|------------|---------|
| ADR-017 | OpenTelemetry | Distributed tracing & metrics |
| ADR-018 | Mapperly | Object mapping |
| ADR-019 | Global Exception Handler | Consistent error responses |
| ADR-020 | Correlation ID | Request tracking |

### 6.2 NuGet Packages Summary

```xml
<!-- Backend Packages to Add -->
<PackageReference Include="Serilog.AspNetCore" Version="8.*" />
<PackageReference Include="Serilog.Formatting.Compact" Version="2.*" />
<PackageReference Include="Serilog.Enrichers.Environment" Version="2.*" />

<PackageReference Include="OpenTelemetry" Version="1.*" />
<PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="1.*" />
<PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" Version="1.*" />
<PackageReference Include="OpenTelemetry.Instrumentation.Http" Version="1.*" />
<PackageReference Include="OpenTelemetry.Instrumentation.SqlClient" Version="1.*" />
<PackageReference Include="OpenTelemetry.Instrumentation.EntityFrameworkCore" Version="1.*" />
<PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.*" />

<PackageReference Include="Riok.Mapperly" Version="3.*" />
```

---

## 7. Implementation Checklist

### Phase 5 (Security Design) Tasks
- [ ] Implement GlobalExceptionMiddleware
- [ ] Create custom exception hierarchy
- [ ] Implement Result<T> pattern
- [ ] Configure Serilog with proper sinks
- [ ] Add sensitive data masking

### Phase 7 (Backend Architecture) Tasks
- [ ] Configure OpenTelemetry tracing
- [ ] Add custom transaction tracing
- [ ] Create Mapperly mapper classes
- [ ] Add correlation ID middleware
- [ ] Test error handling flows

---

## 8. Best Practices Summary

### Error Handling
1. **Never expose stack traces** to end users
2. **Always include correlation ID** in error responses
3. **Use Result<T> pattern** for expected failures
4. **Throw exceptions** only for unexpected failures
5. **Log errors with full context** internally

### Logging
1. **Use structured logging** with named parameters
2. **Never log sensitive data** (passwords, tokens, full account numbers)
3. **Set appropriate log levels** per environment
4. **Include correlation ID** in all log entries
5. **Use scopes** for related operations

### Observability
1. **Trace all external calls** (HTTP, database)
2. **Create custom spans** for business operations
3. **Record metrics** for key performance indicators
4. **Propagate trace context** across services
5. **Set meaningful span names** and tags

### Object Mapping
1. **Use compile-time mappers** (Mapperly) over reflection
2. **Create dedicated mapper classes** per domain
3. **Ignore sensitive properties** explicitly
4. **Use custom methods** for complex mappings
5. **Test mappings** with unit tests

---

**Document Status**: COMPLETE - Ready for implementation
**Next Steps**: Update 03-tech-stack-decisions.md with new ADRs
