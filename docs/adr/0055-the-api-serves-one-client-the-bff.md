# ADR-0055: The API serves one client, the BFF

**Status:** Accepted · **Date:** 2026-09-19 · Completes [ADR-0001](0001-bff-pattern.md): the BFF
keeps the token out of the browser, and that is worth something only while the API will not hand
the same token to anyone who asks it directly.

## Context

ADR-0001 chose a BFF so the browser holds a cookie and never a token. Everything built on it since
— the cookie session, the level-two gate on `/full-number` (ADR-0008), the BFF's rate limits, the
Fetch-Metadata checks (ADR-0018) — lives in the BFF. None of it is in the way of a caller that goes
round the BFF, and nothing stopped one. Measured on 2026-09-19 against the running API on the local
stack, with no BFF in the path and a user created for the probe:

```
POST /api/auth/register                     201
POST /api/auth/login                        200, a bearer token in the body
GET  /api/accounts                          200, number masked (AB-****-****-32)
GET  /api/accounts/{id}/full-number         200, the unmasked number; no PIN ever set or entered
POST /api/auth/pin/verify, wrong PIN x3     200, 200, then 429 PIN_LOCKED, Retry-After: 900
```

So the API's own defences held where it has them: the PIN lock engaged with no BFF, and the three
operations that move money or close an account refuse without a step-up authorisation the API
itself minted (ADR-0042). What did not hold is everything the BFF adds, because the BFF was
optional. The repository said so in passing — `Program.cs` carried the comment "direct API access
is server-to-server or Swagger/dev" — and had no hosting configuration that would have made the
API private.

## Decision

**D1 — The API refuses a request that does not carry the BFF's service credential.**
`ServiceCredentialMiddleware` runs before authentication and compares the
`X-AzureBank-Service-Key` header with `ServiceCredential:BffKey`. Without a match the answer is
`401 application/problem+json` with `errorCode: SERVICE_CREDENTIAL_REQUIRED`, and nothing about the
caller's token, body or path is evaluated.

**D2 — One answer for a missing key and a wrong one, compared in constant time.** Both sides are
hashed with SHA-256 and compared with `CryptographicOperations.FixedTimeEquals`; the digests give
the comparison two inputs of one length. A header sent twice is refused even if one value is
right. The log line for a refusal carries the HTTP method and neither the header's value nor the
path (ADR-0017: a path carries a customer's handle).

**D3 — Both hosts refuse to start without a usable key.** `ValidateOnStart`, 32 characters or
more, the same rule and the same treatment as the other secrets. An API that started without the
key and served every caller would be this hole reopened by a deployment mistake; a BFF that
started without it would run and fail every login with a 401 nobody can read.

**D4 — The BFF sends the key on both of its roads to the API, and no browser can send it
instead.** The named `BackendApi` client carries it as a default header; the YARP transform
removes whatever the caller sent under that name and sets the BFF's own, next to where it does the
same for `Authorization`.

**D5 — The key travels over TLS, or to this machine, and nothing is forwarded anywhere else.** It
is a bearer secret, so `http://api.internal` would put it on the network in clear. The BFF refuses
to start when `BackendApi:BaseUrl` or any YARP destination is neither `https` nor `http` on
loopback — loopback because that is the development and CI topology, and it crosses no network.
YARP reloads its configuration while the host runs, so the proxy asks the same question per
request, and a destination that fails it gets NO request: the transform throws and YARP answers
502. Observed while testing that: a live reload re-runs the options' validation too, and throws the
startup message.

> ⚠️ The first version of that per-request guard withheld the credential, logged, and let the
> request go. Review asked what else was on it, and the measurement answered: with the cluster
> reloaded to `http://api.internal:5068`, 50 of 50 requests reached that destination, every one
> carrying `Bearer fake-jwt` — the session's own access token, which the lines below the guard put
> there. **Withholding one secret while handing over another is not a guard**, and "send less" is
> not a transport boundary. Hence the throw.

The BFF's own client follows no redirect, because .NET drops `Authorization` when a redirect leaves
the authority and keeps every other header; YARP never follows one. It trusts an unverifiable
certificate on loopback alone: that exception exists for ASP.NET's development certificate on
`https://localhost:7215`, and granted to the whole Development environment it would also cover a
remote `https://` destination, which this same decision allows — so whoever answered that address
would be handed the key, certificate or not.

> ⚠️ **Both of those are decided per REQUEST on that road too, and the first version of each was
> decided once.** `IHttpClientFactory` pools the primary handler for its lifetime while the named
> client reads `BackendApi:BaseUrl` live on every `CreateClient`, so the address a handler was
> built for and the address a request goes to are not the same thing. The key was a default header
> on that client, and the certificate exception came from the address read at construction.
> Measured before the fix, with the configuration reloaded to `http://api.internal:5068`: the next
> login reached that destination carrying the key. `ServiceCredentialHandler` attaches the key per
> request and throws when the address is neither `https` nor loopback, and the certificate callback
> reads `request.RequestUri`, accepting an unverifiable certificate only on this machine and only
> in Development. Outside Development no callback is installed, so .NET's own validation stands.

**D6 — What is exempt.** `/health/*`, which an orchestrator calls with no credential and which
says nothing about a customer. In Development only, `/openapi` and `/scalar`, which a developer
opens in a browser; the operations they describe are not exempt, so Scalar's "Try it" needs the
header like any other caller.

**D7 — In production the API also has no public address.** A private network, with the
platform's identity between the two hosts (managed identity on Azure) or mutual TLS from a service
mesh. That is a hosting decision and this repository has no hosting configuration, so it is
written here and not in code. The key stays as the second line behind it.

## Rejected

- **Mutual TLS in the repository.** Stronger, and it costs a certificate authority, two
  certificates to issue and rotate, and Kestrel configured to require them in development, in
  `WebApplicationFactory` and in the three CI jobs that start the stack — for two processes on one
  machine. It belongs to D7, where the platform supplies it.
- **Leaving it to the network alone.** Correct in production and invisible in the repository: a
  reader who runs `curl` against the API gets a token, and the BFF looks like decoration.
- **A PIN check inside the API on `/full-number`.** That read is the one sensitive operation
  whose PIN only the BFF checks, and it was the measurement that started this. Decided NOT to do,
  2026-09-19: D1 closes the road that made it matter, since no caller reaches the API without the
  BFF and the BFF checks the PIN. Doing it anyway means a new step-up operation, a regenerated
  contract and a changed frontend flow, for a read that moves no money. It is worth reopening on
  the day "What would change this" below comes true, and not before.
- **An allow-list of caller addresses.** The BFF and the API share a host in development and CI,
  so the list would be `127.0.0.1`, which is every caller.

## Consequences

- Anything that calls the API directly now presents the key: the integration tests (the factory
  puts it on every client it hands out, and `ServiceCredentialTests` takes it off again), CI's
  Schemathesis run, and the Bruno collection (`collection.bru`).
- A seventh secret, and the first one two hosts share. The BFF gains a user-secrets store for it.
- The key authenticates the BFF, not a user, and it is a bearer secret: whoever reads it off the
  BFF's host can call the API. D7 is what makes that host hard to reach.
- The OpenAPI document does not describe the header. It is not part of any operation's contract;
  it is a condition of reaching the API at all.

## What would change this

A second legitimate client of the API (a mobile app with its own token flow, a partner
integration). One shared key does not distinguish callers; that is the point at which the API
needs per-client credentials and this ADR is superseded.
