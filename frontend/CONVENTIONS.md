# Frontend conventions

Rules that hold across the SPA. They are here rather than in an ADR because none of them is a
decision with a weighed alternative — they are the shape this codebase already has, written down so
a change reads as a change.

Decisions live in [`docs/adr/`](../docs/adr/README.md); in particular ADR-0019 (SPA/BFF
integration), ADR-0022 (money mutations) and ADR-0023 (runtime validation). Cross-cutting traps
live in [`docs/engineering-traps.md`](../docs/engineering-traps.md).

---

## Data layer

**Cache tags are the invalidation contract.** Two clauses are not obvious and are worth stating:
`set-primary` invalidates the blanket `Account` tag, because exactly one account is primary and
changing which one moves state on two rows; and historical snapshots (`?at=`) stay **tag-less**,
because a past balance cannot become stale and tagging it would evict it on every mutation.

**Every endpoint unwraps its envelope through `unwrap`, and typing is not validation.** The seam is
per-endpoint and typed, so the *declared* shape is checked against the generated contract at compile
time — but `unwrap(envelope, schema?)` takes the schema **optionally**, and without one it returns
`envelope.data` as-is. A compile-time type is a claim about what the server sends, not a check.
Server drift on an unvalidated endpoint reaches the RTK Query cache silently.

Fail-closed runtime checking happens only where a schema is passed, and the set is deliberate: the
**four money receipts** (deposit, withdraw, transfer, internal transfer), the **accounts list** and
the **transaction summary** — plus the whole `/bff/auth/*` surface, which has no OpenAPI contract
behind it and gates authentication. Everything else on `/api/*` validates in development and test
only, where a mismatch should be loud, rather than in production where it would take the page down
over a field nobody renders. (One case is guarded regardless: a 2xx with a null `data` throws
rather than letting `undefined` into the cache.) ADR-0023 has the rejected alternatives.

**422 routing is on `errorCode`, four ways** — the business rule that failed decides the message and
the affected field. Never route on the status alone: several unrelated rules share 422.

**429 appears in three places** — login lockout, PIN lockout, and per-user API rate limiting — and
in all three the countdown is **computed client-side from the response's `retryAfterSeconds`**.
Never trust an absolute `lockedUntil` timestamp from the server: the browser's clock is not the
server's, and a skewed clock either unlocks early or hangs forever.

**In demo mode a claim adds three 429s, and two of them are worded with no countdown, by
decision.** `RATE_LIMIT_EXCEEDED` is the BFF's limiter, which the claim shares with sign-in: on the
sign-in page it is counted down as sign-in's is, and in the "Start over" dialog it is one fixed
sentence. `DEMO_POOL_EMPTY` and `DEMO_DAILY_LIMIT` are each worded by their code, in the app's
sentences and never the server's, and neither is counted down, although the day's limit names a
wait in `retryAfterSeconds`: the countdowns of the sign-in page belong to the lock and to the
limiter, by their codes (`src/pages/LoginPage.tsx`). A fourth code, `DEMO_COPY_LIMIT`, can answer
any change made in a copy that is past its budget of changes (ADR-0063, decision 11). Nothing is
built for it: each surface shows the fallback it has, which where that prints the problem's
`detail` is the API's own sentence (held for the "Start over" dialog by
`src/features/demo/StartOverDialog.test.tsx`, `any other refusal: what the server said, or the
fallback`; read from the code for every other surface; no run on a stack met it).
_(Until 2026-10-05 this section counted three places and knew no 429 without a countdown.)_

**One place does compare a server instant with the browser's clock: a kept demo copy's end, on
the sign-in page.** It is not a countdown. The page reads the clock once, when it opens, and a
copy whose `expiresAt` is not after that instant is not offered and is removed from the browser.
So a browser whose clock is ahead of the server's by more than the copy has left forgets a living
copy for good, with nothing said, and that copy is never sent to the server; one whose clock is
behind offers a copy that has ended, and there the server's 401 to "Continue with my copy" is
what says so. ADR-0063's section "What the browser keeps in demo mode" has why it is built that
way and what it costs.

