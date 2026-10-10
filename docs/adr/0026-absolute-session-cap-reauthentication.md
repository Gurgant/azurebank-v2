# ADR-0026: The absolute session cap is re-authenticated, never extended

**Status:** Accepted · **Date:** 2026-07-30 · **Amended:** 2026-09-28 (item 4, ADR-0057),
2026-10-05 (item 2, ADR-0063) · **Decision Makers:** Vladislav Aleshaev

## Context

The BFF enforces two independent session deadlines: an inactivity window that slides on every
cookie-bearing request, and an absolute cap of `SessionCreated + AbsoluteTimeoutMinutes` that never
moves. "Stay signed in" fires `/bff/auth/me`, which slides `LastActivity` only, so at the absolute
cap that button cannot work: the screen offers what the sentence beside it calls impossible. Two
parts of the answer leave nothing to read in the code, a credential that is rejected and what
becomes of the old session, and a later reader would otherwise take them for bugs.

## Decision

1. **Reaching the cap re-authenticates; it does not extend**, because a cap exists so that a
   session cannot outlive its own start whatever the activity, and one that can be pushed is
   decoration. Re-authentication mints a different session: new id, new cookie, new window,
   `AuthLevel` back to 1, no PIN elevation carried over. Same user, different object.
2. **The password is the credential; the PIN is rejected**, because the PIN is auth level 2, a
   step-up inside an authenticated session: six digits with lockout is a sound second factor and
   an unsound sole credential for creating a session, and a shoulder-surfed PIN would mint
   sessions indefinitely. On the public demo, for the owner of a claimed copy on the browser that
   keeps its sign-in details, the dialog has one button, "Stay signed in", and the password sent is
   the one that browser keeps: the credential and the endpoint are unchanged, and what the cap
   re-establishes there is that the browser still holds the copy's password, not that a person
   typed it (ADR-0063, "What the browser keeps in demo mode", point 4).
3. **`POST /bff/auth/reauthenticate` takes a password and no identity**: the email comes from the
   server-side session, because re-authentication happens in place, with the page and any
   half-filled form still mounted, and an endpoint that accepted an identity would let whoever is
   at the keyboard put their own session behind another person's screen.
4. **Order is load-bearing: authenticate, mint, set the new cookie, end the old session, queue its
   grant**, because nothing may end before authentication succeeds: a mistyped password ends
   nothing. The old session ends as a sign-out of one session ("Esci") does, and `GrantRevoker`
   revokes its one grant at the API through `POST /api/auth/revoke` after any renewal it had in
   flight (ADR-0057, §4.6). The API's logout is never called here, because it revokes every grant
   the user holds, the replacement session's included.
5. **The request is validated for length, not with `[Password]`**, because that attribute enforces
   the complexity pattern, which is right when a password is chosen and wrong when one is
   verified: a wrong guess failing complexity would answer 400 where every other wrong guess
   answers 401, and a correct password set under an older policy would be refused by validation.

## Rejected

- Rejected: a cap that slides, because the cap is the only rule that bounds how long a stolen but
  live session can be used, however busy the attacker keeps it.
- Rejected: `POST /bff/auth/login` from the dialog, on point 3: it accepts an identity.
- Rejected: PIN re-authentication, on point 2.
- Rejected: the same session id with a fresh `SessionCreated`, because that is session fixation: a
  new authentication event gets a new identifier, which `CreateSession` guarantees by construction.
- Rejected: "Sign out now" as the only button at the cap, because a screen that is honest and
  still cannot help is not worth changing this surface twice.

## Consequences

- The absolute cap can move, and the client follows it: the mutation invalidates the `Session`
  tag, the live `getMe` subscription of `AuthBootstrap` refetches, and `sessionMiddleware` learns
  the policy from that response.
- `sessionActivity.syncFromProbe` also reads the cap from a `session-status` probe, and only ever
  moves it forward. Without it, a refetch that fails leaves the countdown running to the old cap
  and signs the user out seconds after the password was proved. Forward only, because a cap that
  can only move later cannot cause an early sign-out, whatever a stale, out-of-order or skewed
  response says. The same line covers a second tab that re-authenticated while this one sat idle.
- A wrong password costs a message, not a session: a 401 `INVALID_CREDENTIALS` is exempt from the
  sign-out a 401 otherwise triggers, so the client never passes through `expired`, a transition
  that runs `resetApiState()` and discards every cached balance and account.
- The upstream error is forwarded verbatim, which inherits the API's login semantics: the generic
  401, the `ACCOUNT_LOCKED` 429 with its `Retry-After`, and a lock that reveals itself only to a
  correct password (ADR-0012). The endpoint adds the BFF's `auth` rate-limit policy.

## Verified by

- `ReauthenticateTests`: `AWrongPasswordLeavesTheSessionAliveAndStillUsable`,
  `TheCallerCannotChooseWhoToSignInAs`, `ItRevokesOnlyTheOldGrant_AndNeverCallsTheApiLogout`.
- `sessionActivity.test.ts`: the cap moves forward and never backwards.

## Related

ADR-0012, ADR-0057, ADR-0063.
