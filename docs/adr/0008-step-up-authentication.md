# ADR-0008: Step-Up Authentication with PIN

**Status:** Accepted · **Date:** 2026-01-15 · **Amended:** 2026-08-12 (ADR-0040), 2026-08-13,
2026-09-04 and 2026-10-06 (ADR-0041), 2026-09-06 (ADR-0049), 2026-09-21 (ADR-0056), 2026-09-28
(ADR-0057) · **Decision Makers:** Vladislav Aleshaev

## Context

A sign-in proves who the user is, not that the user is present when money leaves an account or an
account changes, and financial regulation may ask for that second, recent proof. The proof has to
be quick to give, limited in time and simple to understand. The API is a stateless JWT service
with no session; the browser holds a BFF session cookie (ADR-0001). Guidance:
[NIST Digital Identity Guidelines](https://pages.nist.gov/800-63-3/), [OWASP Session Management](https://cheatsheetseries.owasp.org/cheatsheets/Session_Management_Cheat_Sheet.html).

## Decision

1. **A 6-digit PIN is the second proof: a session is at level 1 after the sign-in and at level 2
   after the PIN is verified**, because a PIN is fast, familiar and needs no app or outside service.
2. **The level is kept in the BFF session (`UserSession.AuthLevel`, `PinVerifiedAt`), not in the
   JWT**, because an elevation bound to one session cannot be replayed. The API has no level.
3. **The API alone verifies the PIN**, at `POST /api/auth/pin/verify`: the BFF forwards it and
   keeps the outcome, never the PIN, and every PIN check shares the API's attempt limit (ADR-0010).
4. **Level 2 lasts `Security:PinValidityMinutes` from the verification, 5 by default and 10 in the
   development settings, and using the app does not extend it**: the proof is limited in time.
5. **Expiry is lazy**: `SessionService.GetAuthLevel` lowers the level when the gate, `/bff/auth/me`
   or `/bff/auth/session-status` reads it past its window, and no timer revokes it at the deadline.
6. **`AuthLevelMiddleware` in the BFF is the step-up gate, before every proxied request.** It
   answers 404 to a proxied `/api/auth/*` entry point no browser has a reason to call; 401
   `AUTH_TOKEN_MISSING`, in the API's own body byte for byte, to `/api` or a path under it with no
   live session; and 403 `STEP_UP_REQUIRED` with `X-Auth-Level-Required` and `X-Auth-Level-Current`
   to a live session at level 1 on a level-2 path. The 401 comes first because the SPA sends a 401
   to the sign-in and opens the PIN prompt on a 403, which nobody without a session can answer.
7. **One path is behind level 2, `GET /api/accounts/{id}/full-number`** (ADR-0020); an operation
   that changes state proves the PIN at the API with an authorisation bound to it and spent once,
   because the API has no level and a session flag protects nothing on a call made straight to it.
8. **Protected Operations**: the level each operation needs, and what enforces it.

   | Operation | Level | Enforced by |
   |---|---|---|
   | View accounts and transactions (read-only), update account (non-financial) | 1 | The session |
   | Deposit (money in, low risk) | 1 | The session |
   | Reveal full account number | 2 | The BFF gate of decision 6 (ADR-0020) |
   | Withdraw (money out) | 2 | An API authorisation in `Step-Up-Authorization` (ADR-0056) |
   | Transfer (money out), internal transfer (account modification) | 2 | An API authorisation in the same header (ADR-0042) |
   | Delete account (destructive) | 2 | An API authorisation in the same header (ADR-0049) |

9. **Only an Argon2id hash of the PIN is stored** (`ApplicationUser.PinHash`, `nvarchar(200)`),
   never the PIN (ADR-0011); passwords are hashed by Identity's PBKDF2, not this way.
10. **A signed-in user enrols a PIN, what needs one is refused until then, and a change requires
    the current PIN** (ADR-0040), because a session that could replace it would pass every gate.

## Rejected

- Rejected: entering the password again, because it is slow before every sensitive operation.
- Rejected: TOTP, because it needs an authenticator app and adds friction to every operation.
- Rejected: biometrics, because they are platform-specific and mobile only.
- Rejected: an e-mail or SMS code, because it is slow, external and only as safe as the mailbox.
- Rejected: a level attribute on the API's controllers, because the API has no session to read.

## Consequences

- A sensitive operation costs one short PIN entry on any platform, and the user has one more
  credential to remember, with enrolment and change flows of its own.
- Not covered: a 6-digit PIN is weaker than a TOTP code, which can be added later as an option.
- Not covered: the level exists in the BFF only; ADR-0055 keeps a caller from going round it.
- Not covered: the gate logs only what it refuses (`StepUpRequired` and `StepUpWithoutSession`
  among its events), and an elevation is logged with no link to the request it then unlocks.
- Not covered: the 403 is the BFF's own shape, so a level-1 session can tell which path is gated.
- Not covered: closing an account is a soft delete its holder cannot undo: the account answers
  404 `ACCOUNT_NOT_FOUND`, its transactions leave the history, no endpoint restores it (ADR-0049).

## Verified by

- `AuthLevelMiddlewareTests`: the 404, the 401 and the 403, and the gate closing again after expiry.
- `PinServiceTests`, `PinReplacementTests`: the API's verification, and a change needing the PIN.

## Related

ADR-0001, ADR-0003, ADR-0010, ADR-0011, ADR-0020, ADR-0022, ADR-0038, ADR-0040, ADR-0041, ADR-0042,
ADR-0049, ADR-0055, ADR-0056, ADR-0057.
