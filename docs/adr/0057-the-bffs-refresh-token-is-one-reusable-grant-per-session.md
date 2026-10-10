# ADR-0057: The BFF's refresh token is one reusable grant per session

**Status:** Accepted · **Date:** 2026-09-28 · **Amended:** 2026-09-30 (ADR-0058), 2026-10-01
(ADR-0059), 2026-10-04 (ADR-0063) · **Supersedes:** ADR-0021 decisions 3, 5 and 6, and ADR-0034 ·
**Amends:** ADR-0026 item 4, ADR-0044, ADR-0055 D4 and D7 · **Decision Makers:** Vladislav Aleshaev

The code cites §1 to §11 (§4: §4.1 to §4.8; §9: the status line), F1 to F15 (bold), O0 to O2 (§10).

## Context

**§1 The problem.** Under rotation (ADR-0021) every renewal wrote: it revoked the token presented
and stored a successor. When the answer was lost the BFF kept the replaced token, and presenting it
again was read as theft. Measured: in 8 of 8 runs where a renewal met a 10 s database hang someone
was signed out, in 4 of them every session of the user. An expired token forwarded to the API and a
half-applied key rotation could end the same way, and "Esci", the sign-out of one session, revoked
every token of the user, so the user's other sessions were then read as theft.

## Decision

- **Preconditions**, which no code can enforce. **First: the API stays on loopback**, a sidecar in
  the BFF's network namespace on compose and on Azure (`127.0.0.1:5068`). Reached over a network,
  stop: DPoP (RFC 9449) or mutual TLS (RFC 8705) comes first. **Second (F14): this decision ships
  before the first Azure deployment**, because its migration revokes every active grant
  (`Deployment`), which the revision before it reads as reuse, revoking every token of its user.
- **§3 The refresh token (the "grant") stays and stops rotating; it lives as long as its session and
  is revoked one session at a time**, because presenting a grant takes the grant, the service key
  and a socket on the API's loopback interface. Only code inside the replica has all three; it
  already reads every token from memory, and revoking one user's tokens does not contain it;
  rotating the service key does (OAuth 2.1 draft 16, §3.2.1). Rotation is mandatory for public
  clients only (RFC 9700 §2.2.2, RFC 9449 §5), and FAPI 2.0 §5.3.2\.1 item 9 forbids it "except in
  extraordinary circumstances", under a client authentication this system lacks. **F8:** a copy of a
  live grant works unseen until its session ends; rotation noticed a used one in about 15 minutes.
- **§4.1 The grant** is 256 bits from a CSPRNG, kept as a SHA-256 hash only. It expires
  `Jwt:RefreshTokenLifetimeMinutes` after issue (60, the BFF's absolute cap, per RFC 10017
  §6.1.2\.2), is never extended and caps each token it renews: `exp = min(now + 15 min, ExpiresAt)`.
  **F11:** the API refuses to start outside 15 to 1440 or below `Jwt:ExpirationMinutes`, because the
  sign-in's own token is minted before its grant, uncapped. **F5:** the BFF's `AbsoluteExpiresAt` is
  `min(SessionCreated + 60 min, refreshTokenExpiresAt)`. **F15:** a session registered without a
  grant ends at its token's expiry. `RevokedReason` is `SessionEnded`, `SignOutEverywhere`,
  `ReuseContainment`, `Incident` or `Deployment`; null is a legacy row.
- **§4.2 The token endpoints answer only the BFF's own client, over loopback.** Login, register,
  refresh, revoke, logout, the demo's claim (ADR-0063) and the stamp feed (§5.3) answer 404 unless
  the remote address is loopback (**F1**; a null address passes only in the test host) and the
  request carries exactly one `X-AzureBank-Token-Road` header with the value `bff` (**F15**),
  because the proxy reaches the API over loopback too: the BFF's own client adds the marker, and the
  proxy strips a browser's copy and blocks those paths. The check runs after the key's.
- **§4.3 `POST /api/auth/refresh` reads the grant and writes nothing**, so the same grant renews
  again. **F10:** the first middleware stamps `ReceivedAt` as process start plus a `Stopwatch`, so a
  clock step cannot reorder two stamps. Every refusal is the same 401 (ADR-0021, decision 4).