**Errors surface through one root `<Toaster>`.** Error toasts persist rather than auto-dismissing,
and they carry a copyable `traceId` — that identifier is the only bridge between what a user saw and
the server-side trace (ADR-0016, ADR-0017). A toast that drops the traceId breaks the only link.
**One toast is a success:** "You have a new copy.", said through the same outlet once the
"Start over" dialog's claim has succeeded and the dialog has closed
(`src/features/demo/useNewCopyToast.tsx`). It names no timeout, so it leaves by itself, and it
carries no `traceId`: nothing in it has to be read or reported. _(Until 2026-10-05 the outlet
carried errors only.)_

**Region discipline for async state**: a first load renders a skeleton shaped like the content it
replaces; a background refetch keeps the stale data and shows a small inline indicator; a failed
region renders an inline retry rather than blanking. Mutations show pending state on the button that
started them, never as a page-level overlay.

**A wait the visitor can see gets a `WaitHint`, under the control or spinner that started it.** Each
page or dialog renders its own rather than one for the app, because a Fluent modal hides everything
outside it from assistive technology; never inside a `role="alert"` or an element an
`aria-describedby` points at, or its words are read as part of those. It says nothing for 5 s, then
"Taking longer than usual…", then "Still trying…" — on a money send that holds a key
(`kind="moneySend"`; a PIN check before it is a `"write"`) "Still trying… Keep this page open.". Only a read can be offered "Stop waiting", from 20 s and only when its
host passes `onStopWaiting`: a write the server may already be doing is never given up on. A read
whose content takes the wait's place passes `failed`, and a wait of it that said something says
"Loaded.", unseen, when it loads. A read's spinner and error bar follow `readWait`, not `isLoading`
and `error`, so **a read's error bar hides while its read refetches**: a Retry shows the wait again.
**The bar goes inside the page's `AlertSlot`**, an empty `role="alert"` that is on the page from the
start, never into an alert of its own that mounts already filled: a failure is then a change of a
region that was there, which is what a screen reader reads, and a second failure fills it again. One
slot per page, so two failures at once are one alert; a check whose line sits under its own field,
as the recipient check's does, keeps a slot of its own there. Stop and Retry arm `useWaitLanding`,
which puts focus on the control the failed wait comes back with (the bar's Retry) unless the visitor
has put it somewhere else meanwhile. ADR-0059 has the reasons.

**A view that says how a money send ended lands lost focus on its sentence.** The control that sent
is disabled during the send and the browser hands its focus to `body`, so the view appears with
nothing focused, and a view that mounts with its words already in it is not announced. The sentence
is a `<Text as="p">` with `tabIndex={-1}` and the ref of `useFocusWhenLost(active)`, which focuses
it when the view appears and focus is on `body` or on a container around the sentence (the dialog
itself, where a press on a disabled control leaves it): the landing is there so that it is read
before the actions, and one Tab reaches the first of them. **Not a live region, an alert or a status
as well, and no `aria-describedby` on a button:** beside the landing those would say the sentence a
second time, and a description would say it again at the Tab. The went-through view of the four
money flows is built this way: `WentThroughView` on the two transfer pages, and the same block in
the deposit and withdrawal dialogs, where the action carries a `key` so that it is a new node, not
the send button renamed under a focus that never left it. In a dialog the view is shorter than the
form it replaces, so the second press of a double click on the send button lands on the backdrop:
the dialog tells its shell (`keepOnOutsidePress`), and a press outside then closes nothing, while
the X and Escape still do. The check view is kept the same way: it is shorter than the form too,
and behind it is the tile that opens the dialog for a second payment. The receipt of a send that
succeeded is not kept. After a key press the browser draws the app's focus ring around the
sentence; after a click it draws none. Three limits, known:

- when another dialog holds focus as the answer arrives, or the visitor moved focus to a control
  during the wait, the landing does not run, and nothing brings the sentence to a screen reader;
- the check view ("We couldn't confirm …") lands no focus, so focus stays where the send left it.
  Measured: on `body` after one press on the send button, and from there Escape reaches the
  dialog only after a Tab has brought focus into it; on the dialog itself after the second press
  of a double click on "Withdraw", and from there Escape closes it at once;
- a landing does not depend on the screen reader, but what each one says on a focused paragraph
  does. NVDA said the sentence once in each of the four flows, and the title only where it is a
  dialog's name: on the two transfer pages, where it is the page's heading, it was not spoken.
  ADR-0059's Validation has the rest of what was heard, and what was not.

## Money and formatting

**Amounts are always positive; direction lives in `type`.** A negative amount in the UI layer means
somebody re-derived a sign that the contract already encodes, and the two will eventually disagree.

**One `formatCurrency`.** EUR, one implementation, no local `Intl.NumberFormat` calls. A second
formatter is how two screens end up disagreeing about what €1.005 rounds to.

## UI stack

**Fluent UI v9 with Griffel, and nothing else.** No third-party toast, skeleton, grid or modal
library — Fluent covers all four, and a second system means two focus models, two theme sources and
two sets of accessibility behaviour.

**MSW is a test tool.** `dev:mock` exists for click-throughs and demos; it must never become *the*
development path, because a frontend developed only against mocks drifts from the real contract and
nobody finds out until integration.

## Demo mode

The public demo is one build with one switch, and the switch is not in the build (ADR-0063).

**The page says whether it is the demo, with one tag, and `isDemoMode()` is its only reader.**
`<meta name="azurebank-demo" content="true">` in the page's head turns the demo on. A page with no
such tag is not the demo, and neither is one whose `content` is anything but exactly `true`. The
BFF puts the tag in where it serves the page with `Demo:Enabled` set (ADR-0063, decision 13). The
Vite dev server adds the same tag only when it was started with `AZUREBANK_DEMO=true` in its
environment, and a build never carries it (`vite.config.ts`, whose comment says why the variable
has no `VITE_` prefix). `isDemoMode()` (`src/features/demo/demoMode.ts`) reads the page each time
it is asked, never once when a module loads: ask it in the component or the handler that needs
the answer. The app has no second place to learn the mode from.

**The demo keeps one key in `localStorage`, `azurebank.demoCopy`, and only a claimed copy's
sign-in details go under it:** `{ v: 1, email, password, pin, contacts, expiresAt }`. It is
written in one place, where a claim's answer arrives (`src/features/auth/sessionMiddleware.ts`),
and read through `src/features/demo/demoCopyStorage.ts` alone. Nothing else of the demo's is
stored there or beside it: no token, no session identifier, nothing a visitor typed. (The app's
other key, `azurebank.theme`, holds the theme preference, on the demo or off it:
`src/theme/themePreference.ts`.) `SECURITY.md`'s storage invariant names this key as its
exception in demo mode. What comes back from the key is input: it is parsed with the
definition the claim's answer is checked by, and a string that fails is removed, not repaired.
The key is read at each ask, so never copy the snapshot into a state or a module variable: a copy
held that way is how a second tab would go on holding a password the first was told to forget.
(The storage module holds one copy that way itself: the one a browser refused to store. While
it does, that tab does not read the key, and what another tab forgot or replaced does not reach
it.) "Try the demo" asks at the press as well, and sends no claim over a copy another tab left.
Off the demo the key is never read. Every kept value is rendered as text. ADR-0063's section
"What the browser keeps in demo mode" has when the key is removed.

**Inside `src/features`, the demo's helpers are imported by file** (`../demo/demoCopyStorage`,
`../demo/demoMode`). `src/features/demo/index.ts` hands out the four components and nothing else:
the session middleware imports the storage module, and through the barrel it would pull every
component in behind it.

