# ADR-0039: The BFF session cache is a fallback, never the answer

**Status:** Accepted · **Date:** 2026-08-10 · **Supersedes nothing.** Closes the residual
[ADR-0015](0015-decouple-username-renameable-handle.md) left open, and corrects the reasoning that
record used to justify leaving it.

## Context

The BFF keeps a `UserSessionInfo` block in the session and `GET /bff/auth/me` returned it verbatim.
The justification is written on the field itself — *"Cached user information to avoid API calls on
/bff/auth/me"* — and it held as long as nothing in that block could change mid-session.

One field can. `AzureTag` is a renameable public handle (ADR-0015), and every fix so far has closed
one route to changing it while leaving another open.

[PR #100](https://github.com/Gurgant/azurebank-v2/pull/100) closed the route the app itself uses:
the rename became a BFF-owned `PATCH /bff/auth/azuretag` that writes the returned handle back into
the session. **Measured on the running stack immediately afterwards, a second route was still open,
and it needs no race and no double-submit:**

```
PATCH /api/users/me/azuretag   cookie only, no Authorization   -> 200 {"azureTag":"admin_probe1"}
  database                                                     -> admin_probe1
  GET /bff/auth/me                                             -> admin          <- stale
  GET /bff/auth/me   again                                     -> admin          <- still stale
```

That is one request through the proxy YARP still serves, with nothing but a session cookie, and the
staleness is permanent for the life of the session. It is verbatim the defect PR #100 was written to
remove, reached through a door that PR did not touch.

The residual ADR-0015 recorded — two concurrent renames landing their responses out of order — is
real, but it was never the important one. It needs a deliberate double-submit; this needs a single
request.

## Decision

**`/bff/auth/me` reads through to `GET /api/auth/me` and serves the cached block only when that read
cannot be completed.** The cache stops being an answer and becomes a degrade path.

1. **The API is asked on every `/me`.** The token comes from the existing re-mint helper, because
   this call bypasses the YARP transform exactly as the other out-of-band calls in that controller do.
2. **Every failure serves the cache** — an unreachable API, a non-2xx, an unparseable body, a null
   re-mint. Deliberately, and this is the load-bearing half: `authSlice` treats a rejected `getMe`
   as *not signed in*, so surfacing the failure would evict every logged-in user for the length of a
   backend hiccup. A name one rename out of date is strictly the lesser harm.
3. **`HasPin` is carried over from the cache, not read.** The API's `/api/auth/me` returns
   `UserResponse`, measured as `{userId, azureTag, email, firstName, lastName}` — no `HasPin`. It is
   BFF-owned state; rebuilding the block from the API alone would silently flip it to false and
   re-prompt a user who already set a PIN.
4. **A changed handle is written back**, so the fallback holds the most recent truth rather than
   decaying to the login-time value.

The rename endpoint keeps its cache write. It is no longer load-bearing for correctness, but it is
what makes the fallback fresh in the window between a rename and the next read.

## What this does to the concurrent-rename residual

It stops mattering, without being fixed. Two renames can still land out of order and leave the cache
holding the loser — but the only reader of that value now re-reads before serving it, so the next
successful `/me` replaces it with whatever the database actually holds. **Permanent staleness becomes
one read wide.**

## Two things ADR-0015 said, that were wrong

Recorded because the reasoning outlived the reasoning's author, and a wrong rejection is worse than
an open defect: it stops the next person from re-examining it.

- **"A per-session lock inside a singleton session service"** was named as the remedy and rejected.
  That placement fixes nothing. The race spans `await SendAsync` and the cache write, both in the
  controller; `SessionService.UpdateUserInfo` mutates the *stored reference* — `InMemoryTokenStore`
  hands back the live object, so its write-back is a no-op — meaning a lock there would guard a
  single reference assignment that is already atomic in .NET.
- **"Disproportionate"** was contradicted by code in the same assembly. `TokenRefresher` already
  ships the per-session single-flight gate — a `ConcurrentDictionary<string, SemaphoreSlim>`,
  `GetOrAdd` + `WaitAsync`, release in a `finally`, bounded timeout — and `Program.cs` registers it
  as a singleton *because* it owns that gate map. The mechanism was sixty lines away.

The lock is still not taken, but for the real reason: it would hold a gate across an outbound call
that has no `CancellationToken` and whose client sets no `Timeout`, so a hung API would serialise a
session's renames behind `HttpClient`'s 100-second default — and bounding it would abandon a
*committed* mutation, deterministically producing the very staleness this record removes.

## Alternatives considered

- **Compare-and-swap on the pre-call handle (rejected).** Correct in exactly the two interleavings
  where last-write-wins is wrong, and wrong in the two where it is right — a relabelling, not a fix.
  It also fails silently: `UpdateUserInfo` returns `void`, so a skipped write is unobservable.
- **An API-issued revision token (rejected).** Would order the writes properly, but it is a contract
  change — a new DTO field, a spec regen, mock and frontend types — and it still leaves the proxied
  door open, which is the defect that actually reproduces.
- **Blocking the proxied rename route at the BFF (rejected as insufficient).** It shuts the measured
  door but not the race, and the catch-all `/api/{**catch-all}` route means shutting it properly is
  more than deleting one entry. Reading through covers both without touching routing.
- **Invalidate-on-rename instead of read-through (rejected).** There is nothing to invalidate *into*:
  before this change `/me` had no path to the API at all, so a dirty flag would have had no reader.

## Consequences

**Positive** — `/me` cannot serve a handle the database disagrees with while the API is reachable,
by any route, including ones not yet written. The cache's remaining job is honest and narrow.

**Negative** — `/me` costs one upstream call plus a token re-mint, paid at app boot and on each
`Session` invalidation; it is not polled, so the volume is a handful per session. And the fallback
means a stale name is still *possible* while the API is down — it is now the documented degrade
rather than an unnoticed default.

## References

- ADR-0015 (the renameable handle and the residual this closes) · ADR-0019 (one error channel, and
  why a rejected `getMe` means signed-out) · ADR-0021 (the re-mint helper this reuses) ·
  ADR-0038 (the session as the only credential, which closed the other proxied-route defect).
