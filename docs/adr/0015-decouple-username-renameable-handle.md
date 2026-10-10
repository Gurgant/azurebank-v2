# ADR-0015: Decouple Identity UserName from the AzureTag; make the handle renameable

**Status:** Accepted · **Date:** 2026-07-17 · **Amended:** 2026-08-10 (ADR-0039, the handle in the
BFF session) · **Decision Makers:** Vladislav Aleshaev

## Context

`AzureTag`, the public payment handle, used to be Identity's `UserName` as well, and the entity
documented it as immutable. A public, user-chosen, shareable handle as the identity key is a
recognised anti-pattern: OIDC classifies `sub` as the immutable identity anchor and
`preferred_username` as mutable and unsafe to key on, and payment apps make the same split (Cash App
signs in by phone or email, and the `$cashtag` is a separate, changeable handle that cannot sign
in). The coupling is harmless while nothing renames: login is by email, no code calls
`FindByNameAsync`, and `AzureTag` has its own unique index. A rename makes it a liability: the edit
would go through `SetUserNameAsync` and leave stale values in tokens already issued.

## Decision

1. **`UserName` is the immutable user id.** Registration and the Seeder set the id explicitly with
   `Guid.CreateVersion7()` and set `UserName = Id.ToString()`. A UUIDv7 because it is time-sortable:
   index-friendly, without the clustered-index fragmentation of a random GUID, and the same as the
   `GuidVersion7ValueGenerator` the domain entities use. Login stays by email, nothing authenticates
   by `UserName`, and `AllowedUserNameCharacters` already permits a GUID string.
2. **`AzureTag` is a plain, renameable public column**: lower-cased, unique-indexed,
   regex-validated, and not Identity's `UserName`.
3. **Rename endpoint.** `PATCH /api/users/me/azuretag` (authenticated) validates the new handle
   (`AzureTagPattern`), rejects one held by another user (409 `AZURE_TAG_TAKEN`) and does nothing if
   the handle is unchanged. It is race-safe: the unique index and a `DbUpdateException` guard scoped
   to the unique-constraint violation map to the same 409, and any other database error propagates.
   Because `UserName` is decoupled it is a plain column update, with no Identity username change. It
   is audit-logged (`SecurityEvent=AzureTagRenamed`) and covered by the per-user `lookup` rate-limit
   policy on `/api/users/*`.
4. **A data migration** backfills `UserName` and `NormalizedUserName` with the id for existing rows.
   It is reversible: the untouched `AzureTag` column restores the old coupling on `Down`.

## Rejected

- Rejected: `UserName = Email`, because an email is itself mutable. It is idiomatic (Microsoft's
  scaffold default), but an immutable surrogate id is the only choice that never goes stale, and it
  matches the OIDC and Cash App model of an opaque stable id and a mutable public handle.
- Rejected: leaving `UserName = AzureTag` and adding the rename later, because handle edits would go
  through `SetUserNameAsync`: an expensive retrofit, where the decoupling is cheap groundwork before
  real data and a rename feature exist.

## Consequences

- The login credential and the public handle are separate. The handle is renameable with a column
  update, and the user id is an explicit UUIDv7. The cost is a one-time data migration.
- The app renames through the BFF's own `PATCH /bff/auth/azuretag`, shaped like `set-pin`: it calls
  the API, forwards its errors untouched and writes the handle the API returned into the session. A
  proxied PATCH runs no BFF code and cannot update the cached handle, and a re-fetch of
  `/bff/auth/me` could not correct it while `/me` served the cache verbatim.
- `/bff/auth/me` reads through to the API (ADR-0039), so a rename made by any route, the proxied
  `/api/users/me/azuretag` included, is what the client sees while the API answers.
- "Taken" is revealed on rename, by a specific 409, unlike registration's neutral answer. That is
  deliberate: the exact-match lookup (ADR-0014) already confirms a handle's existence to a signed-in
  user, and the endpoint is rate-limited.
- Not covered: the handle in the current token. The bearer JWT carries `azure_tag` as a claim, so
  the token keeps the old handle until it is re-minted. Nothing reads that claim for a decision: the
  database is the source of truth and the claim is informational.
- Not covered: two renames in flight on one session can commit upstream in one order and land their
  responses in the other, so the session keeps the earlier handle. It is not serialised and the
  cache is not repaired: the value is only not served while the API answers. ADR-0039 says why no
  per-session lock is taken.

## Verified by

- `UserEndpointTests` (`Register_SetsUserNameToImmutableUuidV7Id_NotAzureTag`, `Rename_*`).
- `AzureTagRenameTests` (BFF): a rename is visible on the same session, and a 409 rides through.
- `UserServiceTests`: the rename's rules. The migration is `DecoupleUserNameFromAzureTag`.

## Related

ADR-0014, ADR-0039, ADR-0042.
