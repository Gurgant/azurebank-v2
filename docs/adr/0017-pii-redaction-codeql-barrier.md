# ADR-0017: PII-safe telemetry and the CodeQL log-forging barrier

**Status:** Accepted · **Date:** 2026-07-20 · **Amended:** 2026-09-11 (the log-identifier rule),
2026-09-14 (the rule applied to the request log) · **Decision Makers:** Vladislav Aleshaev

## Context

ADR-0016 makes logs leave the process (OTLP to Loki), so anything logged is exported telemetry.
The code follows "log the opaque user id, not PII" almost everywhere. Before this decision two
sites logged a raw e-mail address (a failed login for an unknown address, where no user id exists,
and a duplicate registration), and transfer lines carried money amounts. CodeQL's `cs/log-forging`
query also re-flags every user-controlled value that reaches a log sink (11 alerts dismissed as
false positives), and would again on every later change.

## Decision

### PII in logs

1. **The two e-mail sites redact through the .NET compliance stack** (D1): a `DataClassification`
   taxonomy (`AzureBank/PII` and `PiiAttribute`), `EmailMaskingRedactor`, `AddRedaction` and an
   injected `IRedactorProvider`, resolved by classification and not by concrete type, so that the
   masking strategy is a one-line swap. The output keeps the first character and the domain
   (`j***@example.com`), because on a failed login for an unknown address the masked form is the
   only signal left of a credential-stuffing burst.
2. **The compliance stack is used at the call site, and the runtime pipeline is not registered**
   (D2), because `EnableRedaction()` (Microsoft.Extensions.Telemetry) and `UseSerilog()` both want
   to own `ILoggerFactory`: Telemetry's `ExtendedLoggerFactory` displaces Serilog and silently
   starves it, and therefore Loki, of every application log.
3. **A redactor is a trust boundary** (D3): the domain tail is echoed only for a provably
   well-formed single address (exactly one `@`, non-empty on both sides, no control, format,
   separator or whitespace character), and everything else collapses to `***`, because a crafted
   "email" such as `a@b` + CRLF + `[WARN]` would otherwise forge log lines through the PII defence.
4. **Masking is pseudonymisation, not anonymisation** (D4): masked lines remain personal data for
   GDPR retention and access. The control reduces exposure; it does not exempt the logs.
5. **Money amounts do not appear in log lines** (D5), because they are financial data in exported
   logs; the transaction number is the audit-trail key. An amount distribution, if ever needed,
   belongs in a histogram value with domain buckets: never a label, never a log line.

### CodeQL log-forging barrier

6. **One central sanitizer, `AzureBank.Shared.Utilities.LogSanitizer.Sanitize(string)`** (D6),
   removes every `\p{C}` code unit (C0 and C1 controls, DEL, format, private-use, unassigned,
   surrogates) and the U+2028 and U+2029 line separators. It is a `Regex.Replace`, because that is
   a call shape CodeQL's built-in `StringReplaceSanitizer` already recognises.
7. **A CodeQL model pack, `.github/codeql/extensions/azurebank-csharp-models`, declares the
   method's return value a `barrierModel` of kind `log-injection`** (D7), and code-scanning default
   setup picks the pack up. CodeQL trusts that claim unconditionally, so tests keep it honest: they
   sweep every UTF-16 code unit of the BMP against an independent oracle (`CharUnicodeInfo`, not
   the regex under test), in both directions.
8. **The name-heuristic alert classes are handled by triage, not by a model** (D8):
   `cs/cleartext-storage-of-sensitive-information` and `cs/exposure-of-sensitive-information`
   classify sources by name regexes inside the queries, and the only barrier kind they consume
   (`file-content-store`) is for genuine PII maskers: on a CR/LF stripper it would suppress true
   positives. Each such alert is dismissed with a stated reason.

### The log-identifier rule

Identifiers in logs are not pseudonymised, because the opaque id is the correlation key: hashing
it at one site while the others log it in clear would make that line the only one that cannot be
read against the rest. So every placeholder in a log template has exactly one class:

1. **Surrogate keys** (user, account, transaction, authorisation, token-row and notice ids) are
   logged in clear: not credentials, not personal data, useless without the database.
2. **Secrets** appear only as their first eight characters, through one helper, `SecretPrefix.Of`;
   the BFF's session id is the one secret any line names.
3. **Direct identifiers** (a handle, a name, anything a person chose to be known by) never appear.
   An e-mail appears only as the redactor's masked form (D1-D3). A request path is one too:
   `GET /api/users/{azureTag}` carries a handle, so `Path` and `RequestPath` are in this class.
4. **Money** never appears (D5).

Everything else is operational: counts, codes, durations, event and endpoint names. The rule is
about the event, not the sentence: it covers every value and property an event carries, whatever
its template says.

## Rejected

- Rejected: a static masking helper alone, because it gives no classification seam for later fields.
- Rejected: the full pipeline (`[LogProperties]`, classified attributes, `EnableRedaction`),
  because it would kill the log pillar (D2).
- Rejected: `HmacRedactor` (correlatable hashes), because the opaque user id already is the
  correlation key.
- Rejected: advanced CodeQL setup with query filters, because its only extra lever, excluding
  queries, would silence true positives; the model pack works under default setup.

## Consequences

- No log template carries a raw e-mail address or an amount to Loki; the two auth sites emit the
  masked form.
- The request line of the API and of the BFF names the route pattern (`RequestLogRoute`), as in
  `HTTP GET /api/users/{azureTag} responded 200`; a request nothing routes logs `(unmatched)`.
- `RequestPathEnricher` strips `RequestPath`, which ASP.NET Core's hosting scope attaches to every
  event of a request, and names the pattern; the BFF's security lines name `{RoutePattern}`.
- `Yarp.ReverseProxy.Forwarder` and `System.Net.Http.HttpClient`, which print upstream URLs at
  Information, log from Warning in the BFF.
- `AppExceptionHandler` logs a domain refusal's code and type, never its message, because the
  message can quote a handle.
- A `cs/log-forging` finding on a value routed through `LogSanitizer.Sanitize` stops at the barrier.
- Not covered: the trace exporter's spans carry `url.path` and `url.full`, with the same handle.
  Whether spans fall under the rule is not decided; a processor could drop `url.path` and trim
  `url.full` to scheme and host, keeping `http.route`.
- Not covered: an unhandled fault's `exception.stacktrace` renders whatever the innermost message
  says: a duplicate-key `SqlException` echoes the key, which in a registration race is the
  normalised e-mail address.

## Verified by

- `LogPlaceholderClassTests` (every log template of the API, the BFF, Infrastructure and the
  Function), `RequestLogRouteTests` (API and BFF) and `DomainRefusalLogTests`.
- `LogSanitizerTests` (the BMP sweep), `EmailMaskingRedactorTests`, `SecretPrefixTests`.

## Related

ADR-0016, ADR-0045, ADR-0055, ADR-0058, ADR-0061.
