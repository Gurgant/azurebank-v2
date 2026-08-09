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

            /*
              DROP whatever the caller sent, ALWAYS, before deciding what to inject.

              YARP copies request headers to the outbound request by default, so an inbound
              `Authorization` is already sitting on ProxyRequest when this runs. Setting the header
              inside the branch below overwrites it — but only when a session resolves. With NO
              session there was nothing to overwrite, and the client's own token rode through to an
              API that validated it happily.

              That was a live bypass, measured end to end rather than reasoned about: the proxied
              POST /api/auth/login returns the raw JWT, and re-presenting it as
              `Authorization: Bearer …` with no cookie returned 200 and the unmasked account number
              from GET /api/accounts/{id}/full-number, with no PIN ever entered — while the same
              request WITHOUT the header returned 401. POST /api/transfers reached the endpoint the
              same way.

              Clearing unconditionally is what makes the session the ONLY route to an authenticated
              call, on every proxied path rather than only the PIN-gated ones. The step-up gate in
              AuthLevelMiddleware also fails closed now, but it covers two paths; this covers all of
              them, and neither is load-bearing alone.
            */
            transformContext.ProxyRequest.Headers.Authorization = null;

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
