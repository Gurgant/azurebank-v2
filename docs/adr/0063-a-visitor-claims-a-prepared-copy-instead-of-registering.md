# ADR-0063: A visitor claims a prepared copy instead of registering

**Status:** Accepted · **Date:** 2026-10-04 · **Amended:** 2026-10-05 (ADR-0064), 2026-10-06
(ADR-0013), 2026-10-07 (decision 14) · **Amends:** ADR-0014, ADR-0053, ADR-0055, ADR-0057, ADR-0062

## Context

ADR-0062 prepares a pool of private copies of the fixed demo, each owned by a demo user with no
password, and leaves open how a visitor gets one. Registration creates a user outside every copy,
who sees none of the demo's history and makes every `recycle` exit 13. The 2 GB database never
gives an audit row back, so what one visitor, one address and one copy can write needs a bound.

## Decision

1. **On the demo a visitor claims a copy, and nobody registers**: a claim gives a free copy's owner
   a password and opens a session; registration is 403 `REGISTRATION_CLOSED`, because a registered
   user belongs to no copy. With `Demo:Enabled` off, the default, the claim is 404.
2. **One conditional statement decides who has a copy**: `UPDATE DemoCopies … WHERE Id = @id AND
   ClaimedAt IS NULL`, because "one row changed" is the only proof that the copy is this request's.
3. **A claim is one transaction, and its statements run in an order that was measured**: the grant
   is inside it; a copy's contacts are read before its password is written, or claims can deadlock.
4. **A visitor is given a fresh copy while one is left, and an old one before none**, because an old
   history is better than none: 429 `DEMO_POOL_EMPTY` means that no copy of any age is free.
5. **Two claims at once can deadlock while `READ_COMMITTED_SNAPSHOT` is off, and the host's retry is
   the answer**, because a claim can be run again: its unique `ClaimId` recognises a lost commit.
6. **The password and the client's key come from the cryptographic generator and a keyed hash**:
   sixteen characters of an alphabet of 56, 92 bits; and `ClientKey`, the address under HMAC-SHA256
   with `Demo:ClientKeySecret`, because a plain hash is undone by hashing every IPv4 address.
