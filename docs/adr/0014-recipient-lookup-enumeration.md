# ADR-0014: Recipient lookup — exact-match, harvest-resistant

**Status**: Accepted

**Date**: 2026-07-17

**Decision Makers**: Vladislav Aleshaev

---

## Context

Transfers need a way to confirm a payee by their public handle (`AzureTag`). The API had
two authenticated (`[Authorize]`) endpoints:

- `GET /api/users/{azureTag}` — **exact** match, returns a masked display name
  (`"Vladislav A."`) + `Exists` so the payer can confirm the right person.
- `GET /api/users/search?azureTag=...` — a **substring** search (`AzureTag.Contains(query)`,
  min 2 chars in the service / 3 in the attribute, capped at 10 results).

The substring search is a **customer-directory harvester**. `[Authorize]` is a weak barrier
because registration is open and auto-logs-in, so an attacker registers one throwaway account
and then sweeps: iterating the 3-character space (~46k queries) against `Contains` + 10
results/query, bounded only by the generous per-IP global limit (~300/min ⇒ ~3000 handles/min),
reveals most of the handle namespace and the masked names attached to it. No real payments app
offers substring search on a payment handle for exactly this reason.

**Key insight — this is NOT the login-enumeration problem (ADR-0012/0013).** Recipient lookup
*cannot* be enumeration-neutral and still work: revealing "this handle exists and belongs to
Vladislav A." **is the feature** (it prevents misdirected transfers). So the goal is not to
hide existence; it is **harvest-resistance** — remove the amplification so an attacker is
reduced to guessing exact handles one at a time, at a metered, monitored cost.

The masked name is not a leak to apologise for — showing a partial name after an *exact* hit
is the accepted industry pattern (Zelle shows the enrolled name; Cash App shows the $cashtag
owner). The anomaly was the substring sweep, not the confirmation.

## Decision

1. **Exact-match only.** Delete `GET /api/users/search` and `SearchUsersAsync`. The exact
   `GET /api/users/{azureTag}` is the sole recipient lookup — the Zelle / Cash App model.
2. **Per-user rate limit.** A dedicated `lookup` policy (sliding, default 20/60s) on the BFF's
   `/api/users/*` route, partitioned per **authenticated user** (session → user id, IP
   fallback), not per IP — because with open registration the abuse unit is the account, not
   the address (the Venmo precedent: per-IP limiting alone was bypassed via many IPs/accounts).
3. **Hardening of the exact lookup.** `[Required][AzureTagQuery]` on the route parameter (was
   unvalidated); project the EF query to only `{Id, FirstName, LastName}` so it never
   materialises `PasswordHash`/`PinHash`/`SecurityStamp`; `ToLowerInvariant` normalisation to
   match registration; a self-lookup returns not-found with no name echoed.

## Residuals (accepted, documented)

- **Per-user limiting is bounded, not absolute.** A determined attacker registers many
  throwaway accounts, each with its own budget (Venmo lost exactly this argument). Combined
  with exact-match — which removes the wildcard sweep so each account can only guess one exact
  handle per request — the attack is reduced from "sweep the directory" to "guess handles at
  20/min/account", and the rejections are logged for detection. This is harvest-resistance, not
  prevention; closing it fully needs bot-defense / device signals (out of scope for a demo).
- **`AzureTag` is currently the Identity `UserName`.** Harmless today (login is by email;
  nothing authenticates by `UserName`), but decoupling it (set `UserName` to the immutable
  user Id, keep `AzureTag` as a plain public-handle column) is tracked as a separate hygiene
  follow-up that also unblocks a "rename your handle" feature.
- **Discovery** (finding *who* to pay when you don't have their handle) is intentionally
  out-of-band — a shared handle, QR, or pay-me link — as in Revolut/Cash App, not a directory
  browse. A roadmap item, not this change.

## Alternatives considered

- **Keep the substring search (rejected).** No payment app ships directory substring search;
  it is the harvest amplifier.
- **Downgrade to a prefix (`StartsWith`) type-ahead (rejected).** Cuts coverage-per-query but
  is still a browsable directory; a payments handle lookup is an exact-match confirmation, not
  a search box.
- **CAPTCHA / proof-of-work on lookup (rejected).** No bank taxes a core payment path this
  way; per-user limiting + monitoring achieves the protection without the friction.
- **Confirmation-of-Payee "assert then verify" (deferred).** The payer supplies the expected
  name and the API answers match/close/no-match — maximally harvest-resistant and what the
  regulated SEPA/UK rails mandate, but disproportionate for an MVP with no such flow.

## Consequences

**Positive** — the directory is no longer browsable; the mandated anti-automation control
(OWASP ASVS 5.0 §2.4.1, L2, names *data exfiltration* explicitly) is enforced per-user on the
lookup path; the exact lookup no longer loads secrets into memory and validates its input.

**Negative** — there is no in-app recipient type-ahead; finding a payee requires their exact
handle (obtained out-of-band). This matches how real payment apps work and is the intended
trade-off. The per-user residual is accepted and monitored.

## References

- OWASP ASVS 5.0 §2.4.1 (anti-automation vs data exfiltration).
- Zelle / Cash App recipient-confirmation model; Venmo scraping precedent (per-IP limits alone
  are insufficient under cheap accounts); EPC Verification-of-Payee rulebook (lax matching =
  name-harvesting risk).
- ADR-0012 (login enumeration/lockout), ADR-0013 (registration enumeration + the BFF limiter
  this reuses).
