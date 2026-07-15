# ADR-0005: Scalar API Documentation

**Status**: Accepted

**Date**: 2026-01-10

**Decision Makers**: Vladislav Aleshaev

---

## Context

The REST API needs interactive documentation for:
- Developer onboarding
- API exploration and testing
- Client integration support
- Contract documentation

## Decision Drivers

- **Developer Experience**: Modern, intuitive UI
- **Performance**: Fast loading and rendering
- **Customization**: Theming and branding options
- **Maintenance**: Active development and support
- **Features**: Code samples, try-it-out, authentication

## Considered Options

1. **Scalar**: Modern API documentation platform
2. **Swagger UI**: OpenAPI's official UI
3. **ReDoc**: Redocly's documentation generator
4. **Stoplight Elements**: Stoplight's web components
5. **RapiDoc**: Open-source API documentation

## Decision

Use **Scalar** (`Scalar.AspNetCore` v2.0.10) for API documentation.

```csharp
// Program.cs
if (app.Environment.IsDevelopment())
{
    app.MapScalarApiReference(options =>
    {
        options
            .WithTitle("AzureBank API")
            .WithTheme(ScalarTheme.BluePlanet)
            .WithDefaultHttpClient(ScalarTarget.CSharp, ScalarClient.HttpClient);
    });
}
```

Accessible at: `https://localhost:7215/scalar/v1`

## Rationale

### Why Scalar over Swagger UI?

1. **Modern UI**: Clean, contemporary design vs Swagger's dated interface
2. **Better Performance**: Faster rendering of large schemas
3. **Code Generation**: Built-in code samples in 20+ languages
4. **Dark Mode**: Native dark mode support
5. **Search**: Full-text search across endpoints
6. **Keyboard Navigation**: Power-user friendly

### Feature Comparison

| Feature | Scalar | Swagger UI | ReDoc | RapiDoc |
|---------|--------|------------|-------|---------|
| Modern UI | ✅ Excellent | ⚠️ Dated | ✅ Good | ✅ Good |
| Try It Out | ✅ Yes | ✅ Yes | ❌ No | ✅ Yes |
| Code Samples | ✅ 20+ langs | ❌ Limited | ❌ No | ✅ Yes |
| Dark Mode | ✅ Native | ❌ No | ✅ Yes | ✅ Yes |
| Search | ✅ Full-text | ⚠️ Basic | ✅ Yes | ✅ Yes |
| .NET Integration | ✅ Native | ✅ Native | ⚠️ Manual | ⚠️ Manual |
| Active Development | ✅ Very | ⚠️ Slow | ✅ Yes | ⚠️ Moderate |

### Screenshots (Conceptual)

```
┌──────────────────────────────────────────────────────────────┐
│  AzureBank API                                    🔍 Search  │
├──────────────────────────────────────────────────────────────┤
│                                                              │
│  📁 Authentication                                           │
│     POST /api/auth/login                                     │
│     POST /api/auth/register                                  │
│     GET  /api/auth/me                                        │
│                                                              │
│  📁 Accounts                                                 │
│     GET  /api/accounts                                       │
│     POST /api/accounts                                       │
│     GET  /api/accounts/{id}                                  │
│                                                              │
│  📁 Transactions                                             │
│     POST /api/transactions/deposit                           │
│     POST /api/transactions/withdraw                          │
│                                                              │
└──────────────────────────────────────────────────────────────┘
```

## Consequences

### Positive

- Excellent developer experience
- Modern, professional appearance
- Built-in code generation saves integration time
- Active development ensures bug fixes and features
- Native .NET integration via NuGet

### Negative

- Less industry familiarity than Swagger UI
- Newer tool with smaller community
- Some advanced customization requires configuration

### Neutral

- Requires `Microsoft.AspNetCore.OpenApi` for schema generation
- Only enabled in Development environment

## Implementation

### Configuration

```csharp
// ServiceCollectionExtensions.cs
public static IServiceCollection AddOpenApiServices(this IServiceCollection services)
{
    services.AddOpenApi("v1", options =>
    {
        options.AddDocumentTransformer<BearerSecuritySchemeTransformer>();
        options.AddDocumentTransformer<ValidationResponseTransformer>();
        // ... other transformers
    });

    return services;
}
```

### Custom Transformers

The API uses 11 OpenAPI document transformers for:
- Bearer authentication scheme
- Validation error responses
- Error response schemas
- Operation metadata

### Access Control

```csharp
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}
```

Documentation is only available in Development environment for security.

## Validation

Success criteria:
- All endpoints documented
- Try-it-out functionality works
- Authentication flow documented
- Request/response schemas accurate
- Code samples generated correctly

## Related

- [ADR-0001: BFF Pattern](./0001-bff-pattern.md)
- [AzureBank.Api README](../../src/AzureBank.Api/README.md)

---

## References

- [Scalar Documentation](https://github.com/scalar/scalar)
- [Scalar.AspNetCore NuGet](https://www.nuget.org/packages/Scalar.AspNetCore)
- [Microsoft.AspNetCore.OpenApi](https://learn.microsoft.com/aspnet/core/fundamentals/openapi/overview)
