# ADR-0002: YARP Reverse Proxy Selection

**Status**: Accepted

**Date**: 2026-01-12

**Decision Makers**: Architecture Team

---

## Context

The BFF gateway needs to proxy requests from clients to the backend API. We need a reverse proxy solution that:
- Integrates well with ASP.NET Core
- Allows custom request/response transformations
- Supports configuration-based routing
- Provides good performance

## Decision Drivers

- **.NET Integration**: Must work seamlessly with ASP.NET Core
- **Customization**: Ability to inject Bearer tokens into requests
- **Configuration**: Route configuration without code changes
- **Performance**: Minimal latency overhead
- **Maintenance**: Active development and Microsoft support

## Considered Options

1. **YARP (Yet Another Reverse Proxy)**: Microsoft's reverse proxy library
2. **Ocelot**: Popular .NET API gateway
3. **nginx**: External reverse proxy
4. **Custom HttpClient**: Manual proxy implementation

## Decision

Use **YARP (Yet Another Reverse Proxy)** version 2.3.0 for the BFF gateway.

## Rationale

### Comparison Matrix

| Feature | YARP | Ocelot | nginx | Custom |
|---------|------|--------|-------|--------|
| .NET Native | ✅ | ✅ | ❌ | ✅ |
| Transform API | ✅ Excellent | ⚠️ Limited | ❌ | ✅ Manual |
| Config-based | ✅ | ✅ | ✅ | ❌ |
| Performance | ✅ High | ⚠️ Medium | ✅ High | ⚠️ Variable |
| MS Support | ✅ Official | ❌ Community | ❌ External | ❌ None |
| Learning Curve | Low | Low | Medium | N/A |

### Why YARP?

1. **Microsoft-backed**: Official Microsoft project with active development
2. **Transform Providers**: Clean API for injecting authentication headers
3. **ASP.NET Core Native**: Uses Kestrel, same pipeline as API
4. **Configuration-based**: Routes defined in appsettings.json
5. **Extensible**: Easy to add custom middleware and transforms

### Transform Example

```csharp
public class BearerTokenTransformProvider : ITransformProvider
{
    public void Apply(TransformBuilderContext context)
    {
        context.AddRequestTransform(async transformContext =>
        {
            if (sessionService.TryGetToken(sessionId, out var token))
            {
                transformContext.ProxyRequest.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", token);
            }
        });
    }
}
```

## Consequences

### Positive

- Clean, declarative route configuration
- First-class support for request/response transforms
- Excellent performance (built on Kestrel)
- Active Microsoft maintenance and updates
- Good documentation and community

### Negative

- Relatively new (less mature than nginx)
- Some advanced features still evolving
- Smaller community than Ocelot

### Neutral

- Routes configured in appsettings.json
- Additional NuGet package dependency

## Validation

Success criteria:
- All API routes successfully proxied
- Bearer token injection working for all authenticated requests
- Latency overhead < 5ms per request
- No memory leaks under sustained load

## Related

- [ADR-0001: BFF Pattern](./0001-bff-pattern.md)
- [YARP Documentation](https://microsoft.github.io/reverse-proxy/)
- [BFF Implementation](../../src/AzureBank.Bff/README.md)

---

## Configuration Example

```json
{
  "ReverseProxy": {
    "Routes": {
      "api-route": {
        "ClusterId": "backend-api",
        "Match": {
          "Path": "/api/{**catch-all}"
        }
      }
    },
    "Clusters": {
      "backend-api": {
        "Destinations": {
          "api": {
            "Address": "https://localhost:7215"
          }
        }
      }
    }
  }
}
```
