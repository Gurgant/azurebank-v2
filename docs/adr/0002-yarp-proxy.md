# ADR-0002: YARP Reverse Proxy Selection

**Status:** Accepted · **Date:** 2026-01-12 · **Decision Makers:** Vladislav Aleshaev

## Context

The BFF gateway (ADR-0001) proxies requests from the browser to the backend API. The proxy has to
sit in the ASP.NET Core pipeline, let the BFF's own code transform a request so that a Bearer
token can be injected, take its routes from configuration with no code change, add minimal
latency, and be actively maintained.

## Decision

1. **The BFF proxies with [YARP](https://microsoft.github.io/reverse-proxy/) (Yet Another Reverse
   Proxy), version 2.3.0**, because it is an official Microsoft project in active development,
   runs on Kestrel inside the BFF's own ASP.NET Core pipeline, and is extended with custom
   middleware and transforms.
2. **The Bearer token is injected by a transform provider**, `BearerTokenTransformProvider`, an
   `ITransformProvider` that looks up the token held for the session and sets the `Authorization`
   header of the proxied request, because YARP's transform API is a clean place for it.
3. **Routes and clusters are configuration**: the `ReverseProxy` section of the BFF's
   `appsettings.json` maps `/api/{**catch-all}` to the cluster `backend-api`, so a route changes
   with no code change. The BFF's [README](../../backend/src/AzureBank.Bff/README.md) has the rest.

## Rejected

- Rejected: Ocelot, because its transform API is limited, its performance is medium and it is
  maintained by its community, with no Microsoft support.
- Rejected: nginx, because it is an external proxy and not .NET: it has no transform API the BFF's
  code can use, and a steeper learning curve.
- Rejected: a hand-written proxy over `HttpClient`, because its routes are not configuration, its
  transforms are written by hand, its performance varies and nobody supports it.

## Consequences

- Route configuration is declarative, and request and response transforms are first-class.
- YARP runs in the BFF's pipeline, on Kestrel; the overhead it adds to a request is not measured.
- It costs one more NuGet package (`Yarp.ReverseProxy`), in a library that is younger than nginx,
  has some advanced features still evolving and a smaller community than Ocelot's.
- Not covered: the two success criteria no test measures, a latency overhead under 5 ms per
  request and no memory leak under sustained load.

## Verified by

- `BrowserCookieStaysInTheBffTests`: with a live session the API gets the Bearer and no cookie
  (`WithALiveSession_TheApiGetsTheBearer_AndNoCookieHeader`).
- `AuthLevelMiddlewareTests`: a client's own Bearer with no session is not proxied.

## Related

ADR-0001.
