using AzureBank.Bff.Http;
using AzureBank.Bff.Observability;
using AzureBank.Bff.Options;
using AzureBank.Bff.Services.Interfaces;
using AzureBank.Shared.Exceptions;
using AzureBank.Shared.Options;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace AzureBank.Bff.Transforms;

/// <summary>
/// YARP transform provider that adds Bearer token to proxied requests.
/// Reads session cookie, retrieves stored JWT, and adds Authorization header.
///
/// This is the core of the BFF security pattern - JWT tokens are stored
/// server-side and injected into API requests by the BFF, never exposed to browser.
///
/// <para>
/// Two answers never leave here as the API's 401, because the SPA reads a 401 as a sign-out and
/// neither is one (ADR-0057 §4.5, §4.7): a renewal that could not be had while the held token has
/// 5 s or less left, and the API refusing this host's service key. Both become a 503 with
/// <c>Retry-After</c>, and the session is kept.
/// </para>
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
              same way. (Amended 2026-09-23: that was the login of the time, a bare JWT string in
              data.token. Login answers the TokenResponse object now, the JWT at
              data.token.accessToken, and the proxied route itself has answered 404 since ADR-0041.)

              Clearing unconditionally is what makes the session the ONLY route to an authenticated
              call, on every proxied path rather than only the PIN-gated ones. The step-up gate in
              AuthLevelMiddleware also fails closed now, but it covers two paths; this covers all of
              them, and neither is load-bearing alone.
            */
            transformContext.ProxyRequest.Headers.Authorization = null;

            /*
              THE SERVICE CREDENTIAL, THE SAME WAY (ADR-0055): whatever the caller sent under that
              name is dropped, then this host's key is set. YARP would otherwise copy a browser's
              own X-AzureBank-Service-Key to the API beside ours, and the API refuses a request
              that carries two. A browser cannot know the key; it must not be able to break the
              request by guessing at it either.
            */
            transformContext.ProxyRequest.Headers.Remove(ServiceCredentialOptions.HeaderName);

            /*
              AND THE TOKEN-ROAD MARKER, WHICH IS NEVER SET ON THIS ROAD (ADR-0057 §4.2). The API's
              token endpoints answer only a request carrying exactly one, over loopback — and this
              proxy reaches the API over loopback too, with the key on every browser request. The
              marker is the one thing that tells the BFF's own client from a browser's request
              passing through, so a browser's copy goes here, before anything else can see it.
              AuthLevelMiddleware 404s the token paths as well; this holds even if a path slipped
              past that list.
            */
            transformContext.ProxyRequest.Headers.Remove(ServiceCredentialOptions.TokenRoadHeaderName);

            /*
              AND THE BROWSER'S COOKIE HEADER, WHOLE, WHETHER OR NOT A SESSION RESOLVES. YARP's
              header copy puts it on the outbound request with the rest: without this line the
              session id goes to the API beside the bearer it has just been exchanged for, and with
              it every other cookie the browser holds for this origin. The API reads no cookie, and
              the session id is this host's secret: presented here, it is the session.

              The whole header, not the session's cookie picked out by name: none of the others is
              the API's to read either. Here, not in the branch below that sets the bearer: a
              request whose session ended after the gate let it through is still forwarded, with no
              bearer, and its cookie has no more reason to leave than a live one's.

              OFF THE OUTBOUND REQUEST ONLY. The browser's own request keeps its header: the session
              lookup below reads it, as the gate and the rate limiter's partition did on the way
              here. Taken off that request instead, the lookup finds nothing and every proxied call
              goes to the API with no bearer.
            */
            transformContext.ProxyRequest.Headers.Remove(HeaderNames.Cookie);

            /*
              OVER TLS OR TO THIS MACHINE ONLY, AND NOTHING IS FORWARDED OTHERWISE. Startup refuses
              such a destination, but YARP reloads its configuration while the host runs, so the
              rule is asked again per request.

              THIS THROWS RATHER THAN SENDING LESS, and the first version did send less: it withheld
              the credential, logged, and fell through to the session block below. Measured on that
              version with a reload pointing the cluster at http://api.internal:5068 — 50 of 50
              requests reached that destination, every one of them carrying `Bearer fake-jwt`, the
              session's own access token. Withholding one secret while handing over another is not
              a guard. YARP answers 502 when a request transform throws (ForwarderError
              .RequestCreation), which is the truth: it could not forward this.
            */
            if (!ServiceCredentialTransport.IsSafe(
                    Uri.TryCreate(transformContext.DestinationPrefix, UriKind.Absolute, out var destination)
                        ? destination
                        : null))
            {
                httpContext.RequestServices
                    .GetRequiredService<ILogger<BearerTokenTransformProvider>>()
                    .LogError("Not forwarded: the API destination is neither https nor loopback");
                throw new InvalidOperationException(
                    "The API destination is neither https nor loopback, so nothing is forwarded to it.");
            }

            /*
              IOptionsMonitor, not IOptions, so the two roads to the API cannot present DIFFERENT
              keys. IOptions computes its value once for the life of the process; the BFF's own
              client reads the monitor, so after a rotation that client would have sent the new key
              and this transform the old one — and the API refuses the old one. Same source, same
              freshness, on both roads.
            */
            transformContext.ProxyRequest.Headers.TryAddWithoutValidation(
                ServiceCredentialOptions.HeaderName,
                httpContext.RequestServices
                    .GetRequiredService<IOptionsMonitor<ServiceCredentialOptions>>()
                    .CurrentValue.BffKey);

            if (httpContext.Request.Cookies.TryGetValue(cookieName, out var sessionId)
                && !string.IsNullOrEmpty(sessionId))
            {
                // The session's access token, renewed first when it runs short (ADR-0057 §4.5), so
                // the 15-minute JWT no longer hard-kills an active session.
                var refresher = httpContext.RequestServices.GetRequiredService<ITokenRefresher>();
                var result = await refresher.GetAccessTokenAsync(sessionId, httpContext.RequestAborted);
                switch (result.Outcome)
                {
                    case AccessTokenOutcome.Token:
                        transformContext.ProxyRequest.Headers.Authorization =
                            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", result.AccessToken);
                        break;

                    case AccessTokenOutcome.Unavailable:
                        /*
                          ANSWERED HERE, NOT FORWARDED. Writing a response from a request transform is
                          YARP's documented short-circuit: it forwards nothing once the transform has
                          set a status other than 200 (HttpForwarder checks IsResponseSet). The token
                          it would have sent has 5 s or less left, or has expired, and the API would
                          answer it 401 AUTH_TOKEN_EXPIRED — which the SPA reads as a sign-out.
                        */
                        await ServiceUnavailable.WriteAsync(
                            httpContext,
                            "The session could not be renewed just now. Try again shortly.",
                            result.RetryAfterSeconds);
                        return;

                    case AccessTokenOutcome.SessionEnded:
                        // No token: the API answers 401 and the SPA's session-expired path fires.
                        break;
                }
            }
        });

        /*
          THE API REFUSING THIS HOST'S KEY IS NOT THE BROWSER'S 401 (ADR-0057 §4.7, F4). A key
          rotation applied on one side only makes the API refuse every proxied call with 401
          SERVICE_CREDENTIAL_REQUIRED, and until PR-1 YARP passed it through: measured in O0 (item 6),
          the browser got the API's own body with its 401 and the SPA signed the user out. The API
          marks that refusal with a header; this turns exactly that response into a 503, which the
          SPA retries on a read and reports on a write, never signing anyone out, and replaces the
          body so the API's refusal text goes no further.
        */
        context.AddResponseTransform(async responseContext =>
        {
            /*
              NO ANSWER FROM THE API IS THE OUTAGE 503, NOT YARP'S EMPTY 504 OR 502 (ADR-0058). YARP
              calls the response transforms with no response when forwarding failed, having already
              set its own status and nothing else. Three of its errors are the API's absence: it did
              not answer within the cluster's activity timeout (RequestTimedOut, an empty 504, which
              BackendTimeoutConfigFilter sets from BackendApi:TimeoutSeconds), it could not be reached
              at all (Request, an empty 502, as while it restarts), or its connection failed while the
              request's body was on its way to it (RequestBodyDestination, an empty 502, or 504 if the
              timeout fired then). All three now get the body the BFF's own routes give, which the SPA
              reads as "try again", with no "applied": whether a write landed is the API's to say, and
              it said nothing.

              Written with the client's token, which WriteAsync uses. The transform context's token is
              the forwarding's own, cancelled when the timeout fires, and would write nothing. Every
              other error keeps YARP's answer: RequestCreation is this host refusing to forward (the
              unsafe destination above), and RequestCanceled is a client that has hung up.
            */
            if (responseContext.ProxyResponse is null)
            {
                await AnswerTheOutageAsync(responseContext.HttpContext);
                return;
            }

            if (!ServiceKeyRefusal.Is(responseContext.ProxyResponse))
            {
                return;
            }

            var httpContext = responseContext.HttpContext;
            httpContext.RequestServices
                .GetRequiredService<ILogger<BearerTokenTransformProvider>>()
                .LogWarning("The API refused this host's service key on a proxied call; answering 503");

            responseContext.SuppressResponseBody = true;
            httpContext.Response.Headers.Remove(ServiceCredentialOptions.RefusalHeaderName);
            await ServiceUnavailable.WriteAsync(
                httpContext, ServiceUnavailable.OutageDetail, ServiceUnavailable.KeyRefusalRetryAfterSeconds);
        });
    }

    /// <summary>
    /// Answers the outage 503 when forwarding failed because the API timed out or could not be
    /// reached, and leaves every other failure, and a response already started, as YARP left it.
    /// </summary>
    private static Task AnswerTheOutageAsync(HttpContext httpContext)
    {
        var reason = httpContext.GetForwarderErrorFeature()?.Error switch
        {
            ForwarderError.RequestTimedOut => "Timeout",
            ForwarderError.Request => "Unreachable",
            // YARP's status is the only mark of which it was: 504 when the timeout had fired.
            ForwarderError.RequestBodyDestination => httpContext.Response.StatusCode
                == StatusCodes.Status504GatewayTimeout ? "Timeout" : "Unreachable",
            _ => null,
        };
        if (reason is null || httpContext.Response.HasStarted)
        {
            return Task.CompletedTask;
        }

        var timeout = httpContext.Features.Get<IReverseProxyFeature>()?.Cluster.Config.HttpRequest?.ActivityTimeout;
        httpContext.RequestServices
            .GetRequiredService<ILogger<BearerTokenTransformProvider>>()
            .LogWarning(
                "The API gave no answer ({Reason}) on {RoutePattern}, waited on for at most {TimeoutSeconds} s; answering 503",
                reason,
                RequestLogRoute.Of(httpContext),
                (int?)timeout?.TotalSeconds);

        return ServiceUnavailable.WriteAsync(
            httpContext, ServiceUnavailable.OutageDetail, ServiceUnavailableException.OutageRetryAfterSeconds);
    }
}
