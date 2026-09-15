# ADR-0017: PII-safe telemetry and the CodeQL log-forging barrier

**Status**: Accepted

**Date**: 2026-07-20

**Decision Makers**: Vladislav Aleshaev

---

## Context

ADR-0016 made logs leave the process (OTLP → Loki), which changed the stakes: anything logged
is now exported telemetry. The codebase already followed "log the opaque user id, not PII"
almost everywhere, but two sites logged a raw email (failed login for an unknown address —
where no user id exists — and duplicate registration), transfer logs carried money amounts,
and CodeQL's `cs/log-forging` query kept re-flagging user-controlled values at log sinks
(11 dismissed false positives) — a treadmill that would restart on every future PR.

## Decision

### PII in logs

1. **The two email sites redact through the real .NET compliance stack** — a
   `DataClassification` taxonomy (`AzureBank/PII` + `PiiAttribute`), an
   `EmailMaskingRedactor : Redactor`, `AddRedaction` registration, and `IRedactorProvider`
   injection — resolved by classification, not by concrete type, so the masking strategy is a
   one-line swap. Output keeps the first character and the domain (`j***@example.com`):
   on a failed login for an unknown address the masked form is the only remaining signal an
   operator has to spot a credential-stuffing burst.
2. **This was chosen from a three-variant bake-off** built on real branches from the same base:
   - `feat/p5-pii-lean` — static masking helper (works, but showcases nothing of the
     compliance API);
   - `feat/p5-pii-hybrid` — the compliance stack at the call site (**chosen**);
   - `feat/p5-pii-enterprise` — the full pipeline (`[LogProperties]` + classified attributes +
     `EnableRedaction`). The experiment produced this chapter's key negative finding:
     **`EnableRedaction()` (Microsoft.Extensions.Telemetry) and `UseSerilog()` are
     architecturally incompatible** — both want to own `ILoggerFactory`, and Telemetry's
     `ExtendedLoggerFactory` displaces Serilog, silently starving Serilog (and therefore
     Loki) of every application log. The finding is pinned by tests on that branch; the
     runtime pipeline is deliberately NOT registered here.
3. **A redactor is a trust boundary.** The kept domain tail is echoed verbatim, so it survives
   only for a provably well-formed single address (exactly one `@`, non-empty on both sides,
   no control/format/separator/whitespace character anywhere); everything else collapses to
   `***`. Without this, a crafted "email" like `a@b` + CRLF + `[WARN]` would forge log lines
   *through* the PII defence.
4. **Masking is pseudonymisation, not anonymisation**: masked lines remain personal data for
   GDPR retention/access purposes. The control reduces exposure; it does not exempt the logs.
5. **Money amounts do not appear in log lines** (financial data in exported logs); the
   transaction number is the audit-trail key. Amount distributions, if ever needed, belong in
   a histogram value with domain buckets — never a label, never a log line.

### CodeQL log-forging barrier

6. **One central sanitizer** — `AzureBank.Shared.Utilities.LogSanitizer.Sanitize(string)` —
   replaces the previous inline CR/LF strips. It removes every `\p{C}` code unit (C0/C1
   controls, DEL, format, private-use, unassigned, surrogates) plus the U+2028/U+2029 line
   separators. `Regex.Replace` on purpose: it is one of the call shapes CodeQL's built-in
   `StringReplaceSanitizer` already recognises, so flows are suppressed by the analyzer's own
   heuristics today AND by the explicit model regardless of future reimplementation.
7. **A CodeQL model pack** (`.github/codeql/extensions/azurebank-csharp-models`) declares the
   method's return value a `barrierModel` row of kind `log-injection` — code-scanning default
   setup picks the pack up automatically. CodeQL then trusts that claim unconditionally, so
   **the claim is kept honest by tests**: the suite sweeps every UTF-16 code unit in the BMP
   (0x0000–0xFFFF) against an independent oracle (`CharUnicodeInfo`, not the regex under
   test), asserting both directions — trusted categories stripped, everything else preserved.
   Weakening the sanitizer cannot pass tests while the model keeps lying to the analyzer.
