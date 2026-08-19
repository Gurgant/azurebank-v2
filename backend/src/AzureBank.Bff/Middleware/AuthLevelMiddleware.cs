using AzureBank.Shared.Constants;
using System.Diagnostics;
using AzureBank.Bff.Options;
using AzureBank.Bff.Services.Interfaces;
using AzureBank.Shared.Utilities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AzureBank.Bff.Middleware;

/// <summary>
/// Middleware that checks AuthLevel for routes requiring step-up authentication.
/// Returns 403 with X-Auth-Level-Required header if PIN verification is needed.
/// </summary>
public class AuthLevelMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AuthLevelMiddleware> _logger;

    /*
      Two questions, deliberately separated — they used to be one, and collapsing them is what made
      this edit dangerous.

      "Is there a caller at all?" (level >= 1) and "has that caller just proved it is them?"
      (level 2) are different questions with different answers and different remedies: the first
      routes to login, the second opens the PIN modal.

      Before ADR-0041 both lived inside the level-2 gate, so a path could only be refused locally
      for having no session if it ALSO required a PIN. Moving transfers off the PIN gate would then
      have silently taken their no-session refusal with it — not a live bypass, because
      BearerTokenTransformProvider clears the caller's own Authorization on every proxied path and
      the API would 401 — but it would have moved the decision to another process and quietly thinned
      a layer while the PR claimed to be strengthening one.
    */

    /// <summary>
    /// The proxied auth entry points that are answered as if they did not exist. Not an allowlist
    /// and not gated on anything: no caller in this deployment has a reason to reach them.
    /// </summary>
    /*
      THE YARP ROUTES FOR THESE TWO ARE DELIBERATELY KEPT in appsettings.json, and deleting them
      would be the tempting mistake. They carry RateLimiterPolicy "auth"; the route table also holds
      a catch-all "/api/{**catch-all}" with NO policy at all. Remove the dedicated pair and the path
      falls to that catch-all — so if this middleware were ever weakened, login would be proxied
      UNRATE-LIMITED, which is worse than the state this change replaces. Keeping them costs nothing
      while this branch stands in front, and keeps the fallback no worse than today.
    */
    private static readonly HashSet<string> BlockedProxiedAuthPaths =
        new(StringComparer.OrdinalIgnoreCase) { "/api/auth/login", "/api/auth/register" };

    /*
      THERE IS NO LONGER AN EXCEPTION LIST, and the set that used to live here is deleted rather than
      emptied — an empty allowlist is an invitation to add one entry back "just for this".

      Its history is worth keeping. It began as an allowlist naming "/api/transfers" and
      "/api/transfers/internal" and nothing else, so every money endpoint added afterwards fell
      straight through: both authorisation mint endpoints (ADR-0042), deposit, withdraw, and the
      PIN-binding endpoint. Nothing caught it, because a list of paths cannot notice a path that was
      never added to it. Inverting it to a deny-by-default gate fixed the failure MODE: forgetting to
      exempt a genuinely public endpoint fails closed and the first call reveals it, while forgetting
      to list a money endpoint failed open and revealed nothing to anyone.

      That left exactly two exemptions — login and register — on the argument that they are how a
      caller obtains the session everything else requires. That argument was wrong about THIS
      deployment: the SPA never calls them. It signs in through the BFF's own /bff/auth/* controller,
      which talks to the API server-side and keeps the token there. The proxied pair had no
      legitimate caller at all, and both are now answered 404 above, so the gate below is
      unconditional for every POST under /api/.
    */

    /*
      Routes gated on the BFF session flag at level 2. EMPTY since ADR-0041 — see above. The set and
      its matching logic stay because /full-number still uses the prefix/suffix rules below: a GET
      with no body, no amount and no payee has nothing to bind an in-band credential to, and PSD2
      does not obviously treat it as an SCA trigger. That last clause is deliberately hedged: an
      earlier version asserted "Art. 97(1)'s list is exhaustive; Art. 4(32) says an account number is
      not sensitive payment data", and both halves were wrong — 4(32)'s exclusion is scoped to
      payment-initiation and account-information PROVIDERS, and 97(1)(c) is a catch-all for any
      remote action that may imply a fraud risk. The reason this path keeps the session model is that
      a GET carries no amount and no payee to bind a code to, not that the regulation is silent.
      See ADR-0041.
    */
    private static readonly HashSet<string> PinRequiredPaths = new(StringComparer.OrdinalIgnoreCase);

    // Path patterns that require PIN
    private static readonly string[] PinRequiredPrefixes =
    {
        "/api/accounts/" // For endpoints like /api/accounts/{id}/full-number
    };

    // Path suffixes that require PIN
    private static readonly string[] PinRequiredSuffixes =
    {
        "/full-number"
    };

    public AuthLevelMiddleware(RequestDelegate next, ILogger<AuthLevelMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext context,
        ISessionService sessionService,
        IOptions<BffSessionOptions> sessionOptions)
    {
        var path = context.Request.Path.Value ?? "";
        // Sanitize ONCE, up front, and use safePath in EVERY log statement below — a branch
        // that logs the raw path would bypass the log-forging defence (route matching still
        // uses the raw path: sanitizing could alter which rules match).
        var safePath = LogSanitizer.Sanitize(path);
        var method = context.Request.Method;
        // Kestrel rejects a request line whose method token holds CR/LF, so this is belt-and-braces
        // rather than a live hole — but the log sink cannot tell where its input came from, and one
        // sanitized value beside one raw value in the same statement is a pattern that invites the
        // wrong copy later. Route matching keeps using the raw `method` for the same reason `path`
        // does: sanitizing could change which rules match.
        var safeMethod = LogSanitizer.Sanitize(method);

        // The browser must NEVER drive refresh-token rotation: only the BFF holds refresh tokens
        // (server-side) and re-mints access tokens itself via the YARP transform (ADR-0021, PR-2).
        // A raw proxied /api/auth/refresh has no legitimate caller here, so short-circuit it to
        // 404 — as if the route did not exist (don't leak why). Trailing slash is normalized the
        // same way the PIN gate is, since endpoint routing tolerates it.
        if (path.TrimEnd('/').Equals("/api/auth/refresh", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "SecurityEvent {SecurityEvent}: blocked raw proxied {Path}", SecurityEvents.RawRefreshBlocked, safePath);
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        /*
          THE SAME TREATMENT FOR THE OTHER TWO PROXIED AUTH ENTRY POINTS, and this one closes a live
          bypass rather than adding depth to an existing refusal.

          MEASURED on the running stack before the change, with no session cookie at all:
            POST http://localhost:5000/bff/auth/register  -> 201   (the legitimate door)
            POST http://localhost:5000/api/auth/login     -> 200, body carries data.token,
                                                             a 392-character API JWT, plus a
                                                             refreshToken
            that token, sent straight to the API:
            GET  https://localhost:7215/api/accounts      -> 200   full account access
            the same token replayed through the BFF:
            GET  http://localhost:5000/api/accounts       -> 401   the transform strips it

          So the BFF was handing out the very credential it exists to withhold. Only the last line
          kept it contained, and that is one unconditional header-clear in
          BearerTokenTransformProvider — a defence that lives on a different path from the one
          issuing the token. /api/auth/register answered 201 to the same sessionless caller.

          The SPA loses nothing: it never calls either path. Sign-in goes through the BFF's own
          /bff/auth/login, which reaches the API with IHttpClientFactory server-side and keeps the
          token there — so this middleware never sees it.

          404 rather than 401, matching the refresh branch above: a caller with no legitimate reason
          to know the route exists is told nothing about it.
        */
        if (BlockedProxiedAuthPaths.Contains(path.TrimEnd('/')))
        {
            _logger.LogWarning(
                "SecurityEvent {SecurityEvent}: blocked raw proxied {Path}",
                SecurityEvents.RawAuthEntryBlocked, safePath);
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Either gate brings the request in here; only the PIN one can answer 403 below.
        var requiresPin = RequiresPinVerification(path, method);
        if (requiresPin || RequiresSession(path, method))
        {
            var cookieName = sessionOptions.Value.CookieName;

            /*
              NO SESSION IS A REFUSAL, not a hand-off.

              This branch used to fall through with "No session cookie - let the API handle 401",
              and that delegation was the bypass: it is only true while the API cannot authenticate
              the caller, and a caller carrying their own `Authorization` header authenticates fine.
              Measured before the fix — GET /api/accounts/{id}/full-number with a bearer and no
              cookie returned 200 and the unmasked number, and POST /api/transfers reached the
              endpoint, with no PIN in either case.

              The header is stripped in BearerTokenTransformProvider now, so the 401 this once hoped
              for would in fact arrive. Refusing here anyway is the point: this gate is the only PIN
              check in the system, and a gate whose correctness depends on a different file getting
              something right is not a gate. Deciding it locally also means the request never leaves
              the BFF.

              401 rather than the 403 below, because these two states are not the same thing and the
              SPA treats them differently: level 1 means "you are signed in, prove it is you", which
              opens the PIN modal; no session means "you are not signed in", which must route to
              login. Answering 403 here would show a PIN prompt to someone with no session at all.
            */
            context.Request.Cookies.TryGetValue(cookieName, out var sessionId);

            // GetAuthLevel returns 0 for a missing, unknown or expired session, so one call
            // separates all three of the states below without asking the store twice.
            var authLevel = string.IsNullOrEmpty(sessionId) ? 0 : sessionService.GetAuthLevel(sessionId);

            if (authLevel == 0)
            {
                _logger.LogWarning(
                    "SecurityEvent {SecurityEvent}: no session on PIN-protected {Method} {Path}",
                    SecurityEvents.StepUpWithoutSession, safeMethod, safePath);

                /*
                  Byte-for-byte the 401 the API emits for a missing token — measured against the
                  running stack rather than copied from a doc:

                    {"type":"https://httpstatuses.com/401","title":"Unauthorized","status":401,
                     "detail":"Authentication is required to access this resource.",
                     "instance":"/api/accounts","errorCode":"AUTH_TOKEN_MISSING","traceId":"…"}

                  Identical on purpose, twice over. The SPA already knows this shape, so nothing
                  downstream needs to learn a new one. And an attacker cannot tell whether the BFF
                  short-circuited or the API answered — so probing does not reveal which paths carry
                  the step-up gate. The same reasoning as the 404 on a raw proxied refresh above:
                  refuse without explaining what you are.
                */
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                var unauthorized = new ProblemDetails
                {
                    Status = StatusCodes.Status401Unauthorized,
                    Title = "Unauthorized",
                    Detail = "Authentication is required to access this resource.",
                    Type = "https://httpstatuses.com/401",
                    Instance = context.Request.Path,
                };
                unauthorized.Extensions["errorCode"] = "AUTH_TOKEN_MISSING";
                unauthorized.Extensions["traceId"] =
                    Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
                await context.Response.WriteAsJsonAsync(unauthorized);
                return;
            }

            if (requiresPin && authLevel < 2)
            {
                _logger.LogWarning(
                    "SecurityEvent {SecurityEvent}: AuthLevel {CurrentLevel} < 2 required for {Method} {Path}",
                    SecurityEvents.StepUpRequired, authLevel, safeMethod, safePath);

                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.Headers.Append("X-Auth-Level-Required", "2");
                context.Response.Headers.Append("X-Auth-Level-Current", authLevel.ToString());

                await context.Response.WriteAsJsonAsync(new
                {
                    type = "STEP_UP_REQUIRED",
                    title = "PIN Verification Required",
                    detail = "This operation requires PIN verification",
                    requiredLevel = 2,
                    currentLevel = authLevel,
                    status = 403
                });

                return;
            }
        }

        await _next(context);
    }

    /// <summary>
    /// Every proxied POST must have a live session before the request leaves the BFF, except the two
    /// paths that exist to create one.
    /// </summary>
    /// <remarks>
    /// The prefix test uses the RAW path so that "/api/" itself cannot slip through a trim, while the
    /// allowlist is matched on the NORMALIZED one — same trailing-slash handling as the PIN gate, and
    /// for the same reason it was needed there (PR #64): endpoint routing tolerates "/api/transfers/",
    /// so raw exact matching would let a slash-suffixed POST past the check. It has to normalise in
    /// both directions now: "/api/auth/login/" reaches login and must stay reachable.
    /// Scoped to /api/ deliberately — the BFF's own controllers live under /bff/ and are mapped after
    /// this middleware, so a wider prefix would lock the front door.
    /// </remarks>
    private static bool RequiresSession(string path, string method)
    {
        if (!method.Equals("POST", StringComparison.OrdinalIgnoreCase)
            || !path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Unconditional since the proxied auth pair is 404'd above: every POST under /api/ needs a
        // session, with no exception list left to fall out of date.
        return true;
    }

    private static bool RequiresPinVerification(string path, string method)
    {
        // Endpoint routing tolerates a trailing slash ("/api/transfers/" and
        // ".../full-number/" still reach their endpoints), so the gate must match the
        // NORMALIZED path — raw exact/suffix matching would let a slash-suffixed request
        // bypass step-up entirely while the API happily serves it.
        var normalizedPath = path.TrimEnd('/');

        // POST to transfer endpoints requires PIN
        if (method.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            if (PinRequiredPaths.Contains(normalizedPath))
            {
                return true;
            }
        }

        // Check path suffixes that require PIN (any method)
        foreach (var suffix in PinRequiredSuffixes)
        {
            if (normalizedPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                // Only if it's under a PIN-required prefix
                foreach (var prefix in PinRequiredPrefixes)
                {
                    if (normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}

public static class AuthLevelMiddlewareExtensions
{
    public static IApplicationBuilder UseAuthLevelEnforcement(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<AuthLevelMiddleware>();
    }
}
