using System.Text;
using System.Text.Json;
using AzureBank.Api.Converters;
using AzureBank.Api.Handlers;
using AzureBank.Api.Mappers;
using AzureBank.Api.Transformers;
using AzureBank.Api.Services.Implementations;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Services.Implementations;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Options;
using AzureBank.Shared.Services.Interfaces;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;

namespace AzureBank.Api.Extensions;

/// <summary>
/// Extension methods for IServiceCollection to configure application services.
/// Follows the enterprise pattern of grouping registrations by concern.
///
/// Benefits:
/// - Clean, readable Program.cs (15-20 lines)
/// - Single Responsibility Principle
/// - Easy to navigate and maintain
/// - Method chaining support
/// - Testable registration logic
///
/// Note: Database services (DbContext, Migrations) are in AzureBank.Infrastructure.
/// Use AddInfrastructure() from AzureBank.Infrastructure.Extensions.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds ASP.NET Core Identity services with ApplicationUser and Guid-based roles.
    /// Configures password requirements and lockout settings.
    /// Requires: AddInfrastructure() must be called first (provides DbContext).
    /// </summary>
    public static IServiceCollection AddIdentityServices(this IServiceCollection services)
    {
        services.AddIdentity<ApplicationUser, IdentityRole<Guid>>(options =>
        {
            // Password requirements (OWASP recommendations)
            options.Password.RequireDigit = true;
            options.Password.RequireLowercase = true;
            options.Password.RequireUppercase = true;
            options.Password.RequireNonAlphanumeric = true;
            options.Password.RequiredLength = 8;
            options.Password.RequiredUniqueChars = 4;

            // Lockout settings
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.AllowedForNewUsers = true;

            // User settings
            options.User.RequireUniqueEmail = true;
            options.User.AllowedUserNameCharacters =
                "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._";

            // Sign-in settings
            options.SignIn.RequireConfirmedEmail = false; // MVP: Skip email verification
            options.SignIn.RequireConfirmedAccount = false;
        })
        .AddEntityFrameworkStores<AzureBankDbContext>()
        .AddDefaultTokenProviders();

        return services;
    }

    /// <summary>
    /// Adds application-specific services including:
    /// - IPasswordHasher (Argon2id for PIN hashing)
    /// - IJwtService (JWT token generation/validation)
    /// - IAuthService (authentication operations)
    /// - IAccountService (account management)
    /// - ITransactionService (deposits/withdrawals)
    /// - ITransferService (money transfers)
    /// - IUserService (user lookup)
    /// - Configuration options (JwtOptions, SeedDataOptions)
    /// </summary>
    public static IServiceCollection AddApplicationServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Configuration options
        services.Configure<JwtOptions>(configuration.GetSection("Jwt"));
        services.Configure<SeedDataOptions>(
            configuration.GetSection(SeedDataOptions.SectionName));

        // Idempotency (ADR-0009). HashKey is a secret (user-secrets/env):
        // fail fast at startup rather than 500 on the first monetary request.
        services.AddOptions<IdempotencyOptions>()
            .Bind(configuration.GetSection(IdempotencyOptions.SectionName))
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.HashKey) && o.HashKey.Length >= 32,
                "Idempotency:HashKey must be configured with at least 32 characters " +
                "(dotnet user-secrets in development; see README)")
            .Validate(
                o => o.Ttl > TimeSpan.Zero
                     && o.ProcessingStaleAfter > TimeSpan.Zero
                     && o.CleanupInterval > TimeSpan.Zero,
                "Idempotency timespans must be positive")
            .ValidateOnStart();

        // Mappers (Mapperly source-generated, stateless - singleton is optimal)
        services.AddSingleton<AccountMapper>();
        services.AddSingleton<TransactionMapper>();
        services.AddSingleton<UserMapper>();

        // Core services
        services.AddScoped<IPasswordHasher, Shared.Services.Implementations.PasswordHasher>();
        services.AddScoped<IJwtService, JwtService>();
        services.AddScoped<IAuthService, AuthService>();
        // PIN attempt-limiting lives in one place; withdrawals depend on the narrow
        // IPinVerifier. PinService persists lockout state in its own DbContext scope.
        services.AddScoped<IPinVerifier, PinService>();
        services.AddScoped<IAccountAccessService, AccountAccessService>();
        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<ITransactionService, TransactionService>();
        services.AddScoped<ITransferService, TransferService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IIdempotencyService, IdempotencyService>();

        // Background sweep of expired idempotency records
        services.AddHostedService<Services.IdempotencyCleanupService>();

        return services;
    }

    /// <summary>
    /// Adds JWT Bearer authentication with configuration from appsettings.
    /// </summary>
    public static IServiceCollection AddJwtAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var jwtOptions = configuration.GetSection("Jwt").Get<JwtOptions>()
            ?? throw new InvalidOperationException("JWT configuration is required");

        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        })
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
                IssuerSigningKey = new SymmetricSecurityKey(
                    Encoding.UTF8.GetBytes(jwtOptions.Secret)),
                ClockSkew = TimeSpan.Zero // No tolerance for expiration
            };

            options.Events = new JwtBearerEvents
            {
                OnAuthenticationFailed = context =>
                {
                    if (context.Exception is SecurityTokenExpiredException)
                    {
                        context.Response.Headers.Append("Token-Expired", "true");
                    }
                    return Task.CompletedTask;
                },

                OnChallenge = async context =>
                {
                    // Skip if response already started
                    if (context.Response.HasStarted)
                    {
                        return;
                    }

                    // Prevent default behavior (empty response)
                    context.HandleResponse();

                    // Determine error type and message
                    var (errorCode, detail) = DetermineAuthError(context);

                    // Build ProblemDetails response (RFC 9457)
                    var problemDetails = new ProblemDetails
                    {
                        Type = "https://httpstatuses.com/401",
                        Title = "Unauthorized",
                        Status = StatusCodes.Status401Unauthorized,
                        Detail = detail,
                        Instance = context.Request.Path
                    };
                    problemDetails.Extensions["errorCode"] = errorCode;
                    problemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;

                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.ContentType = "application/problem+json";

                    await context.Response.WriteAsJsonAsync(problemDetails);
                },

                OnForbidden = async context =>
                {
                    // Skip if response already started
                    if (context.Response.HasStarted)
                    {
                        return;
                    }

                    // Build ProblemDetails response (RFC 9457)
                    var problemDetails = new ProblemDetails
                    {
                        Type = "https://httpstatuses.com/403",
                        Title = "Forbidden",
                        Status = StatusCodes.Status403Forbidden,
                        Detail = "You do not have permission to access this resource.",
                        Instance = context.Request.Path
                    };
                    problemDetails.Extensions["errorCode"] = ErrorCodes.Forbidden;
                    problemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;

                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    context.Response.ContentType = "application/problem+json";

                    await context.Response.WriteAsJsonAsync(problemDetails);
                }
            };
        });

        return services;
    }

    /// <summary>
    /// Determines the appropriate error code and message based on the authentication failure.
    /// Maps specific SecurityToken exceptions to user-friendly error codes.
    /// </summary>
    private static (string ErrorCode, string Detail) DetermineAuthError(JwtBearerChallengeContext context)
    {
        // Check the AuthenticateFailure exception type
        return context.AuthenticateFailure switch
        {
            SecurityTokenExpiredException => (
                ErrorCodes.TokenExpired,
                "The authentication token has expired."),

            SecurityTokenInvalidSignatureException => (
                ErrorCodes.TokenInvalid,
                "The authentication token signature is invalid."),

            SecurityTokenMalformedException => (
                ErrorCodes.TokenMalformed,
                "The authentication token format is invalid."),

            SecurityTokenValidationException => (
                ErrorCodes.TokenInvalid,
                "The authentication token validation failed."),

            // No exception means no token was provided
            null when string.IsNullOrEmpty(context.Request.Headers.Authorization) => (
                ErrorCodes.TokenMissing,
                "Authentication is required to access this resource."),

            // Token provided but validation failed for unknown reason
            null => (
                ErrorCodes.TokenInvalid,
                "The authentication token is invalid."),

            // Catch-all for other exceptions
            _ => (
                ErrorCodes.TokenInvalid,
                "The authentication token validation failed.")
        };
    }

    /// <summary>
    /// Adds FluentValidation validators from the assembly.
    /// </summary>
    public static IServiceCollection AddValidation(this IServiceCollection services)
    {
        services.AddValidatorsFromAssemblyContaining<Program>();
        return services;
    }

    /// <summary>
    /// Adds exception handlers for ProblemDetails responses (RFC 7807).
    /// Order matters: first registered = first tried.
    /// </summary>
    public static IServiceCollection AddExceptionHandlers(this IServiceCollection services)
    {
        services.AddExceptionHandler<ValidationExceptionHandler>();
        services.AddExceptionHandler<AppExceptionHandler>();
        services.AddExceptionHandler<GlobalExceptionHandler>();
        services.AddProblemDetails();

        return services;
    }

    /// <summary>
    /// Adds API documentation using Scalar (NOT Swagger).
    /// Scalar provides a modern, clean API documentation UI.
    /// Includes JWT Bearer authentication scheme for the Scalar "Configure" button.
    /// </summary>
    public static IServiceCollection AddApiDocumentation(this IServiceCollection services)
    {
        services.AddOpenApi(options =>
        {
            // Schema transformer: Convert enums to string type with enum values
            // This fixes Microsoft.AspNetCore.OpenApi issue where JsonStringEnumConverter
            // is not respected in generated schemas
            options.AddSchemaTransformer<JsonStringEnumSchemaTransformer>();

            // Schema transformer: Fix .NET 10 integer/string union type quirk
            // Ensures minimum/maximum constraints are enforced on numeric types
            // Reference: https://svrooij.io/2025/12/19/openapi-dotnet-10-number-quirk/
            options.AddSchemaTransformer<IntegerSchemaTransformer>();

            // Schema transformer: Apply Data Annotation constraints to schemas
            // This fixes Schemathesis "API rejected schema-compliant request" errors
            options.AddSchemaTransformer<DataAnnotationSchemaTransformer>();

            // Operation transformer: Add 401/403 responses to authenticated endpoints
            // This fixes Schemathesis "Undocumented HTTP status code: 401" errors
            options.AddOperationTransformer<AuthorizationResponseTransformer>();

            // Operation transformer: Add 400 Bad Request to endpoints with request bodies
            // This documents validation error responses for Schemathesis compliance
            options.AddOperationTransformer<ValidationResponseTransformer>();

            // Operation transformer: Add 404 Not Found to endpoints with path parameters
            // This documents resource lookup failures for Schemathesis compliance
            options.AddOperationTransformer<NotFoundResponseTransformer>();

            // Operation transformer: Mark [AllowAnonymous] endpoints as not requiring auth
            // This fixes Schemathesis "Missing header not rejected" false positives
            options.AddOperationTransformer<AnonymousEndpointTransformer>();

            // Document transformer: Add JWT Bearer authentication scheme
            options.AddDocumentTransformer<BearerSecuritySchemeTransformer>();

            // Document transformer: Add constraints to query parameters
            // Fixes PageSize/Page minimum constraints for Schemathesis compliance
            options.AddDocumentTransformer<QueryParameterConstraintsTransformer>();

            // Document transformer: Apply validation constraints to request body schemas
            // This fixes .NET 10's IOpenApiSchemaTransformer limitation where custom
            // validation attributes ([MoneyRange], [Password], etc.) don't persist
            options.AddDocumentTransformer<RequestBodySchemaConstraintsTransformer>();

            // Document transformer: Add x-business-rules extension to document
            // cross-field constraints that cannot be expressed in JSON Schema
            // Reference: project-docs/30-business-rule-validation-implementation-plan.md
            options.AddDocumentTransformer<BusinessRulesDocumentTransformer>();

            // Operation transformer: Document the required Idempotency-Key
            // header + 409/422 responses on [RequireIdempotency] endpoints
            // (ADR-0009). Keeps spec 1:1 with live and Schemathesis green.
            options.AddOperationTransformer<IdempotencyOperationTransformer>();
        });

        return services;
    }

    /// <summary>
    /// Adds controller services with JSON serialization options.
    /// </summary>
    public static IServiceCollection AddApiControllers(this IServiceCollection services)
    {
        services.AddControllers()
            .AddJsonOptions(options =>
            {
                // Use camelCase for JSON properties (JavaScript convention)
                options.JsonSerializerOptions.PropertyNamingPolicy =
                    System.Text.Json.JsonNamingPolicy.CamelCase;

                // Strict enum converter: rejects integer values, only accepts strings
                // Fixes Schemathesis "API accepted schema-violating request" for enum fields
                options.JsonSerializerOptions.Converters.Add(
                    new Converters.StrictJsonStringEnumConverterFactory());

                // RFC 3339 DateTime format for OpenAPI compliance
                // Outputs: "2026-01-12T10:48:26.5907775Z" instead of "2026-01-12T10:48:26.5907775"
                options.JsonSerializerOptions.Converters.Add(
                    new Rfc3339DateTimeConverter());
            });

        return services;
    }

    /// <summary>
    /// Adds CORS policy for frontend development.
    /// </summary>
    public static IServiceCollection AddCorsPolicies(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddCors(options =>
        {
            options.AddPolicy("AllowFrontend", policy =>
            {
                policy
                    .WithOrigins(
                        "http://localhost:5173",  // Vite dev server
                        "http://localhost:3000",  // Alternative frontend port
                        "https://localhost:5173")
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials()
                    // Response headers are NOT readable by browser JS unless
                    // exposed: the frontend must be able to see replays.
                    .WithExposedHeaders(IdempotencyConstants.ReplayedHeaderName);
            });

            // Development: Allow any origin
            options.AddPolicy("Development", policy =>
            {
                policy
                    .AllowAnyOrigin()
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .WithExposedHeaders(IdempotencyConstants.ReplayedHeaderName);
            });
        });

        return services;
    }
}
