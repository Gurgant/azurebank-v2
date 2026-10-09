# AzureBank.Bff

The gateway between the browser and the API (Backend-For-Frontend, ASP.NET Core on .NET 10). It
signs a user in at the API, keeps the session's tokens in its own memory, gives the browser an
HTTP-only session cookie and proxies `/api` to the API with YARP, adding the session's token. It
also limits request rates, sets the security headers and, where a build is configured, serves
the SPA. It has no database and references `AzureBank.Shared` only.

Why a BFF: [ADR-0001](../../../docs/adr/0001-bff-pattern.md). Why YARP:
[ADR-0002](../../../docs/adr/0002-yarp-proxy.md). One request followed end to end:
[How AzureBank works](../../../docs/architecture/overview.md). The other projects:
[AzureBank Backend](../../README.md).

## Running Locally

Secrets and database first, once:
[Local setup](../../../docs/engineering-practices.md#local-setup). Then, from the repository
root, the API first and each in its own terminal:

```bash
dotnet run --project backend/src/AzureBank.Api --launch-profile https   # https://localhost:7215
dotnet run --project backend/src/AzureBank.Bff --launch-profile http    # http://localhost:5000

curl http://localhost:5000/bff/auth/session-status   # "isAuthenticated":false with no cookie
```

- The host refuses to start without `ServiceCredential:BffKey`: 32 characters or more, the same
  value the API holds, in user-secrets in development (ADR-0055 D3).
- The API must run its `https` profile. The proxy's cluster points at `https://localhost:7215`:
  with the `http` profile the BFF starts and every proxied call fails.
- `appsettings.Development.json` is git-ignored. Copy `appsettings.Development.json.example` to
  that name once: it makes the proxy accept any certificate on the hop to the API, in
  Development only, and holds the Development timeouts (session 10 and 20 minutes, PIN 10).
- `/health/ready` answers 200 while the API is unreachable, on purpose: its body then says
  `Degraded` (`Health/BackendApiHealthCheck.cs`). Wait for the word `Healthy`, not for the
  status. `/health/live` says only that the process is up.

## Tests

```bash
dotnet test backend/AzureBank.slnx
```

The BFF's suite is `backend/tests/AzureBank.Bff.Tests`. Name the solution: a `--filter` on
`AzureBank.Tests` leaves every BFF test out and still reports success
([engineering traps](../../../docs/engineering-traps.md)). Stop the API and the BFF first: on
Windows the build cannot replace an executable that is running.

## What is where

| Path | What it holds |
|------|---------------|
| `Program.cs` | The settings validated at start, the rate limiters, the middleware pipeline in order |
| `Controllers/BffAuthController.cs` | The BFF's own endpoints, `/bff/auth/*` |
| `Middleware/` | Correlation id, security headers, Fetch-Metadata, session activity, the demo's two answers, the gate on `/api` |
| `Transforms/BearerTokenTransformProvider.cs` | What a proxied request carries to the API, and the 503s |
| `Services/` | The session store, token renewal, grant revocation, the session-stamp watcher, cleanup |
| `Extensions/SpaHostingExtensions.cs` | Serving the built SPA |
| `ClientAddress.cs`, `StrictForwardedFor.cs` | The address a client is counted by |
| `Options/` | The settings classes and their startup validators |
| `Http/`, `Health/`, `Observability/` | The service key and the timeout on the road to the API, the readiness probe, the request log |

## BFF Endpoints

### Under `/bff/auth`

The BFF's own endpoints. No answer carries a token.

| Endpoint | What it does |
|----------|--------------|
| `POST /login` | Signs in at the API, opens a session, sets the cookie |
| `POST /register` | Registers, opens a session, sets the cookie. On the public demo (`Demo:Enabled`): 403 `REGISTRATION_CLOSED`, whatever the body, and the API is not called |
| `POST /demo/claim` | On the public demo only: claims a private demo copy and opens a session on it. Body `{}` as JSON. Answers `{ data: { user, expiresAt, copy }, message }`, `no-store`; `copy` holds the email, the password, the PIN, the two contacts' handles and the copy's end. 404 with no body while `Demo:Enabled` is false (ADR-0063) |
| `POST /reauthenticate` | Takes the password, before the absolute limit, and opens a new session at level 1 with no PIN elevation carried over. After the limit it answers 401 (ADR-0026) |
| `POST /logout` | Ends this session only and clears the cookie |
| `GET /me` | The user, read through to the API with the session's copy as fallback (ADR-0039), and the session's details. Counts as activity |
| `GET /session-status` | Whether a session is live, its level and its two deadlines. Does not count as activity (ADR-0018, decision 6) |
| `POST /set-pin` | Sets or changes the PIN |
| `POST /verify-pin` | Verifies the PIN and raises the session to level 2 |
| `PATCH /azuretag` | Renames the public handle and writes it back to the session |

`/reauthenticate`, `/me`, `/set-pin`, `/verify-pin` and `/azuretag` answer 401 without a live
session. The demo's two answers come after the rate limiter and before the body is read
(ADR-0063, decision 9). Another method on the claim's or the registration's path is 405 with
`Allow: POST`, whatever the flag.

### Proxied Routes

The bare `/api` path and everything under it go to the API. The `ReverseProxy` section of
`appsettings.json` holds one cluster, `backend-api`, and four routes: `/api/auth/login` and
`/api/auth/register` (rate-limit policy `auth`), `/api/users/{**catch-all}` (policy `lookup`)
and `/api/{**catch-all}`. `AuthLevelMiddleware` decides first, in this order, and forwards
nothing it refuses (`AuthLevelMiddlewareTests`):

| Request | Answer |
|---------|--------|
| `/api/auth/login`, `/api/auth/register`, `/api/auth/refresh`, `/api/auth/revoke`, `/api/auth/logout`, `/api/auth/session-stamps`, `/api/auth/demo/claim` | `404` with no body, with a live session or with none. The browser uses `/bff/auth/*`, and the API answers these seven to the BFF's own client only (ADR-0041, decision 5; ADR-0057 §4.2; ADR-0063, decision 12) |
| Any other request for `/api` or a path under it, any method, with no live session | `401` with the API's own `AUTH_TOKEN_MISSING` body and no `X-Auth-Level-*` header (ADR-0038, decision 3; ADR-0041, decision 4) |
| `/api/accounts/{id}/full-number`, any method, with a session at level 1 | `403 STEP_UP_REQUIRED`, `X-Auth-Level-Required: 2`, `X-Auth-Level-Current: 1` |
| Everything else | Proxied with the session's token |

- There is no exception list: a new API route needs a session without anybody adding it.
- A cookie the store cannot resolve is a 401, never a 403. No session sends the SPA to sign-in;
  level 1 opens the PIN prompt.
- The 401 is the API's own body on purpose: a caller probing for the gate cannot tell which of
  the two hosts answered.
- `/full-number` is the only route behind the level-2 gate. Level 2 starts at
  `POST /bff/auth/verify-pin` and lasts `Security:PinValidityMinutes`: 5, and 10 in the
  Development example. A transfer and a withdrawal are not gated here: the API takes the PIN at
  an authorisation mint, and the request carries the one-shot authorisation in the
  `Step-Up-Authorization` header (ADR-0041, ADR-0042, ADR-0056).
- Keep the routes `api-auth-login-route` and `api-auth-register-route`, although the middleware
  answers their paths 404. They carry the `auth` rate-limit policy and the limiter runs before
  the middleware: without them the two paths fall to `/api/{**catch-all}`, which has no policy.

### Bearer Token Transform

`BearerTokenTransformProvider` runs on every proxied route:

- It takes off the outbound request whatever the caller sent as `Authorization`, as the service
  key or as the token-road marker, and the browser's whole `Cookie` header, whether or not a
  session resolves. The session is the only credential, the API reads no cookie, and the session
  id is the BFF's own secret (ADR-0038; ADR-0055 D4; ADR-0057 §4.2).
- It adds the BFF's service key and the session's access token, renewed first when it runs short
  (ADR-0057 §4.5). A session that has ended gets no token, and the API answers 401.
- It answers 503 `SERVICE_UNAVAILABLE` with `Retry-After` and `no-store`, and keeps the session,
  where a 401 would sign the user out of the SPA: when a renewal cannot be had and the held token
  has 5 s or less left, and when the API refuses the service key (ADR-0057 §4.5, §4.7).
- It answers the same 503, with `Retry-After: 10`, in place of YARP's empty 504 or 502 when the
  API does not answer within `BackendApi:TimeoutSeconds`, cannot be reached, or the connection
  breaks while a proxied body is sent. The BFF never sends a request again (ADR-0058 D11).
- It forwards nothing to a destination that is neither https nor loopback: YARP answers 502
  (ADR-0055 D5).

## Session Management

### Storage

Sessions live in this process's memory (`InMemoryTokenStore`, behind `ITokenStoreService`). A
restart signs everyone out, and two instances share no session: the deployment runs at most one
replica (ADR-0057 §5.2), and a second one needs a shared store first. A session holds the access
token, the grant that renews it (a refresh token that never rotates and never reaches the
browser), the user's details, the auth level and the deadlines (`Models/UserSession.cs`).

### Session Security

| Cookie | Value |
|--------|-------|
| Name | `__Host-AzureBank.Session`; `.AzureBank.Session` in Development. The prefix is put on `Session:CookieName` at runtime (ADR-0018, decision 2) |
| Attributes | `HttpOnly`, `SameSite=Strict`, `Path=/`, and `Secure` everywhere but Development, whose loop runs on `http://localhost` |
| Lifetime | A session cookie, with no `Expires` and no `Max-Age`: the server enforces the two limits below |
| Session id | 32 bytes from the cryptographic generator |

### Session Lifecycle

1. **Opening.** Sign-in, registration, a demo claim and re-authentication each open a session at
   level 1. One opened over an older cookie ends the older session.
2. **Activity.** Every request that carries the cookie counts, except
   `GET /bff/auth/session-status`.
3. **Limits.** A session ends after `Session:InactivityTimeoutMinutes` without activity (15; 10
   in the Development example) and at the latest `Session:AbsoluteTimeoutMinutes` after it
   opened (60; 20), never later than its grant's own expiry (ADR-0057 §4.1).
   `SessionCleanupService` removes expired sessions every 5 minutes.
4. **Renewal.** The access token is renewed from the grant while the session lives; the browser
   sees neither (ADR-0057 §4.5).
5. **Sign-out** ends this session and clears the cookie; the user's other sessions are
   untouched. Every ending (sign-out, expiry, re-authentication, a new sign-in over an old
   cookie, a raised session stamp, a graceful stop) queues the session's grant on
   `GrantRevoker`, which revokes it with `POST /api/auth/revoke` (ADR-0057 §4.6).
6. **Sign-out everywhere.** Each session keeps the user's session stamp from sign-in. When a
   user is signed out of every session at the API (`POST /api/auth/logout`, or the SQL of the
   [runbook](../../../docs/runbooks/refresh-token-reuse-recorded.md)), the stamp goes up.
   `SessionStampWatcher` reads the stamps of the signed-in users every 15 s through
   `POST /api/auth/session-stamps` (no call when nobody is signed in), and a session whose stamp
   is below the one read is refused at its next request. A failed read keeps the last values
   (ADR-0057 §5.3).

## Middleware Pipeline

The order in `Program.cs` matters: forwarded headers (only with a proxy listed), correlation
id, request log, security headers, Fetch-Metadata, the built SPA, session activity, rate
limiter, demo mode, the gate on `/api`, controllers, proxy, health probes.

- The security headers come before the SPA, so every page carries the policy.
- Fetch-Metadata comes before session activity and the limiter: a cross-site request that
  changes state is answered 403 before it can keep a session alive or spend an allowance
  (ADR-0018, decision 4).
- The limiter comes before demo mode and the gate, so a refused request still spends its
  allowance.

### Security Headers Middleware

On every response:

| Header | Value |
|--------|-------|
| `X-Content-Type-Options` | `nosniff` |
| `X-Frame-Options` | `DENY` |
| `X-XSS-Protection` | `0`, as OWASP recommends: the old filter could create XSS (ADR-0054 D6) |
| `Referrer-Policy` | `strict-origin-when-cross-origin` |
| `Permissions-Policy` | `accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()` |
| `Content-Security-Policy` | The policy of [ADR-0054](../../../docs/adr/0054-the-bff-serves-the-built-spa-under-a-csp-measured-against-it.md) D3, measured against the served SPA: no `'unsafe-inline'`, no `'unsafe-eval'`, and one hash in `style-src`, the empty string's, for Griffel's empty `<style>` elements |
| `Strict-Transport-Security` | `max-age=31536000`, in every environment but Development. Sent over plain http too: behind an edge that terminates TLS every request arrives as http, and `UseHsts` would send nothing (ADR-0054, Consequences) |

The BFF registers no CORS, by design (ADR-0018, decision 1). The browser reaches it same-origin
only: in development Vite's `server.proxy` forwards `/api` and `/bff`, and in production the BFF
serves the SPA itself.

### The built SPA (`Spa:RootPath`)

When `Spa:RootPath` names a Vite build (`frontend/dist`), the BFF serves it: fingerprinted files
under `/assets` as `immutable`, everything else revalidated, and the page shell for any GET
navigation no endpoint claimed. Never under `/api`, `/bff` or `/health`, whatever run of slashes
and backslashes the path begins with (`//api/accounts`, `/%5Capi/accounts`), and never for a file
name, so an unknown API route stays a 404 and a POST-only route stays a 405. An encoded slash is
not a separator: `/%2Fapi/accounts` still gets the shell. Unset, the BFF serves no pages and Vite
does, as in the dev loop; set to a directory with no `index.html`, the host refuses to start
(ADR-0054 D1, D2). The container image sets it (`Dockerfile`).

On the public demo (`Demo:Enabled`) the page carries one tag,
`<meta name="azurebank-demo" content="true">`, put right before its `</head>` when the host
starts: for a navigation, and for `index.html` asked for by its name. A build whose `index.html`
has no `</head>` stops the host. With the demo off the page is the file, byte for byte. In the
dev loop the BFF serves no page: Vite adds the tag itself, and only when it is started with
`AZUREBANK_DEMO=true` (ADR-0063, decision 13).

## Rate Limiting

Three limiters, set by the `RateLimiting` section; no limit is hard-coded.

| Limiter | Applies to | Permits | Window | Counted by |
|---------|------------|---------|--------|------------|
| Global | Every request except `/health/live` and `/health/ready` | 300 | 60 s, fixed | Client address |
| `auth` | `/bff/auth/login`, `/bff/auth/register`, `/bff/auth/demo/claim`, `/bff/auth/reauthenticate`, `PATCH /bff/auth/azuretag`, and the proxied `/api/auth/login` and `/api/auth/register` routes | 10 | 60 s, sliding, 6 segments | Client address |
| `lookup` | `/api/users/{**catch-all}` | 20 | 60 s, sliding, 6 segments | Signed-in user; the client address with no session (ADR-0014, decision 2) |

- The client address is `ClientAddress.Of`: an IPv4 address as it is, an IPv6 address by its /64
  prefix, and `unknown` for a connection with no address.
- A refusal is **429** with `Retry-After`, `Cache-Control: no-store` and the API's ProblemDetails
  shape (`errorCode: RATE_LIMIT_EXCEEDED`). Nothing queues. The sliding policies name no retry
  time of their own, so `RateLimiting:AuthWindowSeconds` is sent.
- `auth` counts a request before the demo's middleware looks at it: a registration the demo
  refuses with 403 spends one of the ten, and so does a claim on a deployment with the demo off,
  which is answered 404 and, past the limit, 429 (`DemoClaimTests`).
- The demo's claim tells the API the same address the limiters count by, so the copies a client
  may claim in a day (`Demo:Claim:MaxPerClientPerDay`, 10, counted by the API) are counted for
  the same client.

### Behind a proxy

The address is the connection's. Behind a proxy it is the proxy's, for every caller: the two
limits that count by address, and the demo's cap of copies a day, are then one budget for
everybody, unless the BFF is told which proxy to believe (ADR-0013, decisions 6 to 8). Both lists
are empty as shipped; the Azure template writes networks only when a run names them
([`infra/README.md`](../../../infra/README.md), "Turn the demo on").

| Setting | Holds | From the environment |
|---------|-------|----------------------|
| `ForwardedHeaders:KnownProxies` | The exact addresses of the proxies to believe | `ForwardedHeaders__KnownProxies__0`, `__1`, ... |
| `ForwardedHeaders:KnownIPNetworks` | Their networks, in CIDR form: `192.0.2.0/24`, `2001:db8:7::/48`. For a platform that may move its proxy inside a range it owns: an exact address stops matching the day it moves, in silence | `ForwardedHeaders__KnownIPNetworks__0`, `__1`, ... |
| `ForwardedHeaders:ForwardLimit` | How many proxies in a row are believed: 1 | `ForwardedHeaders__ForwardLimit` |

With both lists empty `X-Forwarded-For` is not read. With an entry in either, it is read on a
connection that comes from a listed address or from inside a listed network, and on no other:
not from this machine itself, and not on a connection that has no address (a Unix socket, a
named pipe), whose caller stays the one key `unknown`. The caller is then the last entry of the
header, the one the proxy appended; a last entry that is no address leaves the proxy's address
in place. `StrictForwardedFor` first makes the header a list of addresses and nothing else: an
entry that holds anything but `0-9 a-f A-F . : [ ]` becomes the word `unknown`. Read by the
framework alone, an entry with a zone and a quotation mark lets a caller behind the proxy name
an address of its choosing, and a new budget with every request.

What to get right:

- **`ForwardLimit` is never more than the proxies there really are.** With 2 and one proxy, a
  caller whose own address is inside a listed network is taken for the second proxy, and the
  entry it wrote is believed (`TrustedProxyNetworkTests`).
- **A listed network holds what the proxy can connect from, and nothing a caller can connect
  from.** Every address inside it can name a caller's address.
- **The framework's own switch stays unset**, under each of its three names:
  `ASPNETCORE_FORWARDEDHEADERS_ENABLED`, `DOTNET_FORWARDEDHEADERS_ENABLED` and
  `ForwardedHeaders_Enabled`. With it on and nothing listed, the host believes whatever a caller
  writes. With it on and a network listed, the framework's own middleware reads the header
  before `StrictForwardedFor` rewrites it, and a caller behind the proxy is believed again. The
  host starts with the switch on: nothing refuses it but the deployment's `--check`
  (`infra/deploy.py`).
- **A bad entry stops the host at startup.** `ProxyOptionsValidator` refuses, in a sentence that
  names the entry, a network that is not `address/prefix-length`; whose prefix length is outside
  its family's range; that is `/0`, or wider than a `/8`; that is an IPv4-mapped IPv6 network;
  or that .NET reads as another network than it shows (`10.0.0.1/8` is read as `10.0.0.0/8`,
  and a first octet written `010` is read in octal, as 8). One address is written with `/32` or
  `/128`, or goes in `KnownProxies`.
- **A public range is not refused.** Whether a range is the proxy's is measured on the
  deployment, not read from the range.

On the proxied road the API reads no forwarded header. The proxy writes `X-Forwarded-For`,
`X-Forwarded-Host` and `X-Forwarded-Proto` itself, the first with the address the BFF has for
the caller in place of whatever a browser sent under that name. A browser's own `X-Real-IP`
goes on as it was sent, and with a proxy listed so does the framework's `X-Original-For`, the
proxy's own address: no host reads either. The API learns a visitor's address from one place
only: the body of the demo's claim, which the BFF writes from the same key. Not measured: what
the proxy does with a browser's own `X-Forwarded-Host` or `X-Forwarded-Proto`.
`TrustedProxyNetworkTests` and `ForwardedForOnARealConnectionTests` hold this section, on the
real pipeline and on a loopback socket.

## Configuration

`appsettings.json` holds the defaults. `Session`, `RateLimiting`, `ForwardedHeaders`,
`ServiceCredential`, `BackendApi`, `Spa` and `Demo` are validated when the host starts: a value
the host cannot run with stops it with a message that names the key, and the process exits with
a failing code.

| Key | Default | What it does |
|-----|---------|--------------|
| `ServiceCredential:BffKey` | None: required | The key the API knows this host by, sent on every call to it (ADR-0055) |
| `BackendApi:BaseUrl` | `https://localhost:7215` | The API, for the BFF's own client. It and every proxy destination must be https, or http on loopback, or the host refuses to start (ADR-0055 D5) |
| `BackendApi:TimeoutSeconds` | 55 | How long the BFF waits on the API: its own client and, through `BackendTimeoutConfigFilter`, every proxy cluster that sets no timeout of its own. Above the API's 40 s request deadline plus what the API may still need after it, so any answer the API gives before a commit arrives first (ADR-0058, `TimeoutChainTests`) |
| `ReverseProxy` | Four routes, one cluster at `https://localhost:7215` | YARP's routes and clusters; a route changes with no code change |
| `Session:CookieName` | `.AzureBank.Session` | See [Session Security](#session-security) |
| `Session:InactivityTimeoutMinutes`, `Session:AbsoluteTimeoutMinutes` | 15, 60 | See [Session Lifecycle](#session-lifecycle) |
| `Security:PinValidityMinutes` | 5 | How long level 2 lasts after a verified PIN |
| `RateLimiting:GlobalPermitLimit`, `GlobalWindowSeconds`, `AuthPermitLimit`, `AuthWindowSeconds`, `AuthSegmentsPerWindow`, `LookupPermitLimit`, `LookupWindowSeconds` | 300, 60, 10, 60, 6, 20, 60 | The three limiters of [Rate Limiting](#rate-limiting); both sliding windows use `AuthSegmentsPerWindow` |
| `ForwardedHeaders:*` | Empty lists, limit 1 | See [Behind a proxy](#behind-a-proxy) |
| `Spa:RootPath` | Unset | The built SPA to serve |
| `Demo:Enabled` | `false` | The public demo: the claim is open and registration is closed (ADR-0063) |

## See Also

- [AzureBank.Api](../AzureBank.Api/README.md): the API behind this gateway
- [Decision records](../../../docs/adr/README.md): every ADR cited above, by number
- [`infra/README.md`](../../../infra/README.md): the deployment, and how the proxy's networks are
  measured there
