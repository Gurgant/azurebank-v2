# ADR-0055: The API serves one client, the BFF

**Status:** Accepted · **Date:** 2026-09-19 · **Amended:** 2026-09-28 (ADR-0057), 2026-10-04
(ADR-0063) · **Completes:** ADR-0001

## Context

ADR-0001 puts a BFF in front of the API so that the browser holds a cookie and never a token. The
cookie session, the level-two gate on `/full-number` (ADR-0008), the rate limits and the
Fetch-Metadata checks (ADR-0018) live in the BFF, and none stops a caller that goes round it.
Measured before this decision, with no BFF in the path: login answered 200 with a bearer token in
the body, and `GET /api/accounts/{id}/full-number` 200 with the unmasked number, no PIN ever set.
The API's own defences held (the PIN lock, the step-up of ADR-0042); the BFF's were optional.

## Decision

- **D1: the API refuses a request that does not carry the BFF's service credential**, because
  everything the BFF adds is otherwise optional. `ServiceCredentialMiddleware` runs before
  authentication and compares `X-AzureBank-Service-Key` with `ServiceCredential:BffKey`: with no
  match it answers 401 `SERVICE_CREDENTIAL_REQUIRED` and evaluates nothing else.
- **D2: a missing key and a wrong key get one answer, compared in constant time** over SHA-256
  digests with `FixedTimeEquals`, so neither the answer nor its duration says how close a guess
  came. A header sent twice is refused even if one value is right. The refusal's log line carries
  the HTTP method, and neither the key nor the path, which can hold a customer's handle (ADR-0017).
- **D3: both hosts refuse to start without a key of 32 characters or more** (`ValidateOnStart`),
  because an API without it would serve every caller and a BFF without it would fail every login.
- **D4: the BFF sends the key on both of its roads to the API, per request and from one source, and
  no browser can send it instead.** `ServiceCredentialHandler` sets it on each request of the
  `BackendApi` client; the YARP transform replaces whatever the caller sent under that name with the
  BFF's own. Both read `IOptionsMonitor`, so neither road keeps a key the other has stopped using.
  The API hashes its copy once, at start, so a rotation is a deployment event on both sides; half
  applied, the API answers 401, which the BFF turns into a 503 with `Retry-After` (ADR-0057 §4.7).
  On the six token endpoints (login, register, refresh, revoke, logout and the demo's claim,
  ADR-0063) and the session-stamp feed the key is not enough: they answer 404 unless the request
  comes over loopback with the BFF's `X-AzureBank-Token-Road` marker (ADR-0057 §4.2).
- **D5: the key travels over TLS or to this machine, and nothing is forwarded anywhere else**,
  because it is a bearer secret. The BFF refuses to start when `BackendApi:BaseUrl` or a YARP
  destination is neither `https` nor `http` on loopback, and both roads ask again per request,
  because the configuration reloads while the host runs: the transform throws (YARP answers 502) and
  `ServiceCredentialHandler` throws, since withholding the key alone would still send the session's
  access token. The BFF's own client follows no redirect, because .NET would carry the key header
  across one, and accepts an unverifiable certificate only on loopback, in Development.
- **D6: `/health/*` is exempt**, because an orchestrator calls it with no credential and it names no
  customer. In Development only, so are `/openapi` and `/scalar`, not the operations they describe.
- **D7: in production the API also has no public address.** It is a sidecar of the BFF and listens
  on loopback only: in `compose.yaml` it shares the BFF's network namespace and binds
  `127.0.0.1:5068`, and on Azure the two run as one app. The key is the second line behind that.

## Rejected

- Rejected: mutual TLS in the repository, because it costs a certificate authority and two
  certificates in development, the test host and three CI jobs, for two processes on one machine.
- Rejected: the network alone, because it is invisible in the repository: `curl` still gets a token.
- Rejected: a PIN check inside the API on `/full-number`, because D1 closes the road that made it
  matter, and it costs a new step-up operation and contract for a read that moves no money.
- Rejected: an allow-list of addresses in place of the key, because `127.0.0.1` is every caller.

## Consequences

- Every direct caller presents the key: the integration tests, CI's Schemathesis run and Bruno.
- The key is one more secret, the first that two hosts share. The OpenAPI document does not describe
  its header: it is a condition of reaching the API, not part of an operation's contract.
- Not covered: the key authenticates the BFF, not a user, so whoever reads it off the BFF's host can
  call the API. D7 is what makes that host hard to reach.

## Revisit when

- A second legitimate client of the API (a mobile app, a partner integration): one shared key does
  not tell callers apart, so the API needs a credential per client, and the PIN check reopens.
- The API is reached over a network, as anything but the loopback sidecar of D7: stop, DPoP (RFC
  9449) or mutual TLS (RFC 8705) comes first, because ADR-0057's reusable grant rests on loopback.

## Verified by

- `ServiceCredentialTests` in `AzureBank.Tests` (what the API answers a caller that is not the BFF)
  and in `AzureBank.Bff.Tests` (each road of the BFF); `TokenRoadTests`; `NoHttpsRedirectTests`.

## Related

ADR-0001, ADR-0008, ADR-0017, ADR-0018, ADR-0042, ADR-0057, ADR-0063.
