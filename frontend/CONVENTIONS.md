# Frontend conventions

Rules that hold across the SPA. None is a decision with a weighed alternative: they are the shape
the code already has, written down so that a change reads as a change.

Decisions are in [`docs/adr/`](../docs/adr/README.md): ADR-0019 (SPA and BFF), ADR-0022 (money
mutations), ADR-0023 (runtime validation), ADR-0059 (waits and outages), ADR-0063 (the demo).
Traps that cut across the stack are in [`docs/engineering-traps.md`](../docs/engineering-traps.md).

---

## Data layer

**Cache tags are the invalidation contract.** Two clauses are not obvious: `set-primary`
invalidates the blanket `Account` tag, because changing the primary account moves state on two
rows; and a historical balance (`?at=`) carries no tag, because a past balance cannot go stale.

**Every endpoint unwraps its envelope through `unwrap(envelope, schema?)`, and typing is not
validation.** Without a schema `unwrap` returns `envelope.data` as it came: a compile-time type
is a claim about what the server sends, not a check. A schema passed bare fails closed, in
production too: the four money receipts, the four step-up authorisations, the accounts list, the
transaction summary and all of `/bff/auth/*`, which has no OpenAPI contract behind it. The rest
of `/api/*` passes `devOnly(schema)` and is checked in development and test only. A 2xx whose
`data` is null always throws (ADR-0023).

**Route a 422 on `errorCode`, never on the status alone.** Several unrelated rules share 422, and
the rule that failed decides the message and the field (`src/api/moneyProblem.ts`).

**A 429's countdown is computed in the browser from `retryAfterSeconds`**, in all three places
that have one: the login lockout, the PIN lockout and the per-user rate limit. Never trust an
absolute `lockedUntil` from the server: a skewed browser clock unlocks early or hangs for ever.

**In demo mode a claim adds three 429s, and two are worded with no countdown, by decision.**
`RATE_LIMIT_EXCEEDED` is the BFF's limiter, shared with sign-in: counted down on the sign-in
page, one fixed sentence in the "Start over" dialog. `DEMO_POOL_EMPTY` and `DEMO_DAILY_LIMIT` are
worded by their code, in the app's sentences and never the server's, with no countdown. Nothing
is built for a fourth code, `DEMO_COPY_LIMIT` (ADR-0063, decision 11): each surface shows the
fallback it has.