8. **The name-heuristic alert classes are handled by triage, not modelling.**
   `cs/cleartext-storage-of-sensitive-information` and
   `cs/exposure-of-sensitive-information` classify sources with name regexes baked into the
   queries (no models-as-data extensibility), and the only barrier kind those queries consume
   (`file-content-store`) is for genuine PII maskers — attaching it to a CR/LF stripper would
   suppress true positives. Per-alert dismissal with a stated reason remains the honest tool;
   a future real `Mask()`/`Redact()` helper may be modelled with that kind and its own test
   guard.

## Alternatives considered

- **Lean helper only**: smallest diff, but demonstrates none of the compliance machinery and
  gives no classification seam for future fields.
- **Full enterprise pipeline**: rejected on evidence — it would kill the log pillar (see the
  pinned incompatibility above). Kept as a documented experiment branch.
- **`HmacRedactor`** (correlatable hashes): rejected — ~~it needs a production-looking managed
  key in a public repo, and~~ the opaque user id already serves as the correlation key.
  *(First reason struck 2026-09-11: this repository already holds six keys, every one in
  user-secrets and none in the repo, so a seventh would need no key in a public repo. The second
  reason stands, and it is the one the log-identifier rule below rests on.)*
- **Advanced CodeQL setup with query filters**: rejected — its only extra lever (excluding
  queries) would silence true positives; the model pack works under default setup.

## Consequences

- Loki receives no raw email, no amounts; the two auth sites emit an operator-useful masked
  form; blanket tests assert the raw address never reaches any log call at any level.
  *(Corrected 2026-09-11: "no amounts" was false from the day it was written — the deposit and
  withdrawal lines logged the amount and the new balance. Both are gone, and
  `LogPlaceholderClassTests` keeps them out; see the rule below.)*

## The log-identifier rule (added 2026-09-11)

The question this record left open — pseudonymise identifiers in logs everywhere, or nowhere — is
closed by the reason already given against `HmacRedactor`: the opaque id IS the correlation key,
and hashing one site while the others log it in clear would make that one line the only one that
cannot be read against the rest. So every placeholder in a log template has exactly one class:

1. **Surrogate keys** — user, account, transaction, authorisation, token-row and notice ids —
   are logged in clear. Not credentials, not personal data, useless without the database.
2. **Secrets** appear only as their first eight characters, through one helper,
   `SecretPrefix.Of`; the BFF's session id is the one secret any line names.
3. **Direct identifiers** — a handle, a name, anything a person chose to be known by — never
   appear. An email appears only as the redactor's masked form (D1-D3 above).
4. **Money** never appears (D5).

Everything else is operational: counts, codes, durations, event and endpoint names.
`LogPlaceholderClassTests` scans every log template in the API, the BFF, Infrastructure and the
Function, and fails on an unclassified placeholder, on a direct identifier or an amount, on a
session id not passed through `SecretPrefix.Of`, and on an email not passed through the redactor.
Its first run found amounts and balances on two lines, handles on three and an account's chosen
name on one; all six were fixed in the same change.

