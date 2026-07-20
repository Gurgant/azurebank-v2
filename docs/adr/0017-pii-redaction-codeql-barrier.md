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
- **`HmacRedactor`** (correlatable hashes): rejected — it needs a production-looking managed
  key in a public repo, and the opaque user id already serves as the correlation key.
- **Advanced CodeQL setup with query filters**: rejected — its only extra lever (excluding
  queries) would silence true positives; the model pack works under default setup.

## Consequences

- Loki receives no raw email, no amounts; the two auth sites emit an operator-useful masked
  form; blanket tests assert the raw address never reaches any log call at any level.
- Future `cs/log-forging` findings on values routed through `LogSanitizer.Sanitize` stop at
  the barrier instead of restarting the dismissal treadmill; the pack's pickup is verifiable
  in the code-scanning tool status of the first hosted run on this PR.
- The bake-off branches (`feat/p5-pii-lean`, `feat/p5-pii-enterprise`) remain for inspection.

## References

- ADR-0016 (the observability chapter this hardens).
- CodeQL models-as-data / `barrierModel` (kind `log-injection`, consumed by
  `LogForgingQuery.qll`); model packs in code-scanning default setup.
- Microsoft.Extensions.Compliance.Redaction; Serilog.Sinks.OpenTelemetry.
