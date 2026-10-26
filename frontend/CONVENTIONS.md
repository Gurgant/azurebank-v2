# Frontend conventions

Rules that hold across the SPA. None is a decision with a weighed alternative: they are the shape
the code already has, written down so that a change reads as a change.

Decisions are in [`docs/adr/`](../docs/adr/README.md): ADR-0019 (SPA and BFF), ADR-0022 (money
mutations), ADR-0023 (runtime validation), ADR-0059 (waits and outages), ADR-0063 (the demo).
Traps that cut across the stack are in [`docs/engineering-traps.md`](../docs/engineering-traps.md).

---

## Data layer

**Cache tags are the invalidation contract.** Three clauses are not obvious: `set-primary`
invalidates the blanket `Account` tag, because changing the primary account moves state on two
rows; a historical balance (`?at=`) carries no tag, because a past balance cannot go stale; and a
mutation refused with `ACCOUNT_NOT_FOUND` invalidates the accounts list, the mint of an
authorisation, the deletion and the reveal of the full number included, because that answer says
an account still on screen is closed.

**A closed account is said in one sentence, and never chosen around.** "This account is closed.
Choose another account." (`ACCOUNT_CLOSED` in `src/api/problemMessages.ts`) is what a visitor
reads when their own account is closed under a screen; the server's sentence carries the
account's identifier and is not shown. A page clears the choice and waits for another: no default
account takes the place of one that is gone. A dialog opened for the account closes, and the
sentence is said in the root `<Toaster>`. When the mint of a new authorisation under a held
idempotency key answers `ACCOUNT_NOT_FOUND`, the check view comes first, and lets the key go
(ADR-0022, decision 6). A transfer page shows it on that answer, and says the sentence once the
visitor is back on the form. The withdrawal dialog shows it on that answer and on its check on
Continue under a held key, and closes with the sentence from its X or from the view's last
button: "Close" while the withdrawal may still go through, "It didn't go through — try again"
once nothing under the key can commit. "Check recent transactions" leaves for the history on
either, with nothing more said. A send that answers `ACCOUNT_NOT_FOUND` holds no key: the page
or the dialog says the sentence at once. The move between the visitor's own accounts has three
sentences of its own beside it, for which of its two accounts is closed. Under the check view
the listing by key answers a closed account 403, and the move then asks its other account.

**An endpoint that answers with data in an envelope unwraps it through
`unwrap(envelope, schema?)`, and typing is not validation.** Two endpoints answer with no
envelope and never pass through it: `GET /api/transactions`, a page of results, and
`GET /bff/auth/session-status`. Each checks its body with its own schema: the first in
development and test only, the second in production too. Without a schema `unwrap` returns
`envelope.data` as it came: a compile-time type is a claim about what the server sends, not a
check. A schema passed as it is fails closed, in production too: the four money receipts, the
four step-up authorisations, the accounts list, the transaction summary and what `/bff/auth/*`
answers with data, which has no OpenAPI contract behind it. One endpoint under `/bff/auth/*` is
not among them: the AzureTag rename has a schema generated from the API's contract and passes
`devOnly(schema)`, as does the rest of `/api/*` that answers with data, checked in development
and test only. Where `unwrap` runs, a 2xx whose `data` is null throws (ADR-0023); set-primary,
account deletion, sign-out and set-PIN answer with no data and do not call it. One more page
is checked fail-closed: the listing by key that the check view asks (`movementsPageSchema`, both
members required), which says nothing from a page it cannot read.

**Route a 422 on `errorCode`, never on the status alone.** Several unrelated rules share 422, and
the rule that failed decides the message and the field (`src/api/moneyProblem.ts`).

**A 429's countdown is computed in the browser from `retryAfterSeconds`**, for all three refusals
that have one: the login lockout, the PIN lockout and the BFF's authentication limiter. Never
trust an absolute `lockedUntil` from the server: a skewed browser clock unlocks early or hangs
for ever.

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