**One place acts for good on an instant the server gave, against the browser's clock:** the
sign-in page removes a kept demo copy whose `expiresAt` has passed when the page opens. A clock
ahead by more than the copy has left forgets a living copy (ADR-0063, "What the browser keeps in
demo mode", item 5).

**Errors surface through one root `<Toaster>`.** An error toast persists and carries a copyable
`traceId`, the only bridge between what a user saw and the server's trace (ADR-0016). One toast
is a success, "You have a new copy." (`src/features/demo/useNewCopyToast.tsx`): it leaves by
itself and carries no `traceId`.

**Region discipline for async state.** A first load renders a skeleton shaped like the content;
a background refetch keeps the stale data and shows a small inline indicator; a failed region
renders an inline retry and does not blank. A mutation shows its pending state on the button
that started it, never as a page-level overlay.

**A wait the visitor can see gets a `WaitHint`** (ADR-0059, decisions 4 to 6):

- Each page or dialog renders its own, under the control or spinner that started the wait: a
  Fluent modal hides everything outside it from assistive technology. Never inside a
  `role="alert"` or an element an `aria-describedby` points at.
- It says nothing for 5 s, then "Taking longer than usual…", then from 20 s "Still trying…". A
  money send that holds a key is `kind="moneySend"` and adds "Keep this page open."; a PIN check
  before it is a `"write"`.
- Only a read is offered "Stop waiting", from 20 s and only when its host passes
  `onStopWaiting`. A write the server may already be doing is never given up on.
- A read's spinner and error bar follow `readWait`, not `isLoading` and `error`: the bar hides
  while the read runs again. A read whose content takes the wait's place passes `failed`.
- The error bar goes inside the page's `AlertSlot`, an empty `role="alert"` that is on the page
  from the start, never into an alert that mounts already filled. One slot for each page; a
  check whose line sits under its own field, as the recipient check's does, keeps its own.
- Stop and Retry arm `useWaitLanding`, which puts focus on the bar's Retry unless the visitor
  has moved it meanwhile.

**The view that says how a money send ended puts lost focus on its sentence.** The control that
sent is disabled during the send, so the view appears with focus on `body`, and a view that
mounts with its words in it is not announced. The sentence is a `<Text as="p">` with
`tabIndex={-1}` and the ref of `useFocusWhenLost(active)`. It is not a live region, an alert or
a status as well, and no button's `aria-describedby` points at it: each would say the sentence a
second time. `WentThroughView` does this on the two transfer pages, and the same block in the
deposit and withdrawal dialogs, where the action carries a `key` so that it is a new node and
the dialog passes `keepOnOutsidePress`: a press outside closes nothing, the X and Escape do.
Known limits: the landing does not run when another dialog holds focus or the visitor moved it
during the wait; the check view ("We couldn't confirm …") lands no focus; and what is spoken on
a focused paragraph depends on the screen reader (ADR-0059, Validation).

**A dialog gives focus back to the control that opened it.** The Fluent dialogs here are opened
by a state, so Fluent has no trigger to go back to. A component that is mounted for as long as
its dialog is open calls `useReturnFocus()` (`src/hooks/useReturnFocus.ts`); `MoneyDialogShell`
calls it for the three dialogs it frames. Not Fluent's `useRestoreFocusTarget` on the openers:
it acts whenever focus is lost inside the dialog, and a money send loses it on purpose. The
hand-rolled `ConfirmDialog` returns focus by itself. Only Chromium was driven.

## Money and formatting

**Amounts are always positive; direction lives in `type`.** A negative amount in the UI means
that somebody derived a sign the contract already encodes.

**One `formatCurrency`.** EUR, one implementation, no local `Intl.NumberFormat`: a second
formatter is how two screens come to disagree about what €1.005 rounds to.

## UI stack

**Fluent UI v9 with Griffel, and nothing else.** No third-party toast, skeleton, grid or modal
library: a second system means two focus models, two theme sources and two sets of accessibility
behaviour.

**Colour belongs in `src/theme/` and nowhere else.** Lint refuses a hex colour or an `rgb()`,
`rgba()`, `hsl()` or `hsla()` call in a string anywhere else. Use a Fluent token, or the local
palette in `src/theme/tokens.ts` for what Fluent has no name for. Lint does not see a named
colour, `color-mix()` or a 3- or 4-digit hex inside a longer string: those are caught by reading.

**MSW is a test tool.** `dev:mock` is for click-throughs and demos, never the development path:
a frontend developed only against mocks drifts from the real contract.

## Demo mode

The public demo is one build with one switch, and the switch is not in the build (ADR-0063).

**The page says whether it is the demo, with one tag, and `isDemoMode()` is its only reader.**
`<meta name="azurebank-demo" content="true">` in the page's head turns the demo on; any other
`content`, or no tag, does not. The BFF puts the tag in where `Demo:Enabled` is set; the Vite dev
server adds it only when started with `AZUREBANK_DEMO=true` in its environment; a build never
carries it (`vite.config.ts`). `isDemoMode()` (`src/features/demo/demoMode.ts`) reads the page
each time it is asked: ask it in the component or the handler that needs the answer, never once
when a module loads.

**The demo keeps one key in `localStorage`, `azurebank.demoCopy`**, with a claimed copy's
sign-in details and nothing else. It is written in one place, where a claim's answer arrives
(`src/features/auth/sessionMiddleware.ts`), and read through
`src/features/demo/demoCopyStorage.ts` alone. Read the key at each ask and never copy it into a
state or a module variable: that is how a second tab would go on holding a password the first
was told to forget. Every kept value is rendered as text, the kept `pin` is never rendered, and
off the demo the key is never read. `SECURITY.md` names this key as the one exception, in demo
mode, to its rule on web storage, and ADR-0063, "What the browser keeps in demo mode", has its
shape and when it is removed.

**Inside `src/features`, the demo's helpers are imported by file** (`../demo/demoCopyStorage`,
`../demo/demoMode`). The barrel `src/features/demo/index.ts` hands out the four components only:
the session middleware imports the storage module, and through the barrel it would pull every
component in behind it.

