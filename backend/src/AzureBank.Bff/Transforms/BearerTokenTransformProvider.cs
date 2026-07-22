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
            var httpContext = transformContext.HttpContext;
            var cookieName = httpContext.RequestServices
                .GetRequiredService<IOptions<BffSessionOptions>>().Value.CookieName;

            if (httpContext.Request.Cookies.TryGetValue(cookieName, out var sessionId)
                && !string.IsNullOrEmpty(sessionId))
            {
                // Silently re-mint the access token if it is within the refresh skew window, so
                // the 15-minute JWT no longer hard-kills an active session (ADR-0021, PR-2). A
                // null result (session gone / refresh token dead) means we inject NO Authorization
                // header — the API then 401s and the SPA's existing session-expired path fires.
                var refresher = httpContext.RequestServices.GetRequiredService<ITokenRefresher>();
                var token = await refresher.GetFreshAccessTokenAsync(sessionId, httpContext.RequestAborted);
                if (!string.IsNullOrEmpty(token))
                {
                    transformContext.ProxyRequest.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                }
            }
        });
    }
}
