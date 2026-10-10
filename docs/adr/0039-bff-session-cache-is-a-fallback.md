# ADR-0039: The BFF session cache is a fallback, never the answer

**Status:** Accepted · **Date:** 2026-08-10 · **Amended:** 2026-09-30 (ADR-0058, the 55 s wait).
Supersedes nothing. Closes the stale-handle residual of ADR-0015.

## Context

The BFF keeps a `UserSessionInfo` block in the session, and `GET /bff/auth/me` used to return it
verbatim. That holds only while nothing in the block can change mid-session, and one field can:
`AzureTag` is a renameable public handle (ADR-0015). The app's own rename goes through
`PATCH /bff/auth/azuretag`, which writes the returned handle into the session, but the proxy still
serves `PATCH /api/users/me/azuretag` to a session cookie. One request there, with no race and no
double-submit, changes the database and leaves the cached handle stale for the life of the session.
Two concurrent renames whose responses land out of order can do the same.

## Decision

**`/bff/auth/me` reads through to `GET /api/auth/me` and serves the cached block only when that read
cannot be completed.** The cache is a degrade path, not an answer.

1. **The API is asked on every `/me`.** The token comes from the existing re-mint helper, because
   this call bypasses the YARP transform as the controller's other out-of-band calls do. The read
   has 5 s for the token and the call together, after which the cached block is served.
2. **Every failure serves the cache**: an unreachable API, a non-2xx, an unparseable body, a null
   re-mint. `authSlice` treats a rejected `getMe` as not signed in, so surfacing the failure would
   sign out every user for the length of a backend hiccup. A name one rename out of date is the
   lesser harm.
3. **`HasPin` is carried over from the cache, not read**, because the API's `/api/auth/me` returns
   `UserResponse` (`{userId, azureTag, email, firstName, lastName}`), which has no `HasPin`. It is
   BFF-owned state: a block rebuilt from the API alone would flip it to false and re-prompt a user
   who already set a PIN.
4. **The read writes nothing**, because a `/me` that begins before a rename and lands after it would
   restore the superseded handle: a read overwriting a newer write.

The rename endpoint keeps its own cache write. Correctness no longer depends on it; it keeps the
fallback fresh for the path users take.

## Rejected

- Rejected: a per-session lock around the rename, because it would hold a gate across an outbound
  call that has no `CancellationToken` and waits `BackendApi:TimeoutSeconds` (55 s, ADR-0058), so a
  hung API would serialise a session's renames behind that wait; and bounding the call would abandon
  a committed mutation, which produces the staleness this record removes.
- Rejected: that lock inside the session service, because the race spans the API call and the cache
  write, both in the controller: the lock would guard one atomic reference assignment.
- Rejected: compare-and-swap on the pre-call handle to order two renames, because it is correct in
  the two interleavings where last-write-wins is wrong, wrong in the two where it is right, and
  silent either way, since `UpdateUserInfo` returns `void`.
- Rejected: compare-and-swap to keep a write-back from the read, because `UpdateUserInfo` hands the
  lambda the live session object, so the comparison reads what the rename already wrote and not a
  snapshot. Deleting the write-back removes the defect outright.
- Rejected: an API-issued revision token, because it is a contract change (a DTO field, the spec,
  the mock, the frontend types) and still leaves the proxied route open.
- Rejected: blocking the proxied rename route at the BFF, because it closes that route but not the
  race, and the catch-all `/api/{**catch-all}` route makes closing it more than deleting one entry.
- Rejected: invalidate-on-rename, because without the read-through `/me` has no path to the API, so
  a dirty flag has no reader.

## Consequences

- While the API is reachable, `/me` cannot serve a handle the database disagrees with, by any route,
  including routes not yet written.
- `/me` costs one upstream call plus a token re-mint, at app boot and on each `Session`
  invalidation. It is not polled, so the volume is a handful per session.
- Not covered: a stale name while the API is down. The fallback does not learn about a rename made
  outside the app, so if the API fails right after one, `/me` serves the previous handle until the
  API returns.
- Not covered: two renames can still land out of order and leave the cache holding the loser, and
  nothing corrects that cache until a later rename overwrites it. The value is not served while the
  API answers: the staleness is bypassed, not repaired.

## Verified by

- `MeReadsThroughToTheApiTests`: the API is asked, each failure serves the cache, `HasPin` survives
  the read, and a read never writes the cache.
- `AzureTagRenameTests`: the rename endpoint caches the handle the API returned.

## Related

ADR-0015, ADR-0019, ADR-0021, ADR-0038, ADR-0057, ADR-0058.