**An error with no surface of its own goes to one root `<Toaster>`.** An error toast persists and
carries a copyable `traceId` when the problem has one, the only bridge between what a user saw
and the server's trace (ADR-0016). One toast is a success, "You have a new copy."
(`src/features/demo/useNewCopyToast.tsx`): it leaves by itself and carries no `traceId`. One
error toast carries none either: the closed account's, which is the app's sentence alone.

**Region discipline for async state.** A first load renders a skeleton shaped like the content;
a background refetch keeps the stale data and shows a small inline indicator; a failed region
renders an inline retry and does not blank. A mutation shows its pending state on the button
that started it, never as a page-level overlay.

**A wait the visitor can see gets a `WaitHint`** (ADR-0059, decisions 4 to 6):

- Each page or dialog renders its own, under the control or spinner that started the wait: a
  Fluent modal hides everything outside it from assistive technology. Never inside a
  `role="alert"` or an element an `aria-describedby` points at.
- It says nothing for 5 s (6 s for the session check at start-up), then "Taking longer than
  usual…", then from 20 s "Still trying…". A money send that holds a key is `kind="moneySend"`
  and adds "Keep this page open."; a PIN check before it is a `"write"`.
- Only a read is offered "Stop waiting", from 20 s and only when its host passes
  `onStopWaiting`. A write the server may already be doing is never given up on.
- A page read's spinner and error bar follow `readWait`, not `isLoading` and `error`: the bar hides
  while the read runs again. A read whose content takes the wait's place passes `failed`.
- The error bar goes inside the page's `AlertSlot`, an empty `role="alert"` that is on the page
  from the start, never into an alert that mounts already filled. One slot for each page; a
  check whose line sits under its own field, as the recipient check's does, keeps its own, and
  so does History's "Load more", whose failure keeps the rows already read and is said under its
  button.
- Stop and Retry arm `useWaitLanding`, which puts focus on the bar's Retry unless the visitor
  has moved it meanwhile. The check view's "Check again" arms it too, and so does the view's own
  ask at the bound when focus was on the view's first button: that ask disables "Check again".
  Either lands on the view's first button as it is once the answer is drawn.

**The view that says a send went through with no receipt puts lost focus on its sentence, and
the check view on its title.** The control that sent is disabled during the send, so either view
appears with focus on `body`, and a view that mounts with its words in it is not announced. The
sentence, and the title "We couldn't confirm your …", are each a `<Text as="p">` with
`tabIndex={-1}` and the ref of `useFocusWhenLost(active)`. Neither is a live region, an alert or
a status as well, and no button's `aria-describedby` points at either: each would say the words
a second time. `WentThroughView` and `ResultUnknownView` do this on the two transfer pages, and
the same blocks in the deposit and withdrawal dialogs, where the went-through view's action
carries a `key` so that it is a new node, and the dialog passes `keepOnOutsidePress` under both
views: a press outside closes nothing, the X and Escape do. From the title Escape closes the dialog,
as from anywhere inside it. The actions of both views, in the dialogs and on the transfer pages,
drop a press, a key's included, for 500 ms after the view appears and after each change of what it
draws: of its verdict and what that offers, of the size of the actions, of the box that holds them
or of a box beside them, of the viewport where that moves the actions, or of where the actions
are when the press comes. They stay enabled and named meanwhile, and a dropped press takes no
focus (`useSettledPress`): at 360×800 the second press of a double click on Deposit landed on
"Check recent transactions" once the check view had grown to say "not yet". The form that "It
didn't go through — try again" brings back in a dialog, or "It didn't go through — start over"
on a transfer page, waits once too, on its actions and on its accounts and amounts: the
deposit's Deposit sends what they hold, and the withdrawal's Continue and the transfer pages'
Review Transfer lead to confirming them. In the dialogs a view keeps the height of its words,
and when it and its actions are taller than the dialog the surface scrolls as a whole: every word
and every action is reached by scrolling, at 320×256 too, and the words come before the actions.
The view of a movement the listing found by key is the went-through view with the movement under
the sentence, and lands focus the same way. Known limits: the landing does not run when another
dialog holds focus or the visitor moved it during the wait; the check view's title takes focus
before the listing by key has answered, so what that listing answered is said in a
`role="status"` region of its own (`MovementCheckStatus`); and what is spoken on a focused
paragraph depends on the screen reader (ADR-0059, Validation).