**A test turns the demo on with `enableDemoMode()`** (`src/test/demoMode.ts`), which puts the tag
on the test page, and leaves a kept copy with `rememberDemoCopy(...)`, a raw write of the key as
the product would have left it. `src/test/setup.ts` takes the tag off and empties the key after
every test, so no test hands the demo to the next. That helper types the tag's name and the key
out and imports neither from the product: a helper that asked the product for a name would follow
it to any name. With the tag on the page the mock is the demo too: it hands out three fixed
copies and signs in the owner of a claimed copy and nobody else, so the mock's own user is
refused there (`accountForLogin` in `src/mocks/handlers.ts`).

**The demo's own sentences live in `src/features/demo/demoWords.ts`,** a constant each, or a
function where a sentence has a blank. The digits of the demo PIN are one constant of
`demoPin.ts` beside it, and the `pin` a kept copy carries is never rendered.

## Testing with Fluent and jsdom

These five have each cost a debugging session, and none of them fails in a way that points at the
cause.

**Never put `contentBefore` on an input inside a dialog.** Typing into it closes the modal. The test
failure reads as "the element disappeared", which sends you looking at your own unmount logic.

**`userEvent.type` truncates Fluent inputs.** Use `fireEvent.change` with the complete value. The
symptom is an assertion failing on a string that is missing its first characters.