| Grant state | Answer | Writes | Event |
|---|---|---|---|
| Unknown | 401 | an audit refusal | `RefreshTokenUnknown` |
| Revoked `SessionEnded`, request received **after** the revoke: **the tripwire** | 401 | the audit row only, written with `CancellationToken.None`; **no revoke** | `RefreshTokenReuse` security event |
| Revoked `SessionEnded`, request received at or before the revoke | 401 | none | Information: revoked while its renewal was in flight |
| Revoked for any other reason, or a legacy row with no reason | 401 | none | Information naming the reason; Warning for `Incident` (the nuclear lever) and `ReuseContainment` (the runbook's answer to a tripwire) |
| Expired, or less than 1 s of life left | 401 | none | Information |
| Active | 200 `{accessToken, expiresAt}` | **none** | the "Refreshed access token" line; the detector's count, in memory (§6) |

- **§4.3, F3: the tripwire records and does not revoke**, because only code inside the replica can
  trip it and revoking one user's grants does not contain that code; the runbook does (§5.4). If its
  audit write fails the answer is 500 (ADR-0044), or 503 when the database cannot be reached
  (ADR-0058). Refresh, revoke and logout carry `[NoRequestDeadline]`: once started they finish.
- **§4.4 `POST /api/auth/revoke` revokes one grant, or up to 1000 (§4.6), as `SessionEnded` at the
  request's `ReceivedAt`.** It answers 200 for revoked and unknown grants alike (RFC 7009 §2.2) and,
  **F12**, 503 with `Retry-After: 5` when the database fails (its §2.2.1). `POST /api/auth/logout`
  revokes every grant of the user as `SignOutEverywhere`; no button calls it.
- **§4.5 The BFF renews by the token's own lifetime, L = exp − iat (F6).** More than L/2 left: use
  it. Down to min(60 s, L/4): use it and renew in the background. Less, or expired: wait at most 5 s
  for a renewal; one still pending is a transient failure (**F15**), after which the held token goes
  out only with more than 5 s left; else the answer is 503 `SERVICE_UNAVAILABLE` with `Retry-After`:
  an expired token is never forwarded. A renewal starts only when the session has not ended, none is
  in flight, none failed in the last 15 s and the grant outlives the token by 1 s. **F2:** single
  flight is `Ended` and `InFlightRenewal` under one lock on the session. Every renewal runs detached
  with a 30 s timeout (**F15**); only a later expiry is stored, and without one the session stops
  renewing. Only a 401 `REFRESH_TOKEN_INVALID` ends the session; any other failure keeps it.
- **§4.6 Every ending takes one path; "Esci" ends one session.** Step 1, under the session lock,
  mark it `Ended`, capture `InFlightRenewal`, remove it; step 2, delete the cookie and answer 200;
  step 3, queue the grant on `GrantRevoker`. A removed session never returns (**F2**: write-backs
  use `TryUpdate`). `GrantRevoker`, a channel of 1000 with 4 workers (**F12**; both are choices),
  waits up to 30 s for the captured renewal, calls revoke with a 5 s timeout and retries a 5xx, a
  timeout, a network error or a refused key until the grant expires; a full channel drops the item
  (`GrantRevokeDropped`). Idle expiry, the cap, re-authentication (ADR-0026), a grant the API calls
  invalid, a new sign-in over its cookie (**F13**) and a stale stamp (§5.3) end a session this way.
  **F7:** a graceful stop revokes every grant held, from `ApplicationStopping`, because the replica
  scales to zero 300 s after its last request; it has 7 s, plus 1 s for a call that ignores the
  cancel (Docker kills the BFF 10 s after the signal), and logs how many it revoked and left.
- **§4.7 (F4)** The API marks a refused service key with `X-AzureBank-Refusal: service-credential`,
  and the BFF answers 503 with `Retry-After` in place of that 401, so the SPA stays signed in.
- **§4.8 The inactivity timeout is 15 minutes**, where PSD2's RTS art. 4(3)(d) allows 5 and OWASP
  names 2 to 5: a deviation this demo accepts, because its visitors read code between clicks.
- **§5 Four levers sign people out.** One session now: "Esci" (§4.6). One user everywhere now:
  `POST /api/auth/logout` or the runbook's SQL (§5.3). Everyone now: restart the revision (§5.2).
  Nuclear: one revision rotates `Jwt:Secret` and `ServiceCredential:BffKey` together, then SQL sets
  `Incident` on every live grant created before it; every access token then fails its signature.
- **§5.1** A lever acts when the BFF session ends: the browser holds a cookie, never a token. An
  access token outlives its session by up to 15 minutes, presentable only from inside the replica.
- **§5.2** A restart signs everyone out, because sessions live only in the BFF's memory and the
  deployment runs at most one replica. A shared store or a second replica needs a global stamp.
- **§5.3 The session stamp**, a per-user counter (`SessionStamp`), rises with every per-user
  sign-out in the same transaction as its revoke, because nothing else signs one user out without
  signing everyone out. A sign-in answers the stamp read before its grant was issued, and the raise
  renews `ConcurrencyStamp`: neither a race nor a stale write undoes it. `SessionStampWatcher` reads
  `POST /api/auth/session-stamps` every 15 s while anyone is signed in, and a session below its
  user's highest known stamp ends (§4.6): about 20 s at worst for up to 1,000 signed-in users, 5 s
  more per further 1,000; with the feed unreadable, at its next renewal, within 7.5 minutes of use.
- **§5.4** The steps and their SQL are in [`docs/runbooks/refresh-token-reuse-recorded.md`](../runbooks/refresh-token-reuse-recorded.md).
- **§6 Anomalies.** Anomaly 1, a renewal for an ended session: the tripwire; the BFF checks `Ended`
  under the lock and sends none. Anomaly 2, too many renewals of one grant:
  `RefreshRenewalRateDetector` counts accepted renewals and logs `RefreshRenewalRateHigh` above 3 in
  one access-token lifetime (the BFF makes about 2: reasoned, not measured); it refuses and writes
  nothing. Anomaly 3, a renewal past the limit: 401, then `RefreshTokenUnknown` once the row is
  swept. Anomaly 4, one grant in two sessions: the API cannot tell a second holder from the first.
- **§8** The BFF's wait for the API, `BackendApi:TimeoutSeconds`, is 55 s (ADR-0058); a renewal has
  30 s, and revoke, `/me` and the stamp poll 5 s. The SPA retries a read's 503 once (ADR-0059).

## Rejected

- Rejected (§2): rotation plus a grace window, because the old token is honoured in the window.
- Rejected: stopping rotation alone, because the 7-day lifetime and the sign-out everywhere stay.
- Rejected: the BFF naming the next token, because it is a new protocol that no vendor ships.
- Rejected: a credential derived from the cookie, because the API would honour the cookie itself.

## Consequences

- A renewal writes nothing, so no outage, lost answer or uncertain commit signs anyone out.
  `ReplacedByTokenId`, `RefreshTokenReuseRevokeFailed` and `MitigationFailed` stay for old rows.
- **§7** Not covered: code running in the replica holds everything live, and no token scheme can see
  it: the nuclear lever answers it. A second holder of a live grant is unseen for up to 60 minutes.
- **§7** Not covered: a tripwire is recorded, not contained, and pushed to nobody. A false one,
  which needs the API to stamp an abandoned renewal over 30 s late (**F9c**), costs one audit row.
- Not covered: a kill, a stop of the whole compose project (the API stops first) or a dropped revoke
  leaves the grants concerned alive, with no holder, until their cap.
- **§11** Not verified on Azure: whether the API still answers the drain when both containers get
  the stop signal at once (the "left" count shows it), and whether 8 s fit the platform's grace.

## Verified by

- **§10 O0-2**, red under rotation: 1 `AuthEndpointTests`; 2 `RefreshTokenRotationSqlServerTests`;
  3 (**F9e**) and 6 `BffOverApiSessionTests`; 4, 5 and 7 `TokenRefreshTests` (items 1 to 7).
- **§10 O2**, the positive controls: O2a (the tripwire fires), O2b (a renewal in flight is not
  theft), O2e (the caps), O2g (concurrent renewals) `RefreshTokenRotationSqlServerTests`; O2c
  `RefreshTokenServiceTests`; O2d (the token road) `TokenRoadTests`; O2f `SessionEndingTests`; O2h
  (no grant in a log line or audit row) `GrantSweepTests`, `GrantSweepSqlServerTests`; O2j
  `SessionStampLeverTests`, `SessionStampLeverSqlServerTests`; O2k `RenewalRateDetectorTests`,
  `RefreshRenewalRateDetectorTests`; O1 (outage replays) and O2i (a transfer in an outage): no test.

## Related

ADR-0001, ADR-0021, ADR-0026, ADR-0034, ADR-0044, ADR-0055, ADR-0058, ADR-0059, ADR-0063.
