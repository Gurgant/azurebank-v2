# ADR-0026: The absolute session cap is re-authenticated, never extended

**Status**: Accepted

**Date**: 2026-07-30

**Decision Makers**: Vladislav Aleshaev

---

## Context

The BFF enforces two independent session deadlines: an **inactivity** window that slides on every
cookie-bearing request, and an **absolute** cap of `SessionCreated + AbsoluteTimeoutMinutes` that
never moves. `SessionExpiryWarning` used to offer the same two buttons on both branches, and on the
absolute branch one of them could not work: "Stay signed in" fires `/bff/auth/me`, which slides
`LastActivity` only. The copy beside it said the session "ends on a fixed schedule, whether or not
you are using it" — so the screen offered exactly what the sentence next to it called impossible.
Reproduced live 2026-07-27, and reproduced against the MSW mock too, so it was never a
missing-backend artifact.

Fixing the screen is code. What this ADR exists for is the part that leaves **nothing to read**: one
credential was researched and rejected, and one platform limitation forced a security-posture call
that a future reviewer will otherwise read as a bug.

## Decision

1. **Reaching the cap re-authenticates; it does not extend.** An absolute cap exists so a session
   cannot outlive its own start regardless of activity. A cap that can be pushed is decoration.
   Re-authentication mints a **different session** — new id, new cookie, new window, `AuthLevel`
   back to 1, no PIN elevation carried over. Same user, different object.

2. **The password is the credential. The PIN is rejected, and this is the part with no code to
   read.** In this app the PIN is auth level 2: a step-up *inside* an already-authenticated session,
   for money operations. Six digits with lockout is a sound second factor and an unsound sole
   credential for **creating** a session. Accepting it here would let a shoulder-surfed PIN mint
   sessions indefinitely — strictly worse than today's outcome, where the user is simply signed out.
   Proving the person at the keyboard still holds the password is precisely what the cap is there to
   re-establish.

3. **`POST /bff/auth/reauthenticate` takes a password and no identity.** The email comes from the
   server-side session. This is a construction rather than a check: re-authentication happens *in
   place*, with the route, the page and any half-filled form still mounted, so an endpoint that
   accepted an identity would let whoever is at the keyboard put their own session behind another
   person's screen.

4. **The old session is revoked LOCALLY and the API is never told — the old refresh token is
   deliberately left to expire.** The API exposes only `RevokeAllForUserAsync`; there is no
   single-token revoke, and `/api/auth/logout` calls it. That leaves no third option:
   - authenticate → mint → call the API's logout would revoke **every** token this user holds,
     including the pair minted moments earlier, so the replacement session would die silently at its
     first re-mint;
   - call it first, then authenticate, means a **mistyped password ends the session** it was meant
     to save.

   So the residue is accepted. Its only copy lived in the session record that is deleted in the same
   request, and it was never in the browser, so after the swap no party can present it.
   **Order is load-bearing: authenticate, mint, revoke locally.**

5. **The request is validated for length, NOT with `[Password]`.** That attribute enforces the
   complexity *pattern*, which is right when a password is chosen and wrong when one is verified: a
   wrong guess failing complexity would answer 400 where every other wrong guess answers 401, and a
   correct password set under an older policy would be rejected by validation — locking a user out
   of their own live session with a message about character classes.

## Consequences

**The absolute cap can now move, and one module was written on the premise that it could not.**
`sessionActivity.syncFromProbe` used to skip the cap entirely, on the stated grounds that it "is
fixed at session creation and never moves". True until re-authentication, which replaces the session
and therefore the cap.

The normal path learns the new window the way every window is learned: the mutation invalidates the
`Session` tag, `AuthBootstrap`'s live `getMe` subscription refetches, and `sessionMiddleware` learns
the policy from that response.

That was not sufficient, and the gap only appears once you ask what happens when the *refetch* fails.
The re-auth succeeds, the `getMe` that would teach the client the new window does not land, the
countdown runs on toward the OLD cap, and the confirming probe at the crossing — which can see the
new cap in `absoluteExpiresAt` — was refusing to look. Result: a user signed out seconds after
proving their password. So **`syncFromProbe` now reads the cap, and only ever moves it FORWARD**.
Monotonic is the safety half: a cap that can only move later cannot cause an early sign-out whatever
a stale, out-of-order or skewed response says, and the arithmetic stays between two server values
like the inactivity path beside it. The same line covers a second tab that re-authenticated while
this one sat idle.

Pinned directly in `sessionActivity.test.ts` rather than only through the dialog, because the
forward half is observable from the UI and the monotonic half is not: no legitimate server sequence
moves a live session's cap backwards, so without a unit test that guard is a line no test could tell
the difference about.

**A wrong password costs a message, not a session — but for a smaller reason than it appears.**
Measured, not assumed: a 401 whose `errorCode` is not `INVALID_CREDENTIALS` *does* dispatch
`sessionExpired()`, and the user still ends up authenticated seconds later, because the bootstrap
probe re-establishes the cookie that was never invalidated. What the D3 exemption actually buys is
never passing **through** `expired` — a transition that also runs `resetApiState()` and discards
every cached balance and account under whatever the user had open. The test asserts the status
*transitions*, not the final value, because the final value is the same either way.

**Forwarding the upstream error verbatim inherits the API's login semantics for free**: the generic
401, the `ACCOUNT_LOCKED` 429 with its `Retry-After`, and — per ADR-0012 — the property that a
locked account only reveals itself to a *correct* password, so the lock is never an oracle for a
guesser. The endpoint adds the BFF's `auth` rate-limit policy on top.

**Interim rejected.** Rendering only "Sign out now" on the absolute branch was considered on
2026-07-27 and declined: an honest screen that still cannot help is worth less than not fragmenting
this surface twice.

## Alternatives considered

- **Make the cap slide (delete it, in effect).** Rejected: it is the only rule that bounds how long a
  stolen-but-live session can be used regardless of how busy the attacker keeps it.
- **Re-use `POST /bff/auth/login` from the dialog.** Rejected on point 3: it accepts an identity.
- **PIN re-authentication.** Rejected on point 2.
- **Re-issue the same session id with a fresh `SessionCreated`.** Rejected: session fixation. A new
  authentication event gets a new identifier, which `CreateSession` guarantees by construction.