**Never assert on `input.value` under react-hook-form's `register`.** RHF owns the DOM node and the
value you read is not necessarily the value in form state. Assert on submitted values or on rendered
output instead.

**The `matchMedia` stub in `src/test/setup.ts` is load-bearing.** It reports `prefers-reduced-motion:
reduce`, which makes Fluent skip dialog animations and gives deterministic ARIA state. Removing it
reintroduces a whole class of flake. Two consequences follow: after any async transition, query with
`findBy*` or wrap in `waitFor`, because tabster lifts background `aria-hidden` asynchronously; and
since jsdom never evaluates media queries, elements hidden behind a mobile-first breakpoint are
present but hidden, so they need `{ hidden: true }` to be found.

**A fake clock can stop jsdom's animation frames for the rest of the file.** jsdom runs
`requestAnimationFrame` on an interval it starts at the first frame request; started under a fake
clock, that interval dies when the clock is restored, and RTK hands a request's store changes to
its subscribers on the next frame, so every later test in the file stops seeing its own cache. The
symptom is a test that passes alone and fails after a fake-clock test in the same file. A test whose
requests go through the store while the clock is fake installs it with `installFakeClock()` from
`src/test/outage.ts`, which keeps the frames on the real clock. A bare `vi.useFakeTimers()` is safe
only while nothing asks for a frame, such as a hook's or a component's own timers with no request
starting or ending.

## `vitest run` does not run the whole suite — it excludes `contract/**` and `integration/**`

Five commands, not one. `vitest run` covers the unit and component tests; the `contract/` and
`integration/` directories are excluded by configuration, so a green `vitest run` is not a green
suite and has never been one.

The last three need the real stack up: seed `AzureBankE2E`, the API on **`https://localhost:7215`**,
and the BFF on `:5000` via `dotnet run --project backend/src/AzureBank.Bff --launch-profile http`.

⚠️ **The launch profile is not optional, and an earlier version of this note implied it was.** It is
the only thing setting `ASPNETCORE_ENVIRONMENT=Development`, and two things hang off that: the dev
certificate is trusted on the BFF→API hop, and the session cookie keeps its plain name. Outside
Development `Program.cs` prefixes it `__Host-`, and the cookie is written `Secure`
(`BuildSessionCookieOptions` in the BFF's `BffAuthController.cs`). A run without the profile
fails on TLS, on that BFF→API hop, and the failure does not name the profile. _(Until
2026-10-05 this said a `__Host-` cookie "cannot be set over `http://localhost:5000` at all",
and that such a run therefore fails on login as well. Measured that day, on the Production
images of `compose.yaml` with `compose.demo.yaml`, in headless Chromium 151.0.7922.34: the
browser kept `__Host-AzureBank.Session` (`Secure`, `HttpOnly`, `SameSite=Strict`, host
`localhost`) from a claim made on `http://localhost:5000` and sent it back, and Playwright's
own request context sent it to `localhost` too. So the cookie's name is not what stops a
browser there. What the two suites that run in node, which carry the cookie themselves, meet
at sign-in outside Development was not measured.)_ No cluster override is needed locally:
`appsettings.json` already points cluster
`backend-api` at `https://localhost:7215`. The `--ReverseProxy:Clusters:backend-api:…` arguments you
will see in `ci.yml` are CI-only, because CI moves the API to `:5068`; they are passed as
command-line config rather than environment variables because the cluster id `backend-api` contains
a hyphen and `ReverseProxy__Clusters__backend-api__…` is not a portable shell identifier.

⚠️ **`:5068` is a real port and still the wrong one to use.** The API's `http` launch profile listens
there, and the `https` profile listens on **both** `https://localhost:7215` and
`http://localhost:5068` — so `:5068` answers, which is what makes the mistake survive. Everything
that names the API means 7215: `vitest.contract.real.config.ts`, `.claude/launch.json`, and the
READMEs. This note said `:5068` for a month before anyone ran it.
Authentication is rate-limited to 10 attempts per 60 seconds per IP, so leave ~70 seconds between
suites or the second one fails on a limiter rather than on anything real.

