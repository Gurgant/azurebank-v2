# ADR-0014: Recipient lookup — exact-match, harvest-resistant

**Status:** Accepted · **Date:** 2026-07-17 · **Amended:** 2026-09-15 (the limit is the BFF's),
2026-10-04 (ADR-0063, the public demo) · **Decision Makers:** Vladislav Aleshaev

## Context

A transfer needs a way to confirm a payee by its public handle (`AzureTag`). Saying "this handle
exists and belongs to this person" is the feature: it prevents misdirected transfers, so the lookup
cannot be enumeration-neutral the way login and registration are (ADR-0012, ADR-0013). A partial
name after an exact hit is the accepted pattern (Zelle shows the enrolled name, Cash App the
$cashtag's owner). The danger is amplification. A substring search is a customer-directory
harvester, and `[Authorize]` is a weak barrier while registration is open: one throwaway account
sweeps the 3-character space (about 46k queries, 10 results each) under the per-IP global limit. The
goal is harvest-resistance: guessing exact handles one at a time, at a metered, monitored cost.

## Decision

1. **Exact-match only.** `GET /api/users/search` and `SearchUsersAsync` are deleted, because a
   substring search is the harvest amplifier. `GET /api/users/{azureTag}` is the sole recipient
   lookup, the Zelle / Cash App model: it answers 200 with a masked display name (the first name and
   the surname's initial) and `exists`, and an unknown handle is `exists: false`, never a 404.
2. **Per-user rate limit.** A dedicated `lookup` policy (sliding, 20 per 60 s by default) on the
   BFF's `/api/users/*` route, partitioned per authenticated user (session to user id, IP as the
   fallback) and not per IP, because with open registration the abuse unit is the account, not the
   address (the Venmo precedent: per-IP limiting alone was bypassed with many addresses and
   accounts). On the public demo (`Demo:Enabled`) the unit is still the account, and the account is
   a claimed copy: registration is closed there, and the API gives one client address
   `Demo:Claim:MaxPerClientPerDay` copies in a rolling 24 hours, 10 by default (ADR-0063, decision
   7, says by how much that cap can be overshot).
3. **Hardening of the exact lookup.** `[Required][AzureTagQuery]` validates the route parameter; the
   EF query projects only `{Id, FirstName, LastName}`, so it never materialises `PasswordHash`,
   `PinHash` or `SecurityStamp`; the handle is normalised with `ToLowerInvariant` to match
   registration; a self-lookup answers `exists: false` with no name echoed.

## Rejected

- Rejected: keeping the substring search, because no payment app ships directory substring search.
- Rejected: a prefix (`StartsWith`) type-ahead, because it cuts coverage per query but is still a
  browsable directory: a payment-handle lookup is an exact-match confirmation, not a search box.
- Rejected: CAPTCHA or proof-of-work on the lookup, because no bank taxes a core payment path this
  way, and per-user limiting with monitoring protects without the friction.
- Deferred: Confirmation-of-Payee "assert then verify" (the payer supplies the expected name, the
  API answers match, close or no-match), because it is the most harvest-resistant and what the
  regulated SEPA and UK rails mandate, but disproportionate for an MVP with no such flow. The EPC
  Verification-of-Payee rulebook treats lax matching as a name-harvesting risk.

## Consequences

- The directory is not browsable. The anti-automation control of OWASP ASVS 5.0 §2.4.1 (L2, which
  names data exfiltration) is enforced per user on the lookup path, at the BFF. The exact lookup
  validates its input and loads no secrets into memory.
- There is no in-app recipient type-ahead. Finding a payee takes the exact handle, obtained out of
  band (a shared handle, a QR code, a pay-me link), as in Revolut and Cash App.
- On the public demo a copy's owner looks up handles inside its own copy only (ADR-0062, decision
  7), so a sweep there finds the copy's two contacts and nobody else.
- The handle is not the Identity `UserName`: ADR-0015 decouples the two and adds the rename.
- Not covered: per-user limiting is bounded, not absolute. An attacker registers many throwaway
  accounts, each with its own budget; with exact match each can only guess one handle per request,
  at 20 a minute per account, and the rejections are logged. This is harvest-resistance, not
  prevention: closing it fully needs bot defence or device signals.
- Not covered: the limit is the BFF's. The API registers no rate limiter, so 20 a minute is the
  BFF's number and the API's is unbounded. A browser has no token to present to the API's own origin
  (ADR-0001, ADR-0038, ADR-0041), and the API refuses a request without the BFF's service credential
  (ADR-0055). Closing it means a limiter in the API, partitioned on the token's subject.
  `SECURITY.md` lists it beside the other controls that stop at the BFF.

## Verified by

- `UserEndpointTests` and `UserServiceTests`: exact match only, the masked name, an unknown handle,
  a self-lookup, an invalid handle.
- `RateLimiterTests` (`YarpUsersRoute_CarriesTheLookupPolicy_PerClient`): the BFF's `lookup` policy.

## Related

ADR-0012, ADR-0013, ADR-0015, ADR-0055, ADR-0062, ADR-0063.
