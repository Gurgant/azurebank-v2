# ADR-0057: The BFF's refresh token is one reusable grant per session

**Status:** Accepted · **Date:** 2026-09-28 · **Decision Makers:** Vladislav Aleshaev ·
**Supersedes** the rotation in [ADR-0021](0021-refresh-token-rotation-bff-remint.md) (its decisions
3, 5 and 6, four rows of its table and its PR-2 failure policy, and every other clause of it that
no longer holds, struck in place there) and the decision in
[ADR-0034](0034-failed-family-revoke-recovery.md), whose record of the SQL errors EF retries stays ·
**Amends** [ADR-0026](0026-absolute-session-cap-reauthentication.md) item 4, the event inventory of
[ADR-0044](0044-the-audit-trail-is-append-only-and-chained.md), and
[ADR-0055](0055-the-api-serves-one-client-the-bff.md) D4 and D7

**Where the code's citations point.** This design was ratified on 2026-09-28 as a plan in the
working-state repository, `azurebank-work/plans/2026-09-27-stage-c/06-SESSION-DESIGN.md`, and the
code and tests cite it by that plan's numbering: "06 §4.5", "06 F3", "06 §10 O2h". This record keeps
the numbering, so each of those citations lands here. §1 to §11 below are the plan's sections of the
same number; §8 and §9, which listed the code and the tests to change, are summaries, and the pull
request holds the rest. §5.4, which points to the incident runbook, is this record's own. F1 to
F15 are the red team's findings, listed at the end. O0 to O2 are the oracles in §10.

## Preconditions

Two things this decision rests on that no code in the repository can enforce. Breaking either one
breaks the argument in §3.

1. **The API stays on loopback.** In `compose.yaml` the API shares the BFF's network namespace
   (`network_mode: "service:bff"`) and listens on `http://127.0.0.1:5068`, and the Azure plan runs it
   the same way: one app, the API as a sidecar of the BFF. Since this decision the token endpoints
   also refuse every address that is not loopback (§4.2). **If the API is ever reached over a
   network** (the plan's old fallback, the API as its own app with internal ingress over https, is
   one way) **stop: this decision's precondition has failed, and DPoP (RFC 9449) or mutual TLS
   (RFC 8705) comes first** (F1).
