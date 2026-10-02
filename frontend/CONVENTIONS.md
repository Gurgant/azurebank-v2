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

**Errors surface through one root `<Toaster>`.** Error toasts persist rather than auto-dismissing,
and they carry a copyable `traceId` — that identifier is the only bridge between what a user saw and
the server-side trace (ADR-0016, ADR-0017). A toast that drops the traceId breaks the only link.

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
Development `Program.cs` prefixes it `__Host-`, which **cannot be set over `http://localhost:5000`**
at all — so a run without the profile fails on TLS and then on login, and neither failure names the
profile. No cluster override is needed locally: `appsettings.json` already points cluster
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

