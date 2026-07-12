using AzureBank.Bff.Options;
using AzureBank.Bff.Services.Interfaces;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace AzureBank.Bff.Transforms;

/// <summary>
/// YARP transform provider that adds Bearer token to proxied requests.
/// Reads session cookie, retrieves stored JWT, and adds Authorization header.
///
/// This is the core of the BFF security pattern - JWT tokens are stored
/// server-side and injected into API requests by the BFF, never exposed to browser.
/// </summary>
public class BearerTokenTransformProvider : ITransformProvider
{
    public void ValidateRoute(TransformRouteValidationContext context)
    {
        // No route-level validation needed
    }

    public void ValidateCluster(TransformClusterValidationContext context)
    {
        // No cluster-level validation needed
    }

    public void Apply(TransformBuilderContext context)
    {
        context.AddRequestTransform(async transformContext =>
        {
            var sessionService = transformContext.HttpContext.RequestServices
                .GetRequiredService<ISessionService>();
            var sessionOptions = transformContext.HttpContext.RequestServices
                .GetRequiredService<IOptions<BffSessionOptions>>();

            var cookieName = sessionOptions.Value.CookieName;

            if (transformContext.HttpContext.Request.Cookies.TryGetValue(cookieName, out var sessionId))
            {
                if (sessionService.TryGetToken(sessionId, out var token) && token != null)
                {
                    transformContext.ProxyRequest.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                }
            }

            await Task.CompletedTask;
        });
    }
}