2. **PR-1 lands before the first Azure deployment (the plan's step C2).** Its migration revokes
   every active grant with the reason `Deployment` (§4.1). The revision before PR-1 reads a revoked
   token that has no successor as reuse, and revokes every token of its user. Once a deployment holds
   live sessions, a swap between the two revisions would do that to each of them (F14).

## 1. The problem

**Measured** with the outage harness (`azurebank-work/plans/2026-09-27-stage-c/05-DB-OUTAGE.md`): in
8 of 8 runs where a renewal met a 10 s database hang, someone was signed out. In 4 of those 8 every
session of the user was signed out, and a false `RefreshTokenReuse` was written. A larger retry
budget changed nothing. Money was safe: 3 of 3 transfers were debited once.

How it happened, read in the code at main `f949b18`:

1. The BFF waited 5 s for the renewal, then kept the old token (`TokenRefresher`).
2. The API still committed the rotation, 9.4 to 9.5 s after the request (measured). Its refresh took
   no cancellation, so nothing stopped the save.
3. The next renewal presented the replaced token. Within 10 s the API answered 401, and the BFF
   ended the session on any 401. After 10 s the API also revoked every token of the user
   (`RefreshTokenService`).

**Four more roads to the same sign-out, read and not measured:**

- EF retries a commit whose outcome is unknown "as if the transaction was rolled back"
  (<https://learn.microsoft.com/en-us/ef/core/miscellaneous/connection-resiliency>). So the API could
  refuse a rotation it had committed.
- When renewal kept failing, the BFF forwarded the expired token. The API answered 401
  `AUTH_TOKEN_EXPIRED`, because its clock skew is zero, and the SPA reads any such 401 as an expired
  session.
- A half-applied service-key rotation answers 401 `SERVICE_CREDENTIAL_REQUIRED`. The renewal path
  ended the BFF session on it, and proxied calls passed it to the SPA, which signed the user out:
  through YARP, and through `ForwardUpstreamError` in verify-pin, set-pin and rename.
- Every "Esci" wrote a false `RefreshTokenReuse` as soon as another session of the same user renewed.
  Logout revoked without a successor, and the code read that as reuse.

**Wrong scope, read in the code:**

- "Esci" revoked every token of the user, on every device.
- Each rotation added another 7 days, so the API never capped a chain.
- The proxy adds the service key to every browser request. A single path check kept browsers off
  `/api/auth/refresh`, and `/api/auth/logout` was not blocked at all.

## 2. Options compared

| Option | Security | Reliability | Simplicity | Standards |
|---|---|---|---|---|
| **B.** Rotation plus a time grace window | Anyone holding the old token gets tokens during the window. The window is stamped before the commit, so it can be shorter than the retry chain | Fixes only losses shorter than the window; late answers race retries | Adds code | Ory: "This is a workaround, not a best practice" (<https://www.ory.com/docs/hydra/guides/graceful-token-refresh>); FAPI 2.0: extraordinary circumstances only |
| **A.** Just stop rotating | Loses fork detection; keeps the 7-day lifetime and the sign-out-everywhere "Esci" | Fixes the lost answer | Simplest | Right on rotation; departs from the BCP's advice on lifetime (RFC 10017 §6.1.2.2, "it makes sense") |
| **P6.** The BFF names the next token | Keeps fork detection with no window; a BFF bug becomes a false fork | Fixes it, with pending state in the BFF | A new protocol and state machine: the most code | No vendor ships it |
| **SC.** A credential derived from the cookie, plus resume after a restart | The cookie becomes a credential the API honours after a BFF restart; no theft verdict | Best: also survives a restart | A new resume path and SPA state | Not OAuth-shaped |
| **G. Chosen: one reusable grant per session** | A copy of a live grant works unseen until its session ends (at most 60 min), and only from a loopback socket inside the replica. An ended grant presented again trips a tripwire | Fixes every road in §1: a renewal writes nothing | Deletes the rotation; adds a column, an endpoint and a revoke queue (with the stamp of §5.3, a second column and endpoint and a watcher) | FAPI 2.0, Duende BFF, Okta for web apps, RFC 10017 §6.1.2.2 |

A judge chose G from four designs, weighing security, reliability, simplicity and standards. A red
team then attacked it, and every finding (F1 to F15) was applied before ratification.

## 3. Decision and why

**The refresh token (the "grant") stays, and stops rotating. It lives only as long as its session,
and it is revoked one session at a time.**

**Why not keep rotation.**

- To present a grant you need three things: the grant, the service key, and a socket on the API's
  loopback interface. The API binds loopback (first precondition), and its token endpoints refuse
  every other address (§4.2). Only code inside the replica has all three, and that code can already
  read every grant and every access token from memory.
- Rotation would catch only a careless attacker of that kind, and revoking one user's tokens would
  not contain them. Rotating the service key does. OAuth 2.1 (draft 16, §3.2.1): "Changing a single
  set of client credentials is significantly faster than revoking an entire set of refresh tokens"
  (<https://www.ietf.org/archive/id/draft-ietf-oauth-v2-1-16.txt>).
- The argument needs the API on loopback, which is why that is a precondition and not a detail.

**What the standards say.** Each quotation below was fetched again and matched on 2026-09-28.

- RFC 9449 §5: a confidential client's refresh tokens "are sender-constrained by way of the client
  identifier and the associated authentication requirement"
  (<https://www.rfc-editor.org/rfc/rfc9449.txt>).
- FAPI 2.0 Security Profile §5.3.2.1, item 9: the authorization server "shall not use refresh token
  rotation except in extraordinary circumstances"
  (<https://openid.net/specs/fapi-security-profile-2_0-final.html>).
  - Its Note 1 says rotation "does not provide security benefits when used with confidential clients
    and sender-constrained access tokens", with FAPI's client authentication (mTLS or
    `private_key_jwt`). AzureBank has neither, so this decision rests on the loopback argument above,
    not on that sentence.
  - The note's other half fits exactly: rotation "causes user experience degradation and operational
    issues whenever the client fails to store or receive the new refresh token".
- RFC 9700 §2.2.2 makes rotation mandatory only for public clients: "Refresh tokens for public
  clients MUST be sender-constrained or use refresh token rotation"
  (<https://www.rfc-editor.org/rfc/rfc9700.html>).
- RFC 10017 §6.1.2.2 (<https://www.rfc-editor.org/rfc/rfc10017.txt>):
  - it "makes sense" to make the BFF session as long as the refresh token's maximum lifetime, and to
    end the session when the refresh token is no longer valid. That is advice, not a requirement;
  - BFFs may also renew "when they observe a token expiration event".
- Duende BFF: "Because the BFF is a confidential client, it does not need one-time use refresh
  tokens" (<https://docs.duendesoftware.com/bff/fundamentals/tokens/>).
- Okta: "Mobile apps and web apps use persistent refresh token behavior as the default"
  (<https://developer.okta.com/docs/guides/refresh-tokens/main/>).

**What this gives up, compared with rotation (F8).**

- **Under rotation:** once a copied live refresh token was used, the copy was detected at the BFF's
  next legitimate renewal, within about 15 minutes, and every token of the user was revoked. A
  patient thief who waited for the session to end was not detected, and renewed 7 days at a time.
  (Read in the code, not tested.)
- **Under this decision:** the copy works silently until its session ends, at most 60 minutes. Used
  after that, it trips the tripwire (§4.3).
- **Partial cover:** a renewal-rate detector that writes nothing (§6, anomaly 2).

**Runner-up: P6.** It adds only an alarm for a careless attacker inside the replica, and it is a
protocol nobody else ships.

**What this record does not claim.** AzureBank is not FAPI-conformant. It signs users in with a
password through the BFF, it has one shared client key, and its access tokens are bearer tokens,
not sender-constrained ones. The standards above are cited for what they say about rotation, not as
a profile this system meets.

## 4. Behaviour

### 4.1 The grant

- 256 bits from a CSPRNG, stored only as a SHA-256 hash, as before.
- `ExpiresAt` is the issue time plus `Jwt:RefreshTokenLifetimeMinutes`: fixed at issue and never
  extended.
  - The default is **60 minutes**, the BFF's absolute session cap (`Session:AbsoluteTimeoutMinutes`).
  - The API refuses to start unless it is between **15 and 1440**, and not below
    `Jwt:ExpirationMinutes`. The sign-in's access token is minted just before its grant and is not
    capped by it, so only this rule keeps it from outliving the grant (F11).
  - It replaces `Jwt:RefreshTokenExpirationDays`, which was 7.
- A new column, `RevokedReason`, holds one of five names, and a CHECK constraint refuses any other:
  - `SessionEnded`: the BFF ended the session that held it;
  - `SignOutEverywhere`: every grant of the user, through `POST /api/auth/logout` or a runbook's SQL;
  - `ReuseContainment`: the operator's answer to a tripwire row (§5.4);
  - `Incident`: the nuclear lever (§5);
  - `Deployment`: set by the migration on every grant still active (not revoked and not expired)
    when it ran. Those were issued for seven days; the second precondition exists because of them.
  - A null reason marks a legacy row, revoked by rotation or by a sign-out before the column existed.
- Login and register also return `refreshTokenExpiresAt`. The BFF stores
  `AbsoluteExpiresAt = min(SessionCreated + 60 min, refreshTokenExpiresAt)` when it creates the
  session (F5).
  - That one field feeds the session's validity check, `/bff/auth/me` and
    `/bff/auth/session-status`.
  - The grant is minted before the session, so the grant sets the cap, earlier by at most the
    sign-in's duration: up to 74.4 s on the 10 × 10 s retry budget (measured in the outage harness).
  - If a registration's best-effort grant failed, the session keeps `SessionCreated + 60 min` and the
    old hard stop at its access token's expiry (F15).
- Access tokens: `exp = min(now + 15 min, the grant's ExpiresAt)`. Nothing from one sign-in
  outlives its grant: sign-in plus `Jwt:RefreshTokenLifetimeMinutes`, 60 minutes by default.
- Every "60 minutes" in this record is a default: `Jwt:RefreshTokenLifetimeMinutes` at the API and
  `Session:AbsoluteTimeoutMinutes` at the BFF are both 60 unless a deployment sets them.

### 4.2 The token endpoints answer only the BFF's own client, over loopback

- The five token endpoints are login, register, refresh, revoke and logout. The stamp feed of §5.3
  gets the same rule when it is built.
- The API answers **404**, the answer an unknown path gets, unless `Connection.RemoteIpAddress` is
  loopback (an IPv4 address mapped into IPv6 counts). A null address is accepted only where the test
  host sets an explicit option, which no configuration can set (F1).
- Loopback alone is not enough, because the BFF's proxy also reaches the API over loopback. So:
  - the BFF's own client (`ServiceCredentialHandler`) adds `X-AzureBank-Token-Road`;
  - the YARP transform strips any copy a browser sends, next to where it strips the service key;
  - the API requires exactly one value, as it does for the key (F15).
- `/api/auth/revoke` and `/api/auth/logout` also join the BFF's blocked proxied paths, beside login,
  register and refresh.
- The check is a middleware on endpoint metadata (`TokenEndpointAttribute`, `TokenRoadMiddleware`).
  It runs after the service-key check, so a caller without the key still gets that check's 401, and
  before authentication and model binding.

### 4.3 The API: `POST /api/auth/refresh`

- The first middleware in the pipeline stamps `ReceivedAt` as the process's start time in UTC plus a
  `Stopwatch` (`ReceivedAtClock`). A wall-clock step cannot then reorder two stamps from one process
  (F10).
- Every refusal is the same 401 `REFRESH_TOKEN_INVALID` (ADR-0021 decision 4, kept).
- "Revoked" is checked before "expired", as before.

| Grant state | Answer | Writes | Event |
|---|---|---|---|
| Unknown | 401 | an audit refusal | `RefreshTokenUnknown` (unchanged) |
| Revoked `SessionEnded`, request received **after** the revoke: **the tripwire** | 401 | the audit row only, written with `CancellationToken.None`; **no revoke** (F3) | `RefreshTokenReuse` security event |
| Revoked `SessionEnded`, request received at or before the revoke | 401 | none | Information: revoked while its renewal was in flight |
| Revoked for any other reason, or a legacy row with no reason | 401 | none | Information naming the reason; Warning for `Incident` and `ReuseContainment` |
| Expired, or less than 1 s of life left | 401 | none | Information |
| Active | 200 `{accessToken, expiresAt}`, with no refresh token in the body | **none** | the existing "Refreshed access token" line; the detector's counter once it is built (§6) |

- **The tripwire records; it does not revoke (F3).**
  - Only code inside the replica can trip it (§3), and revoking one user's grants does not contain
    that code. The incident runbook does (§5.4).
  - Any innocent trigger that remains (a bug, a clock or dispatch stall) now costs one audit row,
    never a sign-out of all the user's sessions.
  - **If its audit write fails,** the tripwire answers as the unknown-grant refusal always has: the
    exception surfaces and `GlobalExceptionHandler` turns it into a **500** (ADR-0044's loud
    failure). Like the unknown-grant refusal, it departs from the uniform 401 only while the audit
    write fails. The `RefreshTokenReuse` log line comes before the audit write, so it is still
    written. Nothing is revoked or issued either way, and the grant is already revoked, so a retry
    only tries the audit write again.
- **Where the event lands.** On Azure the plan sends application logs nowhere, so the event is in
  the audit trail, which a SQL query or the audit verifier reads. It is not a push notification.
  Pushing it (an alert on the audit table, or turning logs on) is a later cost decision.

### 4.4 Revoke, and sign out everywhere

- `POST /api/auth/revoke {refreshTokens}` takes one grant, or up to 1000 for the shutdown drain
  (§4.6).
  - It is `[AllowAnonymous]`, because the grant is the credential. The key, the marker and loopback
    still apply. A null in the list names no grant, and the request is refused with 400 before
    anything is revoked.
  - It runs one `UPDATE … SET RevokedAt = ReceivedAt, RevokedReason = 'SessionEnded' WHERE TokenHash
    IN (…) AND RevokedAt IS NULL`.
  - It answers 200 for revoked and unknown grants alike (RFC 7009 §2.2: "The authorization server
    responds with HTTP status code 200 if the token has been revoked successfully or if the client
    submitted an invalid token", <https://www.rfc-editor.org/rfc/rfc7009.txt>).
  - It answers **503** with `Retry-After: 5` when the database fails, so the client retries
    (RFC 7009 §2.2.1) (F12).
  - Repeating it changes nothing, so an EF retry is harmless.
- `POST /api/auth/logout` keeps its effect: it revokes every grant of the user, now with the reason
  `SignOutEverywhere`. When the stamp of §5.3 is built, it also adds 1 to the user's stamp. Nothing
  in the app calls it, and no button leads to it; the Bruno collection does.

### 4.5 BFF renewal

- The thresholds come from the token's own lifetime, L = exp − iat (F6). For 15-minute tokens they
  are 7.5 minutes and 60 s; for 2-minute tokens, 60 s and 30 s.
  - **More than L/2 left:** use the token.
  - **Between min(60 s, L/4) and L/2 left:** use the token, and start a background renewal.
  - **min(60 s, L/4) or less left, or already expired:** join the renewal in flight or start one, and
    wait at most 5 s.
    - A renewal still pending at 5 s counts as a transient failure (F15).
    - On a transient failure, use the old token if it has more than 5 s left. Otherwise answer
      **503** `SERVICE_UNAVAILABLE` with `Retry-After`.
    - Never forward an expired token.
- A renewal starts, in either branch, only when all of these hold (F6): the session has not ended; no
  renewal is in flight; none failed in the last 15 s; and the grant outlives the held token by at
  least 1 s. Once a renewal returns an expiry no later than the one held, the session stops renewing
  for good.
- **Single flight lives on the session (F2).** `UserSession` carries `Ended` and `InFlightRenewal`,
  guarded by one lock on the session object. Every renewal checks `Ended` and registers itself under
  that lock. This replaced a map of semaphores that dropped a session's entry, so a second semaphore
  could appear beside the first.
- Every renewal, foreground or background, runs detached with its own 30 s timeout and never takes the
  caller's cancellation (F15). This is MSAL's `refresh_in` pattern
  (<https://learn.microsoft.com/en-us/entra/msal/dotnet/advanced/high-availability>).
- **Only a 401 whose `errorCode` is `REFRESH_TOKEN_INVALID` ends the session.** A refused key, any
  other 401, a 5xx, a timeout, a network error or an unreadable body all keep it.
- **What gets stored:** the access token and its expiry, and only if the new expiry is later. The
  grant is never overwritten. A result that arrives for an ended session is dropped.

### 4.6 Ending a session

- **The store no longer brings removed sessions back (F2).** A write-back replaces only an entry
  that is still there (`TryUpdate`), so a request that read a session before "Esci" cannot restore it
  after.
- **"Esci":**
  1. Under the session lock, mark it `Ended`, capture `InFlightRenewal` and remove the session.
  2. Delete the cookie and answer 200 at once. The access-token re-mint logout used to do is gone.
  3. Queue the grant, with the captured renewal, on `GrantRevoker`.
  - The user's other sessions are not touched.
- **`GrantRevoker`** is a bounded channel of 1000 items, worked by 4 items in parallel (F12). Both
  numbers are choices, not measurements.
  - Each item waits for its captured renewal (30 s at most), then calls `/api/auth/revoke` with a
    5 s timeout.
  - It retries a 5xx, a timeout, a network error or a refused service key, with backoff from 1 s
    doubling to 30 s, until the grant expires; then it logs `GrantRevokeAbandoned`. Any other refusal
    is final and is logged the same way.
  - When the channel is full, it drops the item and logs `GrantRevokeDropped`.
- **The same path ends these sessions too:**
  - idle expiry and the absolute cap, on a read and in the 5-minute sweep;
  - re-authentication, in ADR-0026's order: authenticate, mint, set the new cookie, end the old
    session, queue its grant;
  - a session whose grant the API calls invalid;
  - an old session whose cookie arrives with a new sign-in or registration: that session now ends
    (F13);
  - a stamp change, once the stamp of §5.3 is built.
- **Graceful stop (F7).** When the host stops, every session is marked `Ended`, and every held and
  queued grant goes to the API in one `/api/auth/revoke` call (split into calls of 1000 beyond that)
  within the shutdown grace period. The drain waits at most 5 s for renewals caught in flight, and
  the log says how many grants were revoked and how many were left.
  - This matters because the replica scales to zero about 5 minutes after its last request (the
    platform's 300 s scale-down wait), before the 15-minute idle expiry would end those sessions.
  - A kill (SIGKILL) leaves those grants alive, with no holder, until their cap. That is a residual.

### 4.7 Key refusals on proxied calls (F4)

- When the API refuses the service key with 401 `SERVICE_CREDENTIAL_REQUIRED`, it adds the header
  `X-AzureBank-Refusal: service-credential`.
- A YARP response transform and `ForwardUpstreamError` turn that response into a 503 with
  `Retry-After`, carrying nothing of the API's refusal, so the SPA stays signed in. Renewal already
  treats the refusal as a transient failure (§4.5).

### 4.8 Inactivity timeout: 15 minutes

The BFF's inactivity timeout is 15 minutes (it was 30). The absolute limit stays 60 minutes.

- PSD2's RTS, art. 4(3)(d): "the maximum time without activity by the payer after being
  authenticated for accessing its payment account online shall not exceed 5 minutes"
  (<https://eur-lex.europa.eu/legal-content/EN/TXT/?uri=CELEX:32018R0389>, read through the
  legislation.gov.uk copy, <https://www.legislation.gov.uk/eur/2018/389/article/4>).
- OWASP: "Common idle timeouts ranges are 2-5 minutes for high-value applications"
  (<https://cheatsheetseries.owasp.org/cheatsheets/Session_Management_Cheat_Sheet.html>).
- Demo visitors read code between clicks, so 5 minutes would sign them out mid-page. 15 minutes halves
  the time an unattended browser stays signed in.
- **The gap to PSD2's 5 minutes is a deviation this demo accepts, and this record is where it is
  stated.** The SPA reads the policy from `/bff/auth/me`, so closing the gap is a configuration
  change.

## 5. Levers to sign people out

| Lever | How it works | Effect on a signed-in browser | Code |
|---|---|---|---|
| **One session now: "Esci"** | Under the session lock, the BFF marks the session `Ended`, removes it, deletes the cookie and answers 200. `GrantRevoker` then revokes that grant (`SessionEnded`), after any renewal in flight | Signed out at once: the next request with that cookie, from any tab, gets 401. Its access token lived only in that session and goes with it | The core of PR-1 (§4.6) |
| **One user everywhere now** | `POST /api/auth/logout` (no button yet), or the runbook's SQL for one user (§5.4). All the user's grants are revoked (`SignOutEverywhere`, or `ReuseContainment` when it answers a tripwire); with the stamp, in the same transaction, the user's stamp goes up by 1 | With the stamp: within 15 s, at the next request (§5.3). Without it: at each session's next renewal, after up to half the token's life (7.5 minutes) of continued use | The stamp is decided and not yet built (§5.3); without it, nothing beyond the revoke |
| **Everyone now** | Restart the revision: the portal's Restart, or `az containerapp revision restart` (<https://learn.microsoft.com/en-us/cli/azure/containerapp/revision#az-containerapp-revision-restart>). Stopping and starting the app also works, with downtime | Signed out at the next request, after a cold start, because no session exists any more (§5.2). The access tokens die with the process memory | None. On a graceful stop the drain of §4.6 also revokes the grants |
| **Nuclear** | One revision rotates `Jwt:Secret` and `ServiceCredential:BffKey` together (a key rotation is already "a deployment event on both sides", ADR-0055 D4). Then SQL sets `Incident` on every live grant created before that revision | Signed out at the next request, since a new revision is a restart. Every access token ever minted fails its signature check. Every grant is useless without the new key, and is revoked | None: the runbook in §5.4 |

### 5.1 How fast: the BFF session against the access token

- The browser never holds a token. It holds a cookie that names a BFF session, and the 15-minute
  access token lives inside that session. So a lever takes effect when the **BFF session** ends.
- An access token can outlive its session: it stays valid at the API for up to 15 minutes. But only
  code inside the replica could present it (§3).
- Only the nuclear lever also kills those leftover access tokens.

### 5.2 Why a restart covers "everyone now"

- Sessions live only in one `ConcurrentDictionary` in the BFF process (`InMemoryTokenStore`), whose
  own documentation says sessions are lost on restart.
- The cookie is an opaque 256-bit random handle, not a signed token. Once the dictionary is gone, the
  cookie proves nothing.
- The plan runs at most one replica, in single-revision mode, so no other process holds a copy of the
  sessions.
- This stops being true the day sessions move to a shared store or to two replicas. "Everyone now"
  would then need a global stamp: the same mechanism as §5.3 with one extra row.

### 5.3 The security stamp: decided, not yet built

The stamp is decided as its own commit of PR-1, so that review can drop it. At the time of writing
(2026-09-28) it is not in the code, and neither `SessionStamp` nor the watcher exists.

- **What:** a per-user counter, `SessionStamp`, in a new column of `AspNetUsers`. It is not
  Identity's own `SecurityStamp`, which has existed since `InitialCreate` and keeps its Identity
  meaning.
- **Writes:** sign-in, registration and re-authentication return the current value, and the BFF
  stores it on the session. Every per-user sign-out adds 1, in the same transaction as its revoke.
- **Check:** `SessionStampWatcher`, a BFF hosted service, calls `POST /api/auth/session-stamps
  {userIds}` every 15 s for the users that hold sessions, and makes no call when nobody does. The
  endpoint gets the loopback, key and marker rule of §4.2. The session validity check refuses a
  session whose stamp is below the latest known value, and that session then ends by the path of
  §4.6. A sign-out that starts inside the BFF updates the watcher's map at once.
- **If the watcher's call fails,** the map keeps its last values. The lever itself needed the
  database and revoked the grants, so those sessions still end at their next successful renewal.
- **Why it is worth building:** it is the only way to sign one user out now without signing everyone
  else out, and it gives the operator a precise answer to a tripwire row now that the automatic
  containment is gone (F3).
- **Cost (estimated):** one column, one endpoint, one hosted service and one check, about 150 lines
  and 3 tests; one primary-key query every 15 s while someone is signed in, and none otherwise.
- **Without it:** a per-user sign-out takes up to 7.5 minutes to reach the browser. To make it happen
  now, restart, which signs everyone else out too.

### 5.4 Runbook: a tripwire row, and the nuclear lever

The steps, with their SQL, are in
[`docs/runbooks/refresh-token-reuse-recorded.md`](../runbooks/refresh-token-reuse-recorded.md),
where CI parses every SQL block a runbook holds and binds it to the schema
(`RunbookSqlParsesSqlServerTests`, `RunbookSqlBindsSqlServerTests`); SQL kept in a record is SQL
nobody checks. What that runbook rests on is decided here.

- **Where the event is.** On Azure, in the audit trail only (§4.3). The row's `ActorUserId` is the
  user and its `SubjectId` the grant's row in `RefreshTokens`. A row written before PR-1 was
  deployed means something else: rotation's reuse detection, which had already revoked every token
  of that user by itself.
- **What a row can mean.** The legitimate BFF cannot send a renewal after it ended the session (§6,
  anomaly 1), and nobody outside the replica can present a grant (§3). So a row is either something
  innocent that costs this row and nothing else — the residual in §7, a renewal the API stamped
  more than 30 s after the BFF sent it, or a bug — or code inside the replica. The row cannot tell
  them apart, and nothing automatic acts on it (F3). The operator picks a lever.
- **The user's sessions:** revoke every live grant of that user, with the reason
  `ReuseContainment`. Each session then ends at its next renewal, after up to 7.5 minutes of
  continued use (§5). Once the stamp of §5.3 is built, that revoke and a raise of the user's stamp
  belong in one transaction.
- **Everyone's sessions:** restart the revision (§5, "Everyone now").
- **The nuclear lever, when code inside the replica is suspected** (§7: no token scheme can see
  it): rotate `Jwt:Secret` and `ServiceCredential:BffKey` together in one revision, which is a
  restart, then set `Incident` on every live grant created before that revision answered. None of
  them can be presented without the new key; the revoke makes it certain. A grant created after
  that belongs to a session that began after the restart, and revoking it would sign that session
  out again at its next renewal, under the Warning the API keeps for `Incident`.
- **A statement run by hand is not audited:** nothing in the API performed it. Whoever runs one
  writes down who, when and why, somewhere the database cannot revise.

## 6. Anomalies

**1. A renewal for a session that has already ended**

- **Covered by:**
  - "Esci", idle expiry, the cap, re-authentication, a new sign-in and a graceful stop all revoke the
    grant as `SessionEnded`. A per-user sign-out revokes it with its own reason.
  - A renewal received after that revoke is the tripwire. One received before it was in flight.
  - The legitimate BFF cannot send a renewal after the session has ended, because `Ended` is checked
    under the session lock (F2).
  - A session lost to a crash or a SIGKILL had no revoke, so its grant still renews until its cap.
    That cannot be detected; it is a residual.
- **Writes:** the tripwire, 401, a `RefreshTokenReuse` log line and audit row, and no revoke. In
  flight: Information. Revoked for another reason (`SignOutEverywhere`, `Incident`, `Deployment`):
  Information or Warning, since a live session can innocently learn of those revokes late.

**2. Too many renewals of one grant**

- **Covered by:** a detector at the API, decided as its own commit of PR-1 so that review can drop
  it, and not in the code at the time of writing.
  - An in-memory count of 200 answers per grant, over one access-token lifetime.
  - Above 3 it raises a Warning. It never refuses and writes nothing.
  - The legitimate BFF renews at most twice per token lifetime (single flight, the half-life
    threshold, and the stop near the cap, §4.5), so a limit of 3 leaves one spare.
  - It catches a copy that renews often, not a patient one.
- **Writes:** `RefreshRenewalRateHigh`, a Warning, in the logs only. On Azure it is invisible while
  logs are off; locally, in CI and in the plan's measurement runs, it is seen. Putting it in the audit
  trail would need a write, and this decision removes writes from renewal.

**3. A renewal past the session limit**

- **Covered by:** it cannot succeed, because `ExpiresAt` is fixed at issue and never extended, so the
  API answers 401. The legitimate BFF never sends one: its cap is the grant's expiry (F5), and it
  stops renewing near that point (F6). Access tokens are clamped to the grant's expiry.
- **Writes:** Information only, since the request gets nothing. Once the cleanup sweep
  (`RefreshTokenCleanupService`) deletes the row, the same grant is treated as unknown:
  `RefreshTokenUnknown`, the existing audit refusal.

**4. The same grant used for two sessions**

- **Covered by:** the legitimate BFF cannot do this. Each sign-in, registration and re-authentication
  mints its own grant, and the BFF never copies or overwrites one (§4.5). The API sees grants, not
  sessions, and it has only one client, so a second holder of a live grant looks exactly like the
  first. This is the loss accepted in §3, bounded by the 60-minute cap.
- **Writes:** nothing of its own. The copy shows up only through anomaly 2 (if it renews often) or
  anomaly 1 (if it is used after the real session ends).

## 7. Threat model

| Threat | Outcome | Why, and how far |
|---|---|---|
| Database dump or backup | Bounded | Only SHA-256 hashes of 256-bit secrets |
| A grant leaks into logs or telemetry | Bounded | Grants are never logged (O2h). Using one needs the key **and** a loopback socket on the API (§4.2) |
| Script in the browser (XSS, an extension) | Bounded | The grant never reaches the browser, and the token endpoints refuse the proxy. The script can use the session until the 15- and 60-minute limits or "Esci", as before |
| Stolen session cookie | Bounded, not detected | 15 minutes idle, 60 absolute, "Esci", a new sign-in in that browser (F13), or the per-user lever (§5) |
| The service key alone | Bounded | The token endpoints answer 404 to any address that is not loopback (F1), and log a Warning naming what was missing. Without the key, the API answers 401 `SERVICE_CREDENTIAL_REQUIRED` and logs the Warning "no valid service credential" |
| A memory image of the BFF | Bounded | Usable only from inside the replica. Grants die within 60 minutes, or at "Esci", idle expiry or a graceful stop |
| Code running in the replica | Accepted; handled as an incident | It holds everything live, and no token scheme can see it. Answer: the nuclear lever (§5.4) |
| A second holder of a **live** grant | Accepted, bounded | Unseen until its session ends, at most 60 minutes; under rotation, at most about 15 (§3, F8). Anomaly 2 covers a copy that renews often, once the detector is built |
| An **ended** grant presented again | Detected, not contained automatically | The tripwire: 401, an audit row, a security event (F3). The operator reads the audit trail and pulls a lever (§5.4) |
| A lost answer, a database outage, an uncertain EF commit | Eliminated | A renewal writes nothing. An expired token during an outage gives 503, never 401 |
| A half-applied key rotation | Bounded | 503 on renewal and on proxied calls (F4). The session is kept and the SPA stays signed in |
| A false tripwire | Residual, to measure | The revoke waits for the renewal in flight (at most 30 s), so a false tripwire needs the API to stamp an abandoned renewal more than 30 s after it was sent (F9c, measured by O1). The road by which a removed session came back is closed (F2). The cost is one audit row, and nobody is signed out |
| An access token after its session ends | Accepted, bounded | At most 15 minutes, and only code inside the replica could present it (§5.1) |
| A revoke lost (an outage, then a kill) | Bounded | Nobody holds the grant, and it dies at its cap. The revoker retries (F12); a graceful stop drains (F7) |
| An insider replays ended grants | Accepted | One audit row each; they are already inside |
| The API moves to its own host | Stop | The first precondition fails: DPoP or mTLS first (F1) |

## 8. The code, in outline

- **API:** `RefreshTokenService` renews by reading (`RenewAsync`), revokes one session's grants
  (`RevokeAsync`) or a user's (`RevokeAllForUserAsync`, with a reason), and no longer rotates.
  `AuthController` gains `POST /api/auth/revoke`; `ReceivedAtMiddleware` and `TokenRoadMiddleware`
  are new; `JwtService` takes an optional `notAfter`; `RefreshResponse` drops its refresh token and
  `TokenResponse` gains `refreshTokenExpiresAt`. One migration adds `RevokedReason`.
- **BFF:** `TokenRefresher` is rewritten as §4.5 says; `UserSession` gains the grant's expiry, the
  absolute expiry, `Ended`, `InFlightRenewal` and the lock; `InMemoryTokenStore` ends every session
  through one path; `GrantRevoker` is new; the proxy strips the marker and maps key refusals to 503.
  A named BFF-to-API timeout option, `BackendApi:TimeoutSeconds`, defaults to 100 s,
  `HttpClient`'s own default, so naming it changed nothing; PR-2 of the plan sets its value.
- **Contract:** the OpenAPI document, the frontend's generated types, the Bruno collection and the
  Schemathesis hooks follow; the direct callers send the marker. No SPA application code changes: a
  503 is already retried.

## 9. Records and plans affected

- [ADR-0021](0021-refresh-token-rotation-bff-remint.md): decisions 3, 5 and 6, the table rows on
  rotation, reuse response, concurrency and lifetime, and the PR-2 failure policy are struck in place
  and point here. Three statements about the standards are corrected there too: Duende's BFF
  documentation *recommends* configuring reusable refresh tokens, RFC 10017 §6.1.2.2 allows renewal
  on an observed expiration event and says only that tying the lifetimes "makes sense", and the
  draft-26 citation becomes RFC 10017 §6.1.2.2. Its `FamilyId` residual is closed.
- [ADR-0026](0026-absolute-session-cap-reauthentication.md) item 4: the old grant is now revoked at
  the API, alone.
- [ADR-0034](0034-failed-family-revoke-recovery.md): its decision is superseded, because with no
  automatic containment its branch no longer exists (F3). Its record of which SQL errors EF retries
  stays; the infrastructure registration cites it for that list and for leaving −2 unretried.
- [ADR-0044](0044-the-audit-trail-is-append-only-and-chained.md): the event inventory.
- [ADR-0055](0055-the-api-serves-one-client-the-bff.md): D4 gains the marker and the loopback check,
  D7 describes the loopback sidecar, and "What would change this" gains the API reached over a
  network.
- In the plan: the outage design's fix 4 is superseded, and the rule that it must land no later than
  fix 1 disappears, because a late renewal changes nothing. The deployment plan's row "Sidecar not
  on loopback" becomes "stop" (F1).

## 10. Verification: the oracles

The rule is the repository's: make the failure happen first, then show it gone.

**O0. On main `f949b18`, these had to fail**, or the harness could not see the failure.

1. The outage harness with `Jwt__ExpirationMinutes=2`: two HANG 10 runs with no extra delay, two
   with 15 s, and two new REFUSED 10 runs (extra 0 s and 3 s) on a 4 × 10 s retry budget.
   **Measured 2026-09-28** (`azurebank-work/plans/2026-09-27-stage-c/pr1/o0-harness.md`): 6 of 6
   runs failed as expected. HANG 10 without delay signed out one session in 2 of 2; with 15 s it
   signed out both, logged `RefreshTokenReuse` twice and left 0 active tokens, in 2 of 2; and
   REFUSED 10 committed its renewal 21.2 and 20.5 s after the BFF gave up, and signed a session
   out, in 2 of 2. The plan also named both p7 HANG runs; they were not run again.
2. Seven CI tests, **7 of 7 red on main, measured**
   (`azurebank-work/plans/2026-09-27-stage-c/pr1/o0-ci.md`):
   1. the same grant renewed twice: the second renewal got 401 (`AuthEndpointTests`,
      `Refresh_TheSameGrantTwice_AnswersOkBothTimes`);
   2. a command recorder saw 1 write command in one renewal (F9d;
      `RefreshTokenRotationSqlServerTests.ARenewal_SendsNoWriteCommand_OnSqlServer`);
   3. "Esci" on session A: B's renewal got 401, and 1 false `RefreshTokenReuse` row was written (F9e;
      `BffOverApiSessionTests.SigningOutOneSession_LeavesTheUsersOtherSessionRenewing_AndRecordsNoReuse`);
   4. an expired token plus a renewal 5xx: the proxied call got 401 `AUTH_TOKEN_EXPIRED`
      (`TokenRefreshTests.ExpiredToken_WithTheRenewalAnswered5xx_Is503_ForwardsNothing_AndKeepsTheSession`);
   5. a renewal refused with `SERVICE_CREDENTIAL_REQUIRED` revoked the session
      (`TokenRefreshTests.RenewalRefusedForTheServiceKey_KeepsTheSession`);
   6. with the BFF's key unlike the API's, a proxied read reached the SPA as 401 (F4;
      `BffOverApiSessionTests.WhenTheBffKeyIsNotTheApiKey_AProxiedReadReachesTheBrowserAs503`);
   7. "Esci" in the gap between a request's session read and its write-back: the next request got
      200, because the session came back (F2;
      `TokenRefreshTests.EsciInTheGapBetweenARequestsReadAndItsWriteBack_TheSessionStaysGone`).

   These seven are the "O0-2 item N" the tests cite, and all seven pass on the branch.

**O1. The same cases on the branch.**

- Before the run, name the branch's own log lines: the BFF's renewal timeout, and the API's
  "Refreshed access token" line. Main's `TaskCanceledException` at about 5 s may not appear (F9a).
- Both sessions answer 200 right after the outage and again 75 s later; 0 `RefreshRejected`, 0
  `RefreshTokenReuse`, 0 audit refusals; the active-grant count and the grant's `RowVersion` are
  unchanged.
- The CI tests: 200 and 200; 0 write commands per renewal; B gets 200 and no event; a 503 with
  nothing forwarded, and the session kept; the key mismatch gives a 503 and the SPA stays signed in;
  the gap gives 401 `AUTH_TOKEN_MISSING`, 0 renewals and 0 `RefreshTokenReuse`.
- HANG 150 with 2-minute tokens (F9b): no 401 and no sign-out; 5xx answers are allowed (a data call
  gets a 500 at about 35 s under any hang over 30 s); afterwards, a 200 on the same session.
- For every renewal, log the BFF's send time next to the API's `ReceivedAt`, on the same host clock,
  and report the largest gap. A false tripwire needs a gap over 30 s (F9c).

The evidence recorded for this record is O0 on main and the CI tests on the branch. The real-stack
replays of O1 are not among it.

**O2. Positive controls, with the same queries, so the zeros in O1 count only if these fire.**

- **a. The tripwire fires.** Sessions A and B of one user: obtain A's grant by a direct login, revoke
  it with `/api/auth/revoke`, present it again. Expect 401, one `RefreshTokenReuse` log line and
  audit row, B's grant still active, and B renewing with 200. Deleting the audit write, or flipping
  the arrival-order comparison, must turn it red
  (`RefreshTokenRotationSqlServerTests.AnEndedSessionsGrantPresentedAgain_TripsTheTripwire_AndTheOtherSessionStillRenews`).
- **b. A renewal in flight is not theft.** Hold A's read while `/api/auth/revoke` commits: 401 and
  no event
  (`RefreshTokenRotationSqlServerTests.ARevokeCommittedWhileARenewalWaitsToRead_IsNotTheTripwire`).
- **c.** The audit row still commits when the tripwire's request drops its connection. The unit
  test checks that the write is not handed the caller's cancellation
  (`RefreshTokenServiceTests.RenewAsync_TheTripwiresRow_IsWrittenEvenWhenTheCallerHangsUp`).
- **d. Sender constraint.** With the path block disabled, `/api/auth/refresh` through the proxy
  still gets 404, and a marker sent by the browser is stripped. Key, marker and a live grant from an
  address that is not loopback get 404 (`TokenRoadTests`).
- **e. Caps.** The option refuses values under 15 minutes, so these backdate the row or use a fake
  clock. An expired grant gets 401 and no event; an access token minted near the cap expires no later
  than the grant; with a 15-minute grant, `/bff/auth/me` reports the grant's expiry and the session
  ends then (F5); the validator refuses 14, 1441 and any value below `Jwt:ExpirationMinutes` (F11).
- **f. Scope.** "Esci" on A marks only A's row `SessionEnded`, and B still renews; re-authentication
  and a new sign-in carrying an old cookie (F13) mark the old grant `SessionEnded`; idle expiry
  revokes the grant within one 5-minute sweep; an "Esci" during a paused SQL is revoked after SQL
  returns; a graceful stop with 2 sessions revokes both grants and logs "left 0" (F7).
- **g.** 8 concurrent renewals give 8 × 200 with `RowVersion` unchanged
  (`RefreshTokenRotationSqlServerTests.EightConcurrentRenewalsOfOneGrant_AllSucceed_AndChangeNothing`).
- **h.** A capture sink finds no grant in any log line or audit row.
- **i.** A transfer during the outage is still debited once.
- **j. Stamp**, once built: sessions A and B of one user and C of another; raise the stamp through
  `/api/auth/logout`, and separately through the runbook's SQL; A and B get 401 at their first
  request after at most 15 s, C keeps getting 200, and removing the check turns it red.
- **k. Detector**, once built: 4 renewals of one grant within one token lifetime all get 200 and
  raise one `RefreshRenewalRateHigh`; 2 renewals raise nothing.

## 11. Not verified

- The O1 replays on the real stack (see §10).
- YARP short-circuiting to 503 from a request transform is seen in tests, through `TestServer`, and
  not yet on the wire.
- The delay before the `ReceivedAt` stamp under a hang is not measured (O1).
- Whether the API sidecar still answers the drain after the stop signal reaches both containers of
  the replica, on Azure. The drain's "left" count will show it.
- The 15 s watcher period, the detector's limit of 3, the revoker's 4 in parallel and 1000 in the
  queue, and "about 150 lines" are choices or estimates.
- The PSD2 text was read only through the legislation.gov.uk copy; EUR-Lex answered HTTP 202 with 0
  bytes when the red team tried it.
- Scale-to-zero still ends in-memory sessions. That belongs to the cold-start work or to a shared
  session store.

## Consequences

**Positive**

- A renewal writes nothing, so no outage, lost answer or uncertain commit can turn one into a
  sign-out. An expired token during an outage becomes a 503 the SPA retries, never a 401.
- "Esci" ends one session, and nothing else is signed out as a side effect.
- Nothing from one sign-in outlives its grant, at the BFF or at the API: sign-in plus
  `Jwt:RefreshTokenLifetimeMinutes`, 60 minutes by default.
- The token endpoints are reachable only by the BFF's own client over loopback, not by whoever holds
  the service key.

**Negative**

- A copy of a live grant works unseen until its session ends, up to 60 minutes, where rotation
  noticed it within about 15 (§3). Only code inside the replica can hold such a copy.
- A tripwire records and does not contain. Containment is a person reading the audit trail and
  pulling a lever, and on Azure nothing pushes the event to them.
- Two preconditions live outside the code, in this record: the API on loopback, and PR-1 before the
  first Azure deployment.
- A SIGKILL leaves the grants of that process's sessions alive, with no holder, until their cap.

**Neutral**

- The `RefreshTokens` columns of the rotation chain (`ReplacedByTokenId`, `RowVersion`) stay for the
  rows written before; nothing writes the chain any more. `RefreshTokenReuseRevokeFailed` and the
  audit outcome `MitigationFailed` are no longer raised, and stay for the rows already written.

## F1–F15: the red team's findings, as applied

- **F1:** the API refuses the token endpoints to any address that is not loopback (404; a null
  address only in the test host). The fallback "the API as its own app" becomes "stop: DPoP or mTLS
  first". Oracle O2d.
- **F2:** the store's write-back never re-adds a removed session; `Ended` and `InFlightRenewal`,
  under one lock on the session, replace the semaphores; `GrantRevoker` waits for the captured
  renewal. The gap test is in O0 and O1.
- **F3:** the tripwire records (a log line, an audit row, a security event) and revokes nothing; the
  runbook is the containment. On Azure the event is in the audit trail, not pushed. ADR-0034's
  revoke-recovery decision is superseded; its record of retried SQL errors stays.
- **F4:** the key refusal carries a header and becomes a 503 on proxied calls. The key-mismatch
  oracle is added.
- **F5:** one `AbsoluteExpiresAt` feeds the session's validity, `/bff/auth/me` and
  `/bff/auth/session-status`. Oracle O2e.
- **F6:** the thresholds come from the token's own lifetime; the grant guard applies in both
  branches, and renewal stops when it gains nothing.
- **F7:** a drain when the host stops, with counts. SIGKILL is stated as a residual.
- **F8:** the trade-off against rotation is stated in §3 and §7, and the write-free rate detector is
  recommended (§6).
- **F9:** (a) the log lines are named before the run; (b) HANG 150 allows 5xx; (c) the send time is
  measured against `ReceivedAt`, with a 30 s bound; (d) a command recorder; (e) the false event from
  the old "Esci" is in §1 and O0; (f) oracles for F1, F2, F4, F5 and F11.
- **F10:** `ReceivedAt` is the process's start time in UTC plus a `Stopwatch`.
- **F11:** the lifetime is validated to 15–1440 and must not be below the access-token lifetime.
  Short-lifetime tests backdate the row or use a fake clock.
- **F12:** the revoker runs in parallel, logs `GrantRevokeDropped` when its channel is full, and
  retries any 5xx or timeout. `/api/auth/revoke` answers 503 when the database fails.
- **F13:** a new sign-in or registration ends the session whose cookie came with it.
- **F14:** "PR-1 before the first Azure deployment" is written into this record (Preconditions).
- **F15:** a renewal still pending at 5 s counts as a transient failure; every renewal is detached,
  with a 30 s timeout; a registration without a grant keeps the old rule; the marker needs exactly
  one value.

## Related

- [ADR-0001](0001-bff-pattern.md) — the BFF, which keeps every token out of the browser
- [ADR-0021](0021-refresh-token-rotation-bff-remint.md) — the rotation this replaces
- [ADR-0026](0026-absolute-session-cap-reauthentication.md) — re-authentication, whose old grant is
  now revoked
- [ADR-0034](0034-failed-family-revoke-recovery.md) — the recovery decision this supersedes
- [ADR-0044](0044-the-audit-trail-is-append-only-and-chained.md) — where the tripwire's row goes
- [ADR-0055](0055-the-api-serves-one-client-the-bff.md) — the service key, and the loopback road
  this adds to it