7. **Four caps, each with its number**: ten claims of one client in 60 s (the BFF's `auth` policy),
   `Demo:Claim:MaxPerClientPerDay` (the daily cap, 10 in 24 hours), `Demo:Copy:MaxWrites` (200) and
   `Demo:Pool:MaxClaimsPerDay` (150). The daily cap counts rows of the database, because a limiter
   in the BFF's memory is lost when the replica stops; the overshoot is at most ten (9 + 10 = 19).
8. **A refusal of the demo is 429 with a code of its own, never 503** (`DEMO_POOL_EMPTY`,
   `DEMO_DAILY_LIMIT`, `DEMO_COPY_LIMIT`), because the SPA words 503 as an outage and retries reads.
9. **The two answers that depend on the flag come before the request's body is looked at**, from a
   marker on the endpoint (`DemoOnly`, `ClosedInDemo`), because the binder answers 400 or 415 first.
10. **Sign-in is gated on the demo**: a user outside every copy, with no password, in an unclaimed
    copy or in one claimed `Demo:CopyLifetimeHours` ago or more is answered exactly as an email
    nobody has, with no failed attempt counted, so that nothing a visitor cannot sign in as can be
    locked. Renewal is not gated: a session opened before a copy's end runs to its grant's end.
11. **A copy can make `Demo:Copy:MaxWrites` changes, and the request past that is 429
    `DEMO_COPY_LIMIT`**: `DemoCopies.Writes` counts each request of a signed-in user that is not
    GET, HEAD, OPTIONS or TRACE, and each reveal of an account number, because the reveal writes an
    audit row. A refused request is counted too; no token endpoint is, so sign-out is never refused.
12. **The BFF's door**: `POST /bff/auth/demo/claim` takes `{}` as JSON, because another site's page
    can post a form but not JSON, and names the client itself (`ClientAddress.Of`, the key its
    limiters count by), because a browser could name any. `/api/auth/demo/claim` is never proxied.
13. **The page says it is the demo with one tag, put in when the host starts**: the BFF serves
    `index.html` with `<meta name="azurebank-demo" content="true">` in its head, because one build
    serves every deployment. The Vite dev server adds the tag only with `AZUREBANK_DEMO=true`.
14. **The settings, and where the demo is on**: `Demo:Enabled` on the API and on the BFF, and on
    the API `Demo:ClientKeySecret`, 32 characters or more. `compose.demo.yaml` sets them on one
    machine, the template's `demo` switch on Azure (ADR-0064). The daily cap is one visitor's own
    only where the BFF sees that visitor's address: behind an ingress it sees the ingress's until
    `ForwardedHeaders:KnownIPNetworks` names its network. The template writes no cap: 10 applies.
15. **What is logged and counted**: plain log lines and the counter `azurebank.demo.claims`; no
    `SecurityEvent` line, no audit row, and no line with a password, an email, a token or a grant.
16. **The claim is the first token endpoint that opens a session from no credential, and it rests on
    the API being on loopback** (ADR-0057 §4.2), because it takes the address on its caller's word.

## Rejected

- Rejected: 404 for any method on a demo-only path, because the public contract states the path.
- Rejected: a `SecurityEvent` line or an audit row for a claim, because sign-in writes neither.
- Rejected: a cap on starting over, per session, because parallel requests and a sign-out pass it.
- Rejected: a `GET /bff/config` for the flag, because it is a round trip before the first screen.
- Rejected: a shell with no `</head>` served untagged, because it reads as demo off; the host stops.
- Rejected: asking the file system what a path resolves to, because it is one call on every GET.
- Rejected: declaring `DEMO_COPY_LIMIT` on its sixteen operations, because only the demo answers it.

## Consequences

- With the demo on nothing creates a user outside the pool: no visitor causes `recycle`'s exit 13.
- With the flag on the job and on neither container, visitors register beside the pool and `recycle`
  exits 13. A flag on one of the app's two containers alone fails closed.
- Not covered: a claim whose answer is lost holds a copy nobody has until `recycle` deletes it.
- Neutral: the contract has 31 operations on 28 paths; a server path behind extra slashes is 404.

### What a visitor can still do

- Not covered: hold several copies, and stay in one past its end until the session's grant ends.
- Not covered: claim past the daily cap, by decision 7's overshoot or from several addresses.
- Not covered: share a cap with strangers: visitors behind one address are one client, and under
  `compose.demo.yaml` every browser on the machine is one client.
- Not covered: learn that this build has the claim: another method on its path is 405, demo or not.
- Not covered: on Windows, read the page without its tag under four spellings of its file's name.

## Revisit when

- The API is reached over a network: the claim's caps are worth nothing until that is answered.
- An endpoint that writes on a safe method is added: it carries `[CountedAsDemoWrite]`.

<a id="what-the-browser-keeps-in-demo-mode-added-2026-10-05"></a>

## What the browser keeps in demo mode

1. **One key, one shape**: `localStorage["azurebank.demoCopy"]` holds `{ v: 1, email, password,
   pin, contacts, expiresAt }`, written where a claim's 200 arrives. The demo stores nothing else.
2. **What comes back from the key is input**: a string that fails the claim's own check is removed,
   not repaired, because anything on the page's origin can write the key.
3. **When the key goes, and when it does not**: a claim replaces it; "Forget this copy", a 401 to
   "Continue with my copy" and a sign-in page opened past the copy's end remove it; a sign-out
   keeps it, because coming back to the copy is what the key is for. No timer runs.
4. **What a script that read the key would gain**: a sign-in to one throwaway copy of invented
   money until the copy ends, with no token and nothing of a real person. "Stay signed in" sends the
   kept password at a session's fixed end, with nothing typed (ADR-0026, decision 2).
5. **What the browser's clock does**: only the sign-in page, when it opens, removes a copy past its
   end, because an ended copy is not worth keeping, and a check at every read would take a signed-in
   owner's dashboard panel. A clock ahead by more than the copy has left forgets a living copy.
6. **The key is the source, read each time it is asked for**: each button reads the pair when
   pressed, so a copy another tab forgot or replaced is never sent. There is no `storage` listener.
7. **Three sentences assume the default lifetime**: "24 hours" is typed out in them.
8. **Beside the key**: the claim's answer is checked whole (ADR-0023); `/register` leads to sign-in.

## Verified by

- `DemoClaimSqlServerTests`, `AuthServiceTests`, `DemoWriteBudgetSqlServerTests`, `DemoClaimTests`.
- The browser: `demoCopyStorage.test.ts`, `demoContract.test.ts`; by hand, `npm run test:e2e:demo`.

## Related

ADR-0012, ADR-0014, ADR-0026, ADR-0054, ADR-0055, ADR-0057, ADR-0058, ADR-0059, ADR-0062, ADR-0064.