**A test turns the demo on with `enableDemoMode()`** and leaves a kept copy with
`rememberDemoCopy(...)` (`src/test/demoMode.ts`). That helper types the tag's name and the key
out and imports neither from the product, on purpose. `src/test/setup.ts` takes the tag off and
empties the key after every test. With the tag on the page the mock is the demo too: it signs in
the owner of a claimed copy and nobody else (`accountForLogin` in `src/mocks/handlers.ts`).

**The demo's sentences are in `src/features/demo/demoWords.ts`**, and the digits of the demo PIN
in `demoPin.ts` beside it. What the demo spells as the backend does is held against the
backend's source by `src/features/demo/demoContract.test.ts`. A file that is not a test and
names one of the demo's codes in a string, and any file that types a refusal out with its
sentence, has to be written into that test's expectations: until then it fails and names the
file.

## Testing with Fluent and jsdom

None of these fails in a way that points at the cause.

- **Never put `contentBefore` on an input inside a dialog.** Typing into it closes the modal, and
  the failure reads as "the element disappeared".
- **`userEvent.type` truncates Fluent inputs.** Use `fireEvent.change` with the complete value.
  The symptom is an assertion on a string that is missing its first characters.
- **Never assert on `input.value` under react-hook-form's `register`.** The value in the DOM is
  not necessarily the value in form state. Assert on submitted values or on rendered output.
- **The `matchMedia` stub is load-bearing** (`src/test/viewport.ts`, installed by
  `src/test/setup.ts`). It reports a 1024 px viewport, the desktop tree, and reduced motion as
  preferred, so Fluent's dialogs enter and leave with no transition. `setViewportWidth` and
  `setReducedMotion` change either for one test. After any async transition, query with
  `findBy*` or `waitFor`. jsdom reads no CSS media query, so an element a stylesheet hides behind
  a breakpoint is found only with `{ hidden: true }`.
- **A fake clock can stop jsdom's animation frames for the rest of the file.** RTK hands a
  request's store changes to its subscribers on the next frame, and jsdom's frame interval,
  started under a fake clock, dies when the clock is restored. The symptom is a test that passes
  alone and fails after a fake-clock test in the same file. Where requests go through the store
  while the clock is fake, install it with `installFakeClock()` from `src/test/outage.ts`. A bare
  `vi.useFakeTimers()` is safe only while nothing asks for a frame.

The traps of jsdom's missing layout are in
[`docs/engineering-traps.md`](../docs/engineering-traps.md#jsdom).

## `vitest run` does not run the whole suite — it excludes `contract/**` and `integration/**`

The suite is five commands: `npm test`, `test:contract:mock`, `test:contract:real`,
`test:integration` and `test:e2e` ([`README.md`](README.md#check-it)). `vitest run`, which is
`npm test`, covers the unit and component tests only: a green `vitest run` is not a green suite.
A sixth command, `npm run test:e2e:demo`, is no part of it: it is run by hand, against the
compose stack with the demo on.

The last three need the real stack: a seeded `AzureBankE2E`, the API on
**`https://localhost:7215`**, and the BFF on `:5000` through
`dotnet run --project backend/src/AzureBank.Bff --launch-profile http`.

- **The launch profile is not optional.** It alone sets `ASPNETCORE_ENVIRONMENT=Development`,
  without which the development certificate is not trusted on the hop from the BFF to the API:
  the run fails on TLS there, and the failure does not name the profile. Outside Development the
  session cookie is also `__Host-` and `Secure`; Chromium still keeps it from
  `http://localhost:5000` (measured on 2026-10-05), and what the suites that run in node meet at
  sign-in there was not measured.
- **`:5068` is a real port and still the wrong one.** The API's `https` profile listens on both
  `https://localhost:7215` and `http://localhost:5068`, so `:5068` answers, which is what makes
  the mistake survive. Everything that names the API means 7215. The
  `--ReverseProxy:Clusters:backend-api:…` arguments in `ci.yml` are CI's alone, because CI moves
  the API to `:5068`.
- **Authentication is rate-limited to 10 attempts in 60 seconds for one address.** Leave about
  70 seconds between suites, or the second one fails on the limiter.