**Every receipt puts the focus its send lost on its title, as the went-through view does on its
sentence.** In `DepositDialog`, `WithdrawDialog`, `TransferPage` and `InternalTransferPage` the
title is a `<Text as="p">` with `tabIndex={-1}` and the ref of `useFocusWhenLost(!!success)`, in
no live region and pointed at by no `aria-describedby`, and it draws the same ring after a send
by keys.

**A dialog gives focus back to the control that opened it.** The Fluent dialogs here are opened
by a state, so Fluent has no trigger to go back to. A component that is mounted for as long as
its dialog is open calls `useReturnFocus()` (`src/hooks/useReturnFocus.ts`); `MoneyDialogShell`
calls it for the three dialogs it frames. Not Fluent's `useRestoreFocusTarget` on the openers:
it acts whenever focus is lost inside the dialog, and a money send loses it on purpose. The
hand-rolled `ConfirmDialog` returns focus by itself. The session warning gives none back: the
clock opens it, and no control. Only Chromium was driven.

## Money and formatting

**Amounts are always positive; direction lives in `type`.** A negative amount in the UI means
that somebody derived a sign the contract already encodes.

**One `formatCurrency`.** EUR, one implementation, and no local `Intl.NumberFormat` but the
whole-euro bound in `src/forms/moneySchemas.ts`: a second formatter is how two screens come to
disagree about what €1.005 rounds to.

## UI stack

**Fluent UI v9 with Griffel, and nothing else.** No third-party toast, skeleton, grid or modal
library: a second system means two focus models, two theme sources and two sets of accessibility
behaviour.

**In TypeScript, colour belongs in `src/theme/` and nowhere else.** The unit test
`src/theme/colourLiterals.test.ts` refuses a hex colour or an `rgb()`, `rgba()`, `hsl()` or
`hsla()` call in a string or a template literal of any other `.ts` or `.tsx` file that lint reads,
with its file and line; the files exempt, and why, are named in it. Use a Fluent token, or the
local palette in `src/theme/tokens.ts` for what Fluent has no name for. The test does not see
every form: a named colour, `color-mix()` and a 3- or 4-digit hex inside a longer string are among
those caught only by reading. Outside TypeScript it reads nothing: the theme variables in
`src/index.css` are generated from the palettes and held to them by
`src/theme/themeVariables.test.ts`, and `index.html` and `public/site.webmanifest` name the
colours of the browser's own surfaces.

**Words that more than one surface says have one owner**, a named export in the module of the
feature that owns them: the act of signing out in `src/components/layout/signOutAction.ts`, what
the PIN confirms in `src/features/auth/pinWords.ts`, the demo's sentences in
`src/features/demo/demoWords.ts` and the app's sentences for a refusal in
`src/api/problemMessages.ts`. A test that finds a control by its name types the name out and
never imports it: an imported name follows a wrong rename, and the test still passes. Sign in and
Sign out, never Log in or Log out: lint refuses the second pair in a string, a template or JSX
text. It needs white space between the two words, a line break included, so `/bff/auth/logout`
and `logout` stay legal.

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
was told to forget. The storage module holds one copy that way itself, the one a browser refused
to store: while it does, that tab does not read the key, and what another tab forgot or replaced
does not reach it. Every kept value is rendered as text, the kept `pin` is never rendered, and
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
backend's source by `src/features/demo/demoContract.test.ts`. A file under `src` that is not a
test and names one of the demo's codes in single quotes, and any file there that types a refusal
out as `errorCode: '…', detail: '…'`, has to be written into that test's expectations: until
then it fails and names the file.

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
- **jsdom's animation frames run on the real clock.** RTK hands a request's store changes to its
  subscribers on the next frame, and jsdom's frame interval, started under a fake clock, dies
  when the clock is restored and stays dead for the rest of the page's life: without isolation,
  for every file a worker runs after that one. `src/test/setup.ts` starts the frames on the real
  clock before any test runs (`src/test/frames.ts`). Where requests go through the store while
  the clock is fake, install it with `installFakeClock()` from `src/test/outage.ts`: it lets real
  time pass, so a response can arrive.