~~**What it does not see**, named rather than hidden: a value that reaches a log through an
operational placeholder. The request log prints the request path, and `GET /api/users/{azureTag}`
puts a handle in it — measured, `HTTP GET /api/users/janesmith responded 200`. The guard reads
templates, and a path is a value; logging the route template instead of the path is its own
change.~~ *(struck 2026-09-14, in the same PR, after review read the gap as a finding rather than a
note. Three things closed it, each measured. The request line of the API and of the BFF names the
ROUTE PATTERN: `HTTP GET /api/users/{azureTag} responded 401` and
`HTTP GET /api/users/{**catch-all} responded 401` read through the hosts in `RequestLogRouteTests`,
and on the running stack `HTTP GET /api/users/{azureTag} responded 200 in 109.2336ms` for an authenticated lookup and
`HTTP GET (unmatched) responded 404` for `/api/nowhere/janesmith`, with zero occurrences of the
handle in the API's console. The BFF's console had two, from a channel no template guard can
see: YARP's own forwarder, `Proxying to https://localhost:7215/api/users/janesmith HTTP/2
RequestVersionOrLower`, at Information. `Yarp.ReverseProxy.Forwarder` and
`System.Net.Http.HttpClient` — which prints every upstream URL the BFF's own client calls — are
overridden to Warning in the BFF's `appsettings.json`; the same lookup with a session then left
`HTTP GET /api/users/{**catch-all} responded 200 in 47.8785ms` and nothing else, and
`RequestLogRouteTests` drives the real forwarder over a stub upstream to keep it so, with a
control that puts the forwarder back at Information and finds the handle. Two more things the
adversarial pass found once the line left the process: the API's request logging sat INSIDE the
exception handler, so a domain refusal unwound through it as an exception and it logged
`responded 500` at Error, stack trace attached, for every 401 or 422 the handler then wrote
(measured through the host: a wrong password read `HTTP POST /api/auth/login responded 500`) —
it sits outside the handler now and reads 401 at Information; and the handler middleware nulls
the endpoint before its handlers run, so their lines read `(unmatched)` — `RequestLogRoute`
falls back to the endpoint the handler feature preserves. `RequestLogRoute` replaces the middleware's
property set, so `RequestPath` is not an attribute either, and the line is written through the host
logger now — it went to the console-only bootstrap logger before, outside every enricher and sink.
Then the measurement that widened it: read through the host logger, the line's properties carried
`RequestId="0HNOIG7UQS63L" | RequestPath="/api/users/janesmith"` — ASP.NET Core's hosting scope,
attached to EVERY event written during a request whatever its template said, and exported by the
OpenTelemetry sink as an attribute; `RequestPathEnricher` strips it from every event and names the
pattern instead. And the BFF's own security lines named `{Path}` seven times — measured through the
BFF's test host, the `AuthLevelMiddleware` event of an unauthenticated `GET /api/users/janesmith`:
`SecurityEvent="SessionRequired" | Method="GET" | Path="/api/users/janesmith"`; they name
`{RoutePattern}` now, and `Path` and `RequestPath` are classified direct identifiers so the guard
refuses their return. A request nothing routes logs `(unmatched)`: its path is whatever the client
sent. One more value channel the same pass found and closed: `AppExceptionHandler` logged a domain
refusal's MESSAGE, and attached the exception — for a recipient lookup that is "Recipient with
identifier 'janesmith' was not found." in `{Message}` and again in `exception.message`; it logs the
code and the type now, and nothing else. Two it found and this record leaves OPEN, named rather than
hidden: the trace exporter's server and client spans carry `url.path` and `url.full` with the same
handle (ADR-0016's instrumentation; whether spans fall under this rule is a decision not yet taken —
mechanically, a processor that drops `url.path` and trims `url.full` to scheme and host, keeping
`http.route`), and an UNHANDLED fault's `exception.stacktrace` renders whatever the innermost message
says — a duplicate-key `SqlException` echoes the key value, which for a registration race is the
normalised email.)*
- Future `cs/log-forging` findings on values routed through `LogSanitizer.Sanitize` stop at
  the barrier instead of restarting the dismissal treadmill; the pack's pickup is verifiable
  in the code-scanning tool status of the first hosted run on this PR.
- The bake-off branches (`feat/p5-pii-lean`, `feat/p5-pii-enterprise`) remain for inspection.

## References

- ADR-0016 (the observability chapter this hardens).
- CodeQL models-as-data / `barrierModel` (kind `log-injection`, consumed by
  `LogForgingQuery.qll`); model packs in code-scanning default setup.
- Microsoft.Extensions.Compliance.Redaction; Serilog.Sinks.OpenTelemetry.
