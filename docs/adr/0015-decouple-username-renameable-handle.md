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

- **Stale handle in the current token/session after a rename.** The bearer JWT carries
  `azure_tag` as a claim and the BFF session caches the handle, so both keep the *old* handle
  until they refresh on next login. This is not a security issue — the database is the source
  of truth and `azure_tag` is informational — but the frontend should re-fetch `/me` after a
  rename. Propagating a rename into the live BFF session is a follow-up.
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

**Negative** — a one-time data migration; and the documented token/session staleness window
after a rename.

## References

- OIDC `sub` (immutable) vs `preferred_username` (mutable) claim semantics; Cash App / Venmo
  handle-vs-credential decoupling. ADR-0014 (recipient lookup), which surfaced this follow-up.