- **A test passes whatever ran before it.** The files of a worker share one page and one module
  registry (`isolate: false` in `vitest.config.ts`), so a test must clean up what it adds
  outside the tree it renders: restore what it spied on or stubbed, and leave no node, style
  rule or global for the next file to find. With `--sequence.shuffle --sequence.seed=<n>` Vitest
  runs the tests in a random order, and a pass for any `n` shows that no test leans on another;
  which files meet in a worker depends on timing, so a failure may not repeat. A file that
  empties the registry (`vi.resetModules()`) hands the next file new copies of the app's modules
  on the same page, which is why `src/test/setup.ts` puts its stubs on the page again for each
  file. A pair of tests whose order is the point keeps it with `{ shuffle: false }` on its
  `describe`.

The traps of jsdom's missing layout are in
[`docs/engineering-traps.md`](../docs/engineering-traps.md#jsdom).

## Two TypeScripts

**The type gate runs on the native compiler, and everything that loads the compiler's API stays
on TypeScript 5.** `typescript` (`~5.9.3`) is what `typescript-eslint`, `openapi-typescript` and
the editor load. `@typescript/native` is `typescript@7.0.2` under another name, and `npm run
build` calls its binary by path (`node node_modules/@typescript/native/bin/tsc -b`). Both
packages declare a `tsc` bin and npm links only one into `node_modules/.bin`, so `npx tsc` can
be either: never call it, and never move `typescript` itself to 7 until those tools support it.

## Two linters

**`npm run lint` runs Oxlint, then ESLint, and a rule runs in one of them.** `.oxlintrc.json`
lists what Oxlint runs, and `eslint.config.js` ends with `eslint-plugin-oxlint`, which turns
those same rules off in ESLint: to move a rule, edit the first file and nothing else. A rule
goes to Oxlint only once both report the same rules on a file that breaks each one. What stays
in ESLint is the React Compiler's rules (Oxlint's disagree with them on this code: purity, refs,
`set-state-in-effect`, `incompatible-library`), `react-refresh/only-export-components`,
`no-octal` and the Sign in / Sign out rule, which has no Oxlint equivalent. While any
Compiler rule is on in ESLint, its Babel pass runs on every file.

## `vitest run` does not run the whole suite — it excludes `src/contract/**` and `src/integration/**`

The suite is five commands: `npm test`, `test:contract:mock`, `test:contract:real`,
`test:integration` and `test:e2e` ([`README.md`](README.md#check-it)). `vitest run`, which is
`npm test`, covers the unit and component tests only: a green `vitest run` is not a green suite.
A sixth command, `npm run test:e2e:demo`, is no part of it: it runs against the compose stack
with the demo on, in CI's `demo-stack` job on every pull request and by hand.

The last three need the real stack: a seeded `AzureBankE2E`, the API on
**`https://localhost:7215`**, and the BFF on `:5000` through
`dotnet run --project backend/src/AzureBank.Bff --launch-profile http`.

- **The launch profile is not optional.** In that command it alone sets
  `ASPNETCORE_ENVIRONMENT=Development`, without which the development certificate is not trusted
  on the hop from the BFF to the API: the run fails on TLS there, and the failure does not name
  the profile. Outside Development the session cookie is also `__Host-` and `Secure`; Chromium
  still keeps it from `http://localhost:5000` (measured on 2026-10-05), and what the suites that
  run in node meet at sign-in there was not measured.
- **`:5068` is a real port and still the wrong one.** The API's `https` profile listens on both
  `https://localhost:7215` and `http://localhost:5068`, so `:5068` answers, which is what makes
  the mistake survive. The BFF's `appsettings.json` points at 7215. The
  `--ReverseProxy:Clusters:backend-api:…` arguments in `ci.yml` are CI's, because CI moves the
  API to `:5068`; `compose.yaml` does the same with environment variables.
- **Authentication is rate-limited to 10 attempts in 60 seconds for one address.** Leave about
  70 seconds between suites, or the second one fails on the limiter.
