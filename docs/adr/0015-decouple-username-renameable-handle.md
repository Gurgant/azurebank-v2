# ADR-0015: Decouple Identity UserName from the AzureTag; make the handle renameable

**Status**: Accepted

**Date**: 2026-07-17

**Decision Makers**: Vladislav Aleshaev

---

## Context

`AzureTag` — the public payment handle — was also Identity's `UserName` (registration set
`UserName = normalizedAzureTag`) and the entity documented it as *immutable*. Using a public,
user-chosen, intended-to-be-shareable handle as the identity/lookup key is a recognised
anti-pattern: OIDC classifies the subject (`sub`) as the immutable identity anchor and
`preferred_username` as **mutable and unsafe to key on**, and payment apps codify the same
split (Cash App logs in by phone/email while the public `$cashtag` is a separate, changeable
handle that cannot log you in).

In this codebase the coupling was **harmless but redundant**: login is by email
(`FindByEmailAsync` + `CheckPasswordAsync`), no application code calls `FindByNameAsync`, and
`AzureTag` already had its own unique index — so `UserName = AzureTag` just duplicated the
handle. It became a real liability the moment a "rename your handle" feature was wanted
(surfaced by the ADR-0014 audit), because a handle edit would otherwise route through
`SetUserNameAsync` (re-normalise, re-check the username index) and leave stale values in
already-issued tokens.

## Decision

1. **`UserName` = the immutable user Id.** Registration and the Seeder now set the Id
   explicitly with `Guid.CreateVersion7()` (a **UUIDv7** — time-sortable, so it is
   index-friendly and avoids the random-GUID clustered-index fragmentation, matching the
   repo's existing `GuidVersion7ValueGenerator` for domain entities) and set
   `UserName = Id.ToString()`. Login is unchanged (still by email); nothing authenticates by
   `UserName`. `AllowedUserNameCharacters` already permits a GUID string, so no auth change.
2. **`AzureTag` is now a plain, renameable public column** (still lower-cased, unique-indexed,
   regex-validated), no longer Identity's `UserName`.
3. **Rename endpoint** — `PATCH /api/users/me/azuretag` (authenticated): validates the new
   handle (`AzureTagPattern`), rejects one already held by another user (`409`
   `AZURE_TAG_TAKEN`), no-ops if unchanged, and is race-safe (the unique index + a
   `DbUpdateException` guard **scoped to the unique-constraint violation** both map to the same
   `409`; any other database error propagates). Because `UserName` is decoupled, this
   is a plain column update — no Identity username change. It is audit-logged
   (`SecurityEvent=AzureTagRenamed`) and covered by the existing per-user `lookup` rate-limit
   policy on `/api/users/*`.
4. **Data migration** backfills `UserName` / `NormalizedUserName` = the Id for existing rows
   (reversible — the untouched `AzureTag` column restores the old coupling on `Down`).

## Residuals (accepted, documented)

- **Stale handle in the current token after a rename — STILL OPEN, and harmless.** The bearer JWT
  carries `azure_tag` as a claim, so the token keeps the *old* handle until it is re-minted. Nothing
  reads that claim for a decision: the database is the source of truth and `azure_tag` is
  informational.

- **Stale handle in the live BFF session — CLOSED (2026-08-10), and it was not harmless.** This
  clause used to be joined to the one above and carried the remedy *"the frontend should re-fetch
  `/me` after a rename"*. Measured on the running stack, that remedy could not work:
  `GET /bff/auth/me` serves the cached session verbatim, so the re-fetch returned the OLD handle for
  the life of the session while the database held the new one. The frontend even claimed it worked
  — `apiSlice.ts` said the refetch "picks up the new tag (same pattern as setPin)" — which was false
  twice over, since set-pin is BFF-owned and writes the cache back whereas the rename was a plain
  proxied PATCH that ran no BFF code at all.

  The follow-up this record named is now done: the rename is a BFF-owned `PATCH /bff/auth/azuretag`
  shaped exactly like `set-pin`, which calls the API, forwards its errors untouched, and writes the
  handle the API RETURNED into the session. The proxied `/api/users/me/azuretag` still exists and is
  still correct for a direct API caller; what changed is that the app no longer reaches the handle
  through a door that cannot update the cache.

- **Concurrent renames on one session can cache the earlier handle — no longer permanent, see
  [ADR-0039](0039-bff-session-cache-is-a-fallback.md).** Closing the residual above introduced it:
  two renames in flight on the same session may commit upstream in one order and have their
  responses land in the other, so the cache keeps the earlier handle while the database holds the
  later one. It is still not serialised, and the cache is still not repaired — `/bff/auth/me` reads
  through to the API and writes nothing, so the losing handle sits there until some later rename
  overwrites it. What changed is that it is no longer *served*: while the API answers, the client
  gets the database's value. Bypassed, not fixed.

  **This bullet used to reject the fix for two reasons that were both wrong**, and the correction
  matters more than the residual did, because a wrong rejection stops anyone re-examining it. "A
  per-session lock inside a singleton session service" would have fixed nothing — the race spans the
  controller's outbound call and its cache write, and `UpdateUserInfo` mutates the stored reference,
  so a lock there guards an assignment that is already atomic. And "disproportionate" was
  contradicted sixty lines away: `TokenRefresher` already ships exactly that per-session gate.
  ADR-0039 records what was measured and why the lock is still not taken.

- **A rename made through the PROXIED route left the cache stale — CLOSED (2026-08-10) by
  ADR-0039.** Point 3 above says the proxied `PATCH /api/users/me/azuretag` is authenticated and
  race-safe, and it is; what it did not say is that reaching it through the BFF with a session cookie
  renamed the user in the database while `/bff/auth/me` went on serving the old handle for the life
  of the session. One request, no concurrency. Measured, then closed by making `/me` read through.
- **"Taken" is revealed on rename** (a specific `409`), unlike registration's neutral response.
  This is fine and deliberate: the exact-match lookup (ADR-0014) already confirms handle
  existence to a signed-in user, and the endpoint is rate-limited.

## Alternatives considered

- **`UserName = Email` (rejected).** Idiomatic (Microsoft's scaffold default), but email is
  itself mutable; an immutable surrogate Id is the only choice that is permanently stale-proof
  and cleanly matches the OIDC / Cash App "opaque stable id vs mutable public handle" model.
- **Leave `UserName = AzureTag` and add rename later (rejected).** Forces handle edits through
  `SetUserNameAsync` and is the expensive retrofit this ADR avoids by doing the cheap
  groundwork before real data and a rename feature exist.

## Consequences

**Positive** — the login credential and the public handle are cleanly separated; the handle is
freely renameable with a trivial column update; the user Id is now an explicit UUIDv7.

**Negative** — a one-time data migration; and the documented token-claim staleness window
after a rename.

## References

- OIDC `sub` (immutable) vs `preferred_username` (mutable) claim semantics; Cash App / Venmo
  handle-vs-credential decoupling. ADR-0014 (recipient lookup), which surfaced this follow-up.
