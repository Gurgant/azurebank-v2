# Test the demo as an automated tester

For a tester that drives a browser or sends requests and has no memory of this project. Exact
and ordered. A person who wants to try the demo by hand reads [Try the demo](try-the-demo.md).

**What "seen" means here.** Every name, text, status, code and header below was met on
2026-10-06 on the stack of `compose.yaml` with `compose.demo.yaml`, at `http://localhost:5000`,
in a Chromium browser without a window driven by a browser automation tool, one copy, one
worker. What that walk did not meet is marked **read**, with the file it is read from.
[Where each fact comes from](#where-each-fact-comes-from) is the last section.

## 1. Before you start

**One parameter: `BASE`**, the demo's base address. Every path below is under it. On a stack
started on one machine with the two compose files it is `http://localhost:5000`.

**The host must be the one the cookie was set for.** The session is one cookie,
`__Host-AzureBank.Session`: `Secure`, `HttpOnly`, `SameSite=Strict`, path `/`, no expiry. The
copy's sign-in details are in the browser's local storage for that origin, under the key
`azurebank.demoCopy`. So use one origin from the first request to the last. On one machine use
the name `localhost` and never `127.0.0.1`: a Chromium browser kept the `Secure` cookie on
`http://localhost` and sent it back (seen), and the repository's own run notes that its request
client, outside a browser, sends such a cookie over http to `localhost` and not to `127.0.0.1`
(read: [the demo run's configuration](../../frontend/playwright.demo.config.ts)).

**Three checks before anything else.** Stop at the first that fails.

| # | Ask | Expect |
|---|---|---|
| P1 | `GET /health/ready` | 200, and the body is the word `Healthy` |
| P2 | `GET /` | 200, `text/html`, and the demo's tag exactly once |
| P3 | A fresh browser context | No cookie and no local storage for `BASE` |

- **P1.** The word, not the status: the same address answers `200 Degraded` while the API
  behind the BFF is not reachable (read:
  [`restart.setup.ts`](../../frontend/e2e-demo/restart.setup.ts)).
- **P2.** The tag is `<meta name="azurebank-demo" content="true">`, before `</head>`. A page
  without it is not the demo: stop. There the claim answers 404 (read: ADR-0063, decision 1).

**Never keep the password.** A claim's answer holds the copy's password. So does the browser's
local storage, and so does the dashboard while "Show sign-in details" is open. Compare, never
print. Record no trace, no video, no picture and no dump of the page while those details are
open, and never log the body of a claim's answer. If you must come back to the copy later, keep
its email and password in a file of your own, outside the repository.

## 2. The budgets you must respect

The demo is small on purpose, and on one machine every tester shares it.

| Budget | Number | Counted for | This guide spends |
|---|---|---|---|
| Copies | 10 in a rolling 24 hours | one client address | 1 |
| Changes in one copy | 200 in its life | the copy | 17, or 20 with step 21 |
| The sign-in doors | 10 in a sliding 60 seconds | one client address | 3 to 5 |
| Handle look-ups | 20 in a sliding 60 seconds | the signed-in user | under 10 |
| All requests | 300 in a fixed 60 seconds | one client address | far under |
| Wrong PINs in a row | 3, then locked 15 minutes | the copy's owner | 0, or 3 at the end |
| Wrong passwords in a row | 5, then locked 15 minutes | the account | 0 |
| Sent to other people | 5,000.00 in a UTC day | the copy's owner | 1.00 |
| A session | 15 minutes idle, 60 in all | the session | under half an hour |

**Why each one matters.**

- **Copies.** A client is an address as the BFF sees it. Under the two compose files on one
  machine the BFF saw one address for every browser, so every tester on that machine draws
  from the same ten (read: the header of [`compose.demo.yaml`](../../compose.demo.yaml)). A
  copy you claim is one somebody else cannot. **Claim once in a run. Never claim in a loop.
  Never use "Start over" to get a clean state: it is a claim.**
- **Changes.** The API counts every request of the signed-in user that is not a GET, HEAD,
  OPTIONS or TRACE, whatever it then answers, and every reveal of an account number. Sign-in,
  sign-out and the claim are not counted (read: ADR-0063, decision 11). Nothing gives a change
  back. **Never loop over a request that changes something.** The walk itself sent 25 such
  requests, five more than the steps below: two deposits made from the keyboard and, once the
  PIN's lock was over, one more check of the PIN with two reveals.
- **The sign-in doors.** `POST /bff/auth/login`, `/bff/auth/register`, `/bff/auth/demo/claim`
  and `/bff/auth/reauthenticate`, and `PATCH /bff/auth/azuretag`, share one allowance for one
  address (read: [`BffAuthController.cs`] and [the BFF's `Program.cs`]). A refused request
  spends one too. **Leave six seconds between two of them. After a 429, wait the seconds its
  `Retry-After` names and ask once more, not more.**
- **One worker.** One browser context at a time, the steps in order. Each step stands on the
  one before, and the budgets above count every parallel request.
- **Never a load test, a fuzzer or a crawler.** The limits above are the product's own
  protection, held by its own tests. Reaching them on a shared stack only locks the others out.
- **Never the repository's demo run on a stack somebody else uses.** `npm run test:e2e:demo`
  restarts two containers and claims two copies. See
  [section 7](#7-what-the-repositorys-own-suites-cover).
- **Do not change the PIN, do not delete an account, do not confirm "Start over".** A changed
  PIN makes the page's own hint wrong for the rest of the run, and the other two are not needed
  for any assertion here.

## 3. How to read the steps

- **A control** is given by its role and its accessible name. Match the name **exactly**: three
  controls have a name that starts with "Start over".
- **`TOTAL`** is the text of the first level-1 heading of the dashboard, for example
  `€14,750.00`. Money is written with `€`, commas for thousands and two decimals.
- **`H`** is the first of the copy's two contacts, read off the dashboard. Handles differ in
  every copy.
- **Arm a wait for an answer before the press that sends the request**, and read the status from
  the answer, never from the screen.
- **Each "Assert" line is one assertion.** A step is done when all of them hold.
- **A text in code style that runs over two lines here is one text.** Read the line break as
  one space.
- The page asks `GET /bff/auth/me` when it loads. With no session that is a 401, and the browser
  writes a console error for it. That line is expected.

## 4. The steps

"Doors" is the requests a step sends at the sign-in doors. "Changes" is the requests the copy's
budget counts, by the rule of section 2: the count itself is kept in the database, and the walk
did not read it.

| # | Step | Doors | Changes |
|---|---|---|---|
| 1 | The sign-in page, with no copy kept | 0 | 0 |
| 2 | `/register` is closed | 0, or 1 | 0 |
| 3 | Claim a copy | 1 | 0 |
| 4 | The dashboard | 0 | 0 |
| 5 | The sign-in details, compared | 0 | 0 |
| 6 | Show a full account number | 0 | 2 |
| 7 | Deposit | 0 | 1 |
| 8 | Withdraw | 0 | 2 |
| 9 | Pay a contact | 0 | 2 |
| 10 | Move money between the two accounts | 0 | 2 |
| 11 | History and its filters | 0 | 0 |
| 12 | The accounts page | 0 | 2 |
| 13 | Settings and the theme | 0 | 0 |
| 14 | A phone's width | 0 | 0 |
| 15 | Three refusals of a transfer | 0 | 1 |
| 16 | One request, sent twice | 0 | 5 |
| 17 | "Start over", opened and not confirmed | 0 | 0 |
| 18 | Sign out, then the Back button | 0 | 0 |
| 19 | "Continue with my copy" | 1 | 0 |
| 20 | Another browser, with the copy's own details | 1, or 2 | 0 |
| 21 | Wrong PINs, last and only if wanted | 0 | 3 |
| 22 | Leave | 0 | 0 |

### Step 1. The sign-in page, with no copy kept

Action: open `/login` in the fresh context.

Assert:

- the title is `Sign in · AzureBank`, and a level-1 heading reads `Welcome back`;
- the text `Try the demo with one click. No sign-up needed.` is on the page;
- there is one button `Try the demo`, no button `Continue with my copy`, and no link
  `Create account`;
- the button's description (`aria-describedby`) reads `You get a private copy of a demo bank
  account with invented money.`;
- the text `Already have a copy's email and password? Sign in here.` is on the page, with a
  textbox `Email address`, a textbox `Password` and a button `Sign in`;
- `localStorage["azurebank.demoCopy"]` is `null`.

### Step 2. `/register` is closed

Action: open `/register?next=%2Fdashboard#frag`.

Assert: the address ends at `/login`, with no query and no fragment.

Only if you can spare a request at the doors: `POST /bff/auth/register` with the JSON body `{}`.

Assert: 403; `errorCode` is `REGISTRATION_CLOSED`; `detail` is `Registration is closed on this
demo.`; `instance` is `/bff/auth/register`.

### Step 3. Claim a copy

Action: arm a wait for the answer to `POST /bff/auth/demo/claim`. Press the button
`Try the demo`.

Assert:

- the request's body is `{}`, sent as `application/json`;
- the status is 200, with `Cache-Control: no-store` and `Pragma: no-cache`;
- `message` is `Demo copy claimed`;
- `data` has `user`, `expiresAt` and `copy`; `data.user` has `id`, `email`, `firstName`,
  `lastName`, `azureTag` and `hasPin`, and `hasPin` is `true`;
- `data.copy` has `email`, `password`, `pin`, `contacts` and `expiresAt`;
- `data.copy.pin` is `123456`, and `data.copy.contacts` holds two handles;
- `data.copy.email` matches `^demo-[a-z0-9]{16}@azurebank\.example$`;
- `data.copy.password` matches `^[A-Za-z0-9]{4}(-[A-Za-z0-9]{4}){3}$`. Assert the shape, as a
  boolean, and nothing else of it;
- `data.copy.expiresAt` ends in `Z` and is 24 hours after the request, give or take a minute;
- the context now holds the cookie `__Host-AzureBank.Session`, and the address is `/dashboard`;
- `localStorage["azurebank.demoCopy"]` is JSON with `v`, `email`, `password`, `pin`, `contacts`
  and `expiresAt`; `v` is 1, and the other five are the five members of `data.copy`.

If the claim is refused, [section 6](#6-the-refusals) has the three codes it can answer. For
`RATE_LIMIT_EXCEEDED` wait as section 2 says and ask once more. For the other two do not ask
again: report the code and stop.

### Step 4. The dashboard

Assert:

- the title is `Home · AzureBank`;
- `TOTAL` is `€14,750.00`, and the text `Across 2 accounts` is on the page;
- the group `Account scope` has three buttons, whose amounts are `€14,750.00`, `€12,450.00` and
  `€2,300.00`;
- a region `Your private copy` has a level-2 heading of that name and three paragraphs:
  1. one that starts `Other visitors can't see this copy. It works until ` and ends
     `, then it is closed and deleted.`, with a date, ` · ` and a time between the two;
  2. `PIN: 123456, unless you changed it`;
  3. one that matches `^Contacts you can pay: @\S+ and @\S+$`. Take `H` from it;
- the region has a button `Show sign-in details`, whose `aria-expanded` is `false`, and a
  button `Start over`;
- the navigation `Main navigation` has a button `Transfer` and the links `Home`, `Accounts`,
  `History`, `Contact` and `Settings`, and the page has a button `Sign out`;
- the page's requests were answered 200: `GET /bff/auth/me`, `GET /api/accounts`,
  `GET /api/transactions?Page=1&PageSize=5` and `GET /api/transactions/summary?FromDate=…`.

Two reads, if wanted. Press `Hide balances`: `TOTAL` is `••••••` and the button is now
`Show balances`; press it. Press the scope button whose name starts with `Checking`: `TOTAL` is
`€2,300.00`; press the one that starts with `All accounts`.

### Step 5. The sign-in details, compared

Action: press `Show sign-in details`. In the page, read the text of the element the button's
`aria-controls` names. Press `Hide sign-in details`.

Assert, as booleans only:

- while open, the button's name is `Hide sign-in details` and its `aria-expanded` is `true`;
- the line matches `^Email: (\S+) · Password: (\S+)$`;
- its two values are the `email` and the `password` of `localStorage["azurebank.demoCopy"]`;
- once hidden, `aria-expanded` is `false` again.

Nothing may look at the page between the two presses but that one read.

### Step 6. Show a full account number

Action: follow the link `Accounts`. Press the button
`Reveal full account number for Main Savings`.

Assert:

- `GET /api/accounts/{id}/full-number` answers 403 with the headers `X-Auth-Level-Required: 2`
  and `X-Auth-Level-Current: 1`. Its body has `type` `STEP_UP_REQUIRED` and `title`
  `PIN Verification Required`, and no `errorCode`;
- an alertdialog `Verify it's you` opens, with six textboxes named `Digit 1 of 6` to
  `Digit 6 of 6`, the text `Demo PIN: 123456, unless you changed it.`, a button `Cancel` and a
  button `Verify` that is disabled.

Action: type `123456` into `Digit 1 of 6`, one key at a time. The boxes move on by themselves,
and the sixth digit sends.

Assert:

- `POST /bff/auth/verify-pin` answers 200 with `data.verified` `true`, `data.authLevel` 2 and
  `message` `PIN verified successfully`, and the dialog closes;
- `GET /api/accounts/{id}/full-number` answers 200 with `Cache-Control: no-store`;
- one text on the page matches `AB-\d{4}-\d{4}-\d{2}`, and there are two new buttons,
  `Hide account number for Main Savings` and `Copy account number for Main Savings`.

Changes counted: 2, the PIN's check and the reveal that answered 200. The 403 is the BFF's own
answer and never reaches the API (read:
[`AuthLevelMiddleware.cs`](../../backend/src/AzureBank.Bff/Middleware/AuthLevelMiddleware.cs)).

A second reveal right after the first opened no dialog and answered 200 (seen once). The BFF's
settings give a checked PIN five minutes (read: [The BFF's `appsettings.json`]), so a later
step that reveals a number may or may not meet the dialog: assert on the number, not on the
dialog.

### Step 7. Deposit

Action: follow `Home`. Press the first button named `Deposit`. In the dialog `Deposit Money`,
fill the textbox `Deposit amount` with `50` and the textbox `Description` with words of your
own.

Assert: the text `New balance: €12,500.00` is in the dialog, and its last button is now named
`Deposit €50.00`.

Action: press `Deposit €50.00`, once.

Assert:

- `POST /api/transactions/deposit` answers 201, and was sent with an `Idempotency-Key` header;
- a dialog `Deposit Complete` holds `Deposit Successful!`, `+€50.00`, `To Main Savings` and
  `New balance €12,500.00`;
- after `Done`, `TOTAL` is `€14,800.00`: up by the amount, exactly.

Three reads, if wanted, typed and not sent. `100000.01`: an alert `Maximum deposit is
€100,000.`, and the button is disabled. `0`: an alert `Minimum deposit is €0.01.` `1.005`: the
textbox holds `1.00`.

**A trap, seen.** A deposit with no description is listed under "Recent activity" as a button
named `Deposit`. From then on `Deposit`, matched exactly, is two buttons on the dashboard.
Describe every deposit, or take the first match.

### Step 8. Withdraw

Action: press `Withdraw`. In the dialog `Withdraw Money`, press the account button whose name
starts with `Checking`, and fill the textbox `Withdraw amount` with `20`.

Assert: the text `Available: €2,300.00` is in the dialog, and a button is named
`Continue · €20.00`.

Action: press it.

Assert: the dialog holds `Verify Withdrawal` and `Enter your 6-digit PIN to confirm withdrawing
€20.00 from Checking.`, and a button `Withdraw €20.00` that is disabled.

Action: type `123456`, then press `Withdraw €20.00`. Here the sixth digit sends nothing.

Assert:

- `POST /api/transactions/withdraw/authorizations` answers 201;
- `POST /api/transactions/withdraw` answers 201, sent with `Idempotency-Key` and
  `Step-Up-Authorization`;
- a dialog `Withdrawal Complete` holds `Withdrawal Successful!`, `-€20.00`, `From Checking` and
  `New balance €2,280.00`;
- after `Done`, `TOTAL` is down by the amount, exactly.

### Step 9. Pay a contact

Action: in `Main navigation` press the button `Transfer`. Under the level-1 heading
`Send Money`, fill the textbox `Recipient handle` with `H`, without the `@`, and press `Verify`.

Assert: `GET /api/users/H` answers 200 with `data.exists` `true`, and the text `@H` is on the
page.

Action: fill the textbox `Transfer amount` with `1`. Press `Review Transfer`.

Assert: the level-1 heading is `Review Transfer`, and the page holds `Amount €1.00` and
`You'll confirm with your PIN on the next step.`

Action: press `Continue`.

Assert: the level-1 heading is `Confirm with PIN`, and one text matches
`^Demo PIN: \d{6}, unless you changed it\.$`.

Action: arm two waits, for `POST /api/transfers/authorizations` and for `POST /api/transfers`.
Type the six digits into `Digit 1 of 6`. The sixth sends both.

Assert:

- both answer 201, and the second was sent with `Idempotency-Key` and `Step-Up-Authorization`;
- the level-1 heading is `Transfer Complete`, and the page holds `Transfer Sent!`, `-€1.00` and
  a reference that matches `TXN-\d{8}-[0-9A-Z]{11}`;
- after `Done` the address is `/dashboard` and `TOTAL` is down by 1.00, exactly.

### Step 10. Move money between the two accounts

Action: press `Transfer`, then the button `Between your own accounts`.

Assert: the level-1 heading is `Move Money` and the address is `/transfer/internal`. The button
`To Main Savings` is disabled while `From Main Savings` is pressed.

Action: press `To Checking`. Fill `Transfer amount` with `100`. Press `Review Transfer`, then
`Continue`, then type the PIN.

Assert:

- `POST /api/transfers/internal/authorizations` and `POST /api/transfers/internal` answer 201;
- the level-1 heading is `Transfer Complete`, and the page holds `Transfer Complete!`,
  `€100.00`, `From Main Savings` and `To Checking`;
- after `Done`, `TOTAL` is what it was before this step;
- in `Account scope`, Main Savings is down by 100.00 and Checking is up by 100.00.

### Step 11. History and its filters

Action: follow the link `History`.

Assert:

- the level-1 heading is `History`, and `GET /api/transactions?Page=1&PageSize=20` answers 200;
- the group `Filter transactions by type` has the buttons `All`, `Deposits`, `Withdrawals` and
  `Transfers`, and `All` is pressed;
- the first rows are this run's, newest first: the move in and out, `To @H` with `-€1.00`, the
  withdrawal, and the deposit by its description.

Action: press `Deposits`, `Withdrawals`, `Transfers` and `All`, one after another.

Assert: the one pressed has `aria-pressed` `true`; the rows are of that kind only; and no
request is sent. The filter works on the rows already loaded.

Action: press `Load more`.

Assert: `GET /api/transactions?Page=2&PageSize=20` answers 200, and the table has more rows.

Action: press the row button named by your deposit's description.

Assert: the address is `/transactions/{id}`; the level-1 heading is `Transaction Details`; the
page holds `+€50.00`, `Completed` and a transaction number that starts with `TXN-`. The button
`Back` leads to `/history`.

### Step 12. The accounts page

Action: follow `Accounts`. Press the button `Add account`. In the dialog `Add New Account`, fill
the textbox `Account name`, choose `Savings` in the combobox `Account type`, and press
`Create Account`.

Assert: `POST /api/accounts` answers 201, and the page has a new account with `€0.00` and a
button `Account actions for <its name>`.

Action: press that button.

Assert: the menu has three items, `Rename`, `Set as primary` and `Delete`. The menu of the
primary account has two, `Rename` and `Delete`.

Action: choose `Rename`. In the dialog `Rename Account`, change the textbox `Account name` and
press `Save`.

Assert: `PATCH /api/accounts/{id}` answers 200, and the new name is on the page.

Action: open the menu again and choose `Delete`.

Assert: a dialog `Delete account?` holds `You're about to delete "<its name>". This can't be
undone.` Press `Cancel`: the account is still on the page, and nothing was sent.

From here on the dashboard reads `Across 3 accounts`.

### Step 13. Settings and the theme

Action: follow `Settings`.

Assert: the level-1 heading is `Settings`; the page holds `Public handle` and the copy's handle
with its `@`; it has the buttons `Change`, `Change PIN` and `Log out`, and a radiogroup `Theme`
with the radios `System`, `Light` and `Dark`.

Action: press `Change`, read the dialog, press `Cancel`. Press `Change PIN`, read the dialog,
press `Cancel`.

Assert: the first dialog is `Change your handle`, with a textbox `Public handle (@)`. The second
is `Change your PIN`, with three groups of six boxes, `Current PIN`, `New PIN` and
`Confirm new PIN`, and a button `Change PIN` that is disabled.

Action: check the radio `Dark`.

Assert: `localStorage["azurebank.theme"]` is `dark`; the page holds `This device will stay on
your choice.`; after a reload the value is still `dark`. Check `System`: the value is `system`,
and the page holds a sentence that starts `Following your device`.

### Step 14. A phone's width

Action: set the viewport to 390 by 844, in the same context. Open `/dashboard`.

Assert:

- `document.documentElement.scrollWidth` equals `window.innerWidth` on `/dashboard`,
  `/accounts`, `/history` and `/transfer`: no page scrolls sideways;
- `Main navigation` now holds the links `Home`, `Accounts` and `History` and the buttons
  `Transfer` and `More`, and no button `Sign out` is drawn;
- after a press on `More`, its `aria-expanded` is `true`, and the links `Contact` and `Settings`
  and the button `Sign out` are in the navigation.

Set the viewport back to 1280 by 720.

### Step 15. Three refusals of a transfer

**A handle nobody has.** On `Send Money`, fill `Recipient handle` with `zz_nobody_here` and
press `Verify`.

Assert: `GET /api/users/zz_nobody_here` answers 200 with `data.exists` `false`; an alert reads
`We couldn't find @zz_nobody_here. Check the handle and try again.`; `Review Transfer` is
disabled. The copy's own handle is answered the same way.

**More than the account holds.** Press `From Checking`, verify `H`, and fill `Transfer amount`
with `99999`.

Assert: an alert reads `Exceeds available balance of €2,380.00.`, with the account's own
balance; `Review Transfer` is disabled; no request is sent.

**More than the day allows.** Press `From Main Savings`, verify `H`, fill the amount with
`6000`, press `Review Transfer` and `Continue`, and type the PIN.

Assert:

- `POST /api/transfers/authorizations` answers 422 with `errorCode` `DAILY_LIMIT_EXCEEDED` and
  `detail` `Daily transfer limit exceeded.`;
- its body has `limit` 5000, `requested` 6000, a number `used` and an instant `resetsAt` that is
  the next midnight in UTC;
- no `POST /api/transfers` follows;
- the level-1 heading is still `Confirm with PIN`, and an alert matches
  `^Daily transfer limit reached — €[\d,]+\.\d{2} left today\. The limit resets on .+\.$`;
- `TOTAL` is unchanged afterwards.

Changes counted: 1, the refused request.

### Step 16. One request, sent twice

**Two presses.** Open the deposit dialog, fill the amount with `5`, and double-click
`Deposit €5.00`.

Assert: one `POST /api/transactions/deposit` was sent, and `TOTAL` is up by 5.00, not by 10.00.

**Two sends of one request.** From the page, so that the cookie rides along, read an account's
`id` and `balance` from `GET /api/accounts`. Then send `POST /api/transactions/deposit` four
times, with the JSON body `{"accountId": "<id>", "amount": 5, "description": "<yours>"}`:

| Send | `Idempotency-Key` | Amount | Expect |
|---|---|---|---|
| 1 | a new UUID, `K` | 5 | 201, and no `Idempotency-Replayed` header |
| 2 | `K` again | 5 | 201, `Idempotency-Replayed: true`, the same `data.newBalance` |
| 3 | `K` again | 6 | 422, `errorCode` `IDEMPOTENCY_KEY_REUSE` |
| 4 | none | 5 | 400, `errorCode` `IDEMPOTENCY_KEY_MISSING` |

Assert: `GET /api/accounts` then gives that account `balance` plus 5, once. The two refusals
read `This idempotency key was already used with a different request payload.` and `The
'Idempotency-Key' header is required on this endpoint.`

Changes counted: 5, the double-click's one request and all four sends. Mind step 7's trap: the
double-clicked deposit has no description unless you type one.

### Step 17. "Start over", opened and not confirmed

Action: on the dashboard, in the region `Your private copy`, press `Start over`.

Assert: an alertdialog `Start over with a new copy?` opens, with a level-2 heading of that name,
the text `You'll get a fresh copy with the starting balances and history. This browser will
forget the current copy, and it will be deleted later.`, and three buttons: `Close`,
`Keep this copy` and `Start over`.

Action: press `Keep this copy`.

Assert: the dialog is hidden; no `POST /bff/auth/demo/claim` was sent; `TOTAL` is unchanged.

Confirming is a second claim. The repository's own run confirms it and expects a 200, the words
`You have a new copy.`, `TOTAL` `€14,750.00` and another address in the kept key (read:
[`demo.spec.ts`](../../frontend/e2e-demo/demo.spec.ts)).

### Step 18. Sign out, then the Back button

Action: press `Sign out`.

Assert:

- `POST /bff/auth/logout` answers 200, the address is `/login`, and the context holds no cookie;
- `localStorage["azurebank.demoCopy"]` is still there;
- one text matches `^This browser remembers a demo copy\. It works until .+\.$`;
- there is one button each of `Continue with my copy`, `Get a new copy` and `Forget this copy`,
  and no `Try the demo`.

Action: go back in the browser's history. Then open `/accounts`.

Assert: both times the address ends at `/login`, no text on the page matches `€\d`, and the page
does not say `Your session has expired`.

If a second tab of the same context was left on the dashboard, press its link `History`.

Assert: its `GET /bff/auth/me` answers 401, its address becomes `/login`, and the page holds
`Your session has expired. Please sign in again.`

### Step 19. "Continue with my copy"

Action: at least six seconds after the last request at the doors, arm a wait for the answer to
`POST /bff/auth/login`. Press `Continue with my copy`.

Assert: the answer is 200, and `TOTAL` is what it was before the sign-out.

Where it lands: `/dashboard`, or the protected page a sign-in was last asked for. After step
18's visit to `/accounts`, the walk landed on `/accounts`.

### Step 20. Another browser, with the copy's own details

Action: open a second fresh context, and `/login` in it.

Assert: one button `Try the demo`, and no `Continue with my copy`.

Only if you can spare a request at the doors: fill `Email address` with
`nobody@azurebank.example` and `Password` with `Not-a-real-pass1`, and press `Sign in`.

Assert: `POST /bff/auth/login` answers 401 with `errorCode` `INVALID_CREDENTIALS`, and an alert
reads `Invalid email or password.` Never send a wrong password with the copy's own address:
five of them lock it.

Action: fill the two textboxes with the copy's email and password, from the first context's kept
key and never from a log. Press `Sign in`.

Assert:

- `POST /bff/auth/login` answers 200, the address is `/dashboard`, and `TOTAL` is the first
  context's;
- the region `Your private copy` has two paragraphs, `This is a private demo copy. It works for
  24 hours from its first use, then it is closed and deleted.` and
  `PIN: 123456, unless you changed it`, and no button;
- `localStorage["azurebank.demoCopy"]` is `null` in this context;
- in the first context `GET /bff/auth/me` still answers 200, also after this context signs out.

### Step 21. Wrong PINs, last and only if wanted

Three wrong PINs in a row lock the copy's PIN for 15 minutes. Nothing after this step can use
the PIN, so it is last.

| Where | The answer | On the page |
|---|---|---|
| The reveal's dialog | `POST /bff/auth/verify-pin` 200, `data.verified` `false` | A |
| A transfer's PIN step | `POST /api/transfers/authorizations` 401 `INVALID_PIN` | B |
| The third, anywhere | 429 `PIN_LOCKED`, `Retry-After: 900` | C |

- **A.** An alert `Incorrect PIN. Please try again.`; the six boxes are invalid; `message` in
  the body is `Invalid PIN`. Press `Cancel`.
- **B.** An alert `Invalid PIN.`, which is the body's `detail`; the boxes are empty and enabled.
- **C.** The body has `retryAfterSeconds` 900 and `lockedUntil`. Two alerts read `Too many
  incorrect PIN attempts. Your PIN is temporarily locked; try again later.` and
  `Too many incorrect PIN attempts.`; a `timer` reads `Try again in 14:59` and counts down; the
  six boxes are disabled.

The count is one count for both places: the walk's wrong PIN in the reveal's dialog was the
first, and its second wrong PIN on the transfer was answered `PIN_LOCKED`. The lock ends by
itself: 23 minutes after it the right PIN was answered `data.verified` `true`.

### Step 22. Leave

Action: sign out. On the sign-in page press `Forget this copy`.

Assert: an element with the role `status` reads `This browser no longer remembers the copy.`;
there is one button `Try the demo`, with the keyboard's focus on it, and no
`Continue with my copy`; `localStorage["azurebank.demoCopy"]` is `null`.

Then delete your own file with the copy's details, if you kept one.

**The copy deletes itself.** No request deletes a copy, and none is needed. It stops signing in
24 hours after the claim, and the pool's job deletes it after that (read: ADR-0062, decisions 8
and 9). Do not "Start over" to tidy up: that takes a second copy and leaves the first as it is.

## 5. The endpoints behind the steps

A success is `{ "data": …, "message": … }`; a list adds `pagination`. A refusal is a problem
body with `type`, `title`, `status`, `detail`, `instance`, `errorCode` and `traceId`, and the
members its code adds; section 6 marks the three refusals that have no `errorCode`. The answers
that were looked at for it carried an `X-Correlation-ID` header. All of these were seen.

| Request | Answer |
|---|---|
| `GET /health/ready` | 200, `text/plain`, `Healthy` |
| `POST /bff/auth/demo/claim`, body `{}` | 200, `no-store` |
| `GET /bff/auth/me` | 200 with `data.user` and `data.session`; 401 without a session |
| `GET /bff/auth/session-status` | 200 either way, with `isAuthenticated` |
| `POST /bff/auth/login` | 200; 401 `INVALID_CREDENTIALS` |
| `POST /bff/auth/logout` | 200 |
| `POST /bff/auth/reauthenticate` | 200 |
| `POST /bff/auth/register` | 403 `REGISTRATION_CLOSED` |
| `POST /bff/auth/verify-pin` | 200, with `data.verified` |
| `GET /api/accounts` | 200, a list |
| `GET /api/accounts/{id}/balance` | 200, with `balance` and `currency` `EUR` |
| `GET /api/accounts/{id}/full-number` | 403 until the PIN is checked, then 200 |
| `POST /api/accounts` | 201 |
| `PATCH /api/accounts/{id}` | 200 |
| `GET /api/users/{handle}` | 200, with `data.exists` |
| `GET /api/transactions?Page=&PageSize=` | 200, a list with `pagination` |
| `GET /api/transactions/summary` | 200 |
| `GET /api/transactions/{id}` | 200 |
| `POST /api/transactions/deposit` | 201 |
| `POST /api/transactions/withdraw/authorizations` | 201 |
| `POST /api/transactions/withdraw` | 201 |
| `POST /api/transfers/authorizations` | 201; 401, 422 and 429 as in section 6 |
| `POST /api/transfers` | 201 |
| `POST /api/transfers/internal/authorizations` | 201 |
| `POST /api/transfers/internal` | 201 |

- An account in `GET /api/accounts` has `id`, `accountNumber`, `name`, `type`, `balance`,
  `isPrimary` and `createdAt`. `balance` is a number, and `accountNumber` is masked there.
- A row of `GET /api/transactions` has `id`, `transactionNumber`, `type`, `amount`,
  `balanceAfter`, `description`, `recipientAzureTag`, `senderAzureTag`, `status` and
  `createdAt`.
- A withdrawal, a payment and a move between accounts need the PIN first: the request that
  ends in `/authorizations` takes the PIN, and the one after it carries what that answered in
  the `Step-Up-Authorization` header. A deposit needs none. All four carry an
  `Idempotency-Key`.
- `GET /bff/auth/session-status` gave `inactivityExpiresAt` 899 seconds and `absoluteExpiresAt`
  3,546 seconds after its `serverTime`, about a minute after a sign-in: the session's two ends.
  A page left alone showed an alertdialog `Session about to expire` 781 seconds after its last
  request, with the buttons `Stay signed in` and `Sign out now` and the text `You have been
  inactive for a while. You will be signed out in 1:59.` There `Stay signed in` sends
  `GET /bff/auth/me`.
- 118 seconds before `absoluteExpiresAt` the same alertdialog read `This session has reached
  its maximum length. For your security it ends on a fixed schedule, whether or not you are
  using it. You will be signed out in 1:59.` In the browser that keeps the copy it had the same
  two buttons and no password field. There `Stay signed in` sent
  `POST /bff/auth/reauthenticate`, a request at the doors, which answered 200; the next
  `session-status` gave 899 and 3,599 seconds, and `TOTAL` was unchanged.

## 6. The refusals

| What was asked | Status, code | Seen |
|---|---|---|
| `GET /bff/auth/me` with no session | 401, no `errorCode` | yes |
| `GET /api/accounts` with no session | 401 `AUTH_TOKEN_MISSING` | yes |
| `POST /bff/auth/register` | 403 `REGISTRATION_CLOSED` | yes |
| `GET /bff/auth/demo/claim` | 405, `Allow: POST`, no body | yes |
| A claim past the client's ten | 429 `DEMO_DAILY_LIMIT` | read |
| A claim with no copy free | 429 `DEMO_POOL_EMPTY` | read |
| A change past the copy's 200 | 429 `DEMO_COPY_LIMIT` | read |
| An eleventh request at the doors | 429 `RATE_LIMIT_EXCEEDED` | read |
| A pair that signs nobody in | 401 `INVALID_CREDENTIALS` | yes |
| A sign-in after five wrong passwords | 429 `ACCOUNT_LOCKED` | read |
| A full number before the PIN | 403, `type` `STEP_UP_REQUIRED` | yes |
| A wrong PIN at `verify-pin` | 200, `data.verified` `false` | yes |
| A wrong PIN at an `/authorizations` | 401 `INVALID_PIN` | yes |
| The third wrong PIN in a row | 429 `PIN_LOCKED` | yes |
| A payment past the day's limit | 422 `DAILY_LIMIT_EXCEEDED` | yes |
| A key used with another body | 422 `IDEMPOTENCY_KEY_REUSE` | yes |
| A money request with no key | 400 `IDEMPOTENCY_KEY_MISSING` | yes |
| `GET /api/users/J` | 400, `errors.azureTag`, no `errorCode` | yes |
| A POST marked as from another site | 403 `CROSS_SITE_REQUEST_BLOCKED` | read |
| A claim whose body is not JSON | 415, or 400 | read |

The words, for the codes a tester is most likely to meet:

- **No session.** The BFF's own 401 has `title` `Unauthorized` and `detail` `Session expired or
  invalid`, as `application/problem+json`. The proxied 401 has `detail` `Authentication is
  required to access this resource.`
- **`DEMO_DAILY_LIMIT`** (read: [`DemoRefusalException.cs`]). `detail` is `This network has used
  its demo copies for today. Please try again later.`, with `retryAfterSeconds` in the body and
  the same number in `Retry-After`. On 2026-10-04 the record's own run met 86394 (read:
  ADR-0063, Validation). The sign-in page words it `This network has used its demo copies for
  today. Please try again tomorrow.` (read: [`demoWords.ts`]). **Do not provoke it.**
- **`DEMO_POOL_EMPTY`** (read: the same two files). `detail` is `All demo copies are in use
  right now. Please try again later.`, and no wait is named. The sign-in page words it `All demo
  copies are in use right now. New ones are added regularly. Please try again later.`
- **`DEMO_COPY_LIMIT`** (read: [`DemoRefusalException.cs`]). `detail` is `This demo copy has
  reached its limit of changes. Start over to get a fresh copy.` Reads still answer.
- **`RATE_LIMIT_EXCEEDED`** (read: [the BFF's `Program.cs`]). `detail` is `Too many requests.
  Please retry later.`, with `Retry-After` and `Cache-Control: no-store`. At the doors the
  header is 60 (read: ADR-0063, Validation). The sign-in page says `Too many attempts from your
  connection.` and counts down.
- **`ACCOUNT_LOCKED`** (read: [`ValidationRules.cs`]; ADR-0063's run met `Retry-After: 899`).
- **`PIN_LOCKED`.** `detail` is `Too many incorrect PIN attempts. Your PIN is temporarily
  locked; try again later.`, with `Retry-After: 900`, `retryAfterSeconds` 900 and `lockedUntil`.

## 7. What the repository's own suites cover

Both browser suites are run from `frontend/`. Neither was run for this guide: the first is
forbidden on a shared stack, and the second needs the demo off. Their tests were counted, the
first by reading its three files and the second by listing it without running it.

| Command | Tests | The stack it needs |
|---|---|---|
| `npm run test:e2e:demo` | 17 | The demo on, and nobody else using it |
| `npm run test:e2e` | 48, in 12 files | The demo off |
| `npm test` | not counted here | None: it runs against a mock |
| `dotnet test backend/AzureBank.slnx` | not counted here | None, or a SQL Server |

**`npm run test:e2e:demo`** ([its configuration](../../frontend/playwright.demo.config.ts),
[`e2e-demo/`](../../frontend/e2e-demo/)).

- It needs the stack of the two compose files, on a volume that has held nothing else; the
  `docker` command; the browser the suite drives, installed; and two optional variables,
  `E2E_DEMO_BASE_URL` (`http://localhost:5000` unless set) and `E2E_DEMO_COMPOSE_PROJECT`
  (`azurebank` unless set).
- **It restarts the BFF's container and then the API's**, with `docker restart`. It claims two
  copies, sends four requests at the doors and one transfer of one euro. It keeps no trace, no
  picture and no video, and its first test holds that. It leaves a saved session and the
  claimed passwords in `frontend/e2e-demo/.auth/`, and its output in `frontend/test-results/`,
  to be deleted once read.
- It holds: the page's tag; the sign-in page with no copy kept; `/register` closed; the claim,
  its headers and what the browser keeps; the dashboard's money and its panel; the sign-in
  details against the kept key; one transfer with the PIN the page prints; "Start over" from
  the keyboard, confirmed, with where focus goes; the sign-in page offering the kept copy;
  "Continue with my copy"; four accessibility scans of the demo's screens; that no screen
  broke the page's Content-Security-Policy; and, after the restart, that the session is gone,
  that the kept copy still signs in, and "Forget this copy".
- **It does not walk** a full account number, a deposit, a withdrawal, a move between
  accounts, the history's filters, the accounts page, settings, the theme, a phone's width,
  a second browser, a second tab, or any refusal of section 6 but the 401 of a missing
  session. Steps 6 to 8, 10 to 16, 20 and 21 above are the ground it leaves.

**`npm run test:e2e`** ([its configuration](../../frontend/playwright.config.ts),
[`e2e/`](../../frontend/e2e/)).

- It needs the demo **off**: the BFF on port 5000 and the API behind it, on a database the
  Seeder's `reset --confirm` filled ([local setup](../engineering-practices.md#local-setup)).
  It starts the page's dev server itself. With `E2E_BASE_URL` set it drives the built page the
  BFF serves and starts nothing. Two of its files skip unless `E2E_PROBE_EMAIL` and
  `E2E_LOCK_PROBE_EMAIL` name two throwaway users; the CI workflow gives it
  `jane@example.com` and `mike@example.com`.
- It holds the app's other half: the redirect from a protected page, sign-in as a seeded user,
  a deposit against the dashboard's figure, the reveal with and without the PIN, the balance
  guard, closing an account with the PIN, the PIN lock's countdown to zero, a slow or absent
  service, the "went through" views, the leave prompt of a transfer under the keyboard, the
  accessibility scans and the Content-Security-Policy.
- **Do not point it at a demo stack.** One of its checks expects "Create account" and no "Try
  the demo", and fails there (read: ADR-0063, "What the browser keeps in demo mode").

**`npm test`** runs the page's unit and component tests against a mock, the demo's screens
among them, with nothing else running. **`dotnet test backend/AzureBank.slnx`** runs the API's
and the BFF's suites: the claim, the sign-in gate and the budget of changes are held there. Stop
a running API and BFF first, and name the solution
([local setup](../engineering-practices.md#local-setup) says why). The proofs on a real
database run only with `AZUREBANK_TEST_SQLSERVER` set, and are skipped without it.

## 8. What not to assert

These differ from copy to copy, from day to day, or from run to run.

| Do not assert | Because |
|---|---|
| Any id, `traceId` or `X-Correlation-ID` | Made for each row and each request |
| The copy's address and password | Drawn for each copy. Assert their shape |
| The handles' last four characters | Drawn for each copy. Read `H` from the page |
| An account number's digits | Drawn for each copy |
| A transaction number, past its shape | Drawn for each transaction |
| Any date or time on the screen | Written in the viewer's time zone |
| The dates of the history | Counted back from when the copy was prepared |
| The month card's title and sums | They follow the calendar |
| "Recently sent to …" and the five recent rows | They follow what the copy did |
| `used` in the day's limit | The history can hold a payment dated today |
| The copy's end, to the second | Assert 24 hours, give or take a minute |
| `data.expiresAt` of the claim | It is the session token's end, not the copy's |
| How many copies are free or left | Others claim from the same pool and the same ten |
| How long a request takes | The machine is shared |
| The seconds left on a lock or a session | They run while you look |
| `Across 2 accounts`, after step 12 | You added a third |
| Colour contrast | The suite's own scans report it and do not fail on it |

In the walk's copy the history held a payment of 25.00 dated the same UTC day, so `used` was 26
after step 9's one euro. In another copy that payment can fall on the day before.

## 9. How to report

One entry for each step, in order, and a line of totals. No entry holds the password, the
copy's address or a claim's answer.

| Field | What goes in it |
|---|---|
| `step` | The number and the name from section 4 |
| `result` | `done`, `failed` or `skipped`, and for a skip the reason |
| `evidence` | Statuses, codes, headers, texts and amounts met, as booleans where secret |
| `first failure` | The first assertion that did not hold: `expected`, then `found` |

After a failed step go on only if the copy's state is still known. Otherwise stop: a later
assertion on a balance would fail for the earlier reason.

```text
step 7  Deposit                         done
  evidence: POST /api/transactions/deposit 201; receipt "New balance €12,500.00";
            TOTAL €14,750.00 -> €14,800.00
step 8  Withdraw                        failed
  evidence: the PIN step read "Verify Withdrawal"; no request followed the sixth digit
  first failure: expected a dialog named "Withdrawal Complete" within 20 s
                 found    the dialog "Withdraw Money", its button "Withdraw €20.00" not pressed
totals: copies 1; changes 3; door requests 2; BASE http://localhost:5000;
        2026-10-06 14:52Z to 14:55Z; Chromium, 1280 by 720
```

That is the walk's own first try at step 8, and the fault was the tester's: it waited for the
sixth digit to send, and a withdrawal waits for its button.

## Where each fact comes from

| Fact | Source |
|---|---|
| Names, texts, statuses, codes, headers not marked "read" | The walk of 2026-10-06 |
| 10 copies a day, one client on one machine | The header of [`compose.demo.yaml`] |
| 200 changes, and what is counted | ADR-0063, decisions 7 and 11; [the Seeder's README] |
| 10, 20 and 300 a minute; 15 and 60 minutes | [The BFF's `appsettings.json`] |
| Which requests are the doors | [`BffAuthController.cs`]; [the BFF's `Program.cs`] |
| 3 PINs, 5 passwords, 15 minutes | [`ValidationRules.cs`] |
| 5,000.00 a day, and what it counts | [The API's `appsettings.json`] |
| The demo's sentences not seen | [`demoWords.ts`]; [`DemoRefusalException.cs`] |
| The two header names | [`IdempotencyConstants.cs`]; [`StepUpConstants.cs`] |
| What a copy holds | ADR-0062, decision 1; [the Seeder's README] |
| What the browser keeps, and when it goes | ADR-0063, "What the browser keeps in demo mode" |

**Where the stack and a file disagree, the stack's answer is the one to write down.** The walk
met no such place. One thing could be taken for one. A comment in
[`ChangePinDialog.tsx`](../../frontend/src/components/dialogs/ChangePinDialog.tsx) says a
`PIN_LOCKED` answer comes with "NO Retry-After header". It is about the request that changes a
PIN, which the walk did not send. On `POST /api/transfers/authorizations` the walk's
`PIN_LOCKED` carried `Retry-After: 900`.

**Not met by the walk, and so only read:** the three 429s a claim can answer, the lock of a
password, a copy past its 200 changes, a copy past its 24 hours, a confirmed "Start over",
a transfer to another copy's handle, a changed handle or PIN, a deleted account, the session's
fixed end in a browser that does not keep the copy, any browser but Chromium, and any address
but `http://localhost:5000`.

The decisions behind all of it are
[ADR-0062](../adr/0062-demo-visitors-get-private-copies-from-a-prepared-pool.md) and
[ADR-0063](../adr/0063-a-visitor-claims-a-prepared-copy-instead-of-registering.md). What an
operator does about the pool is in [the runbook](../runbooks/demo-pool.md).

[`compose.demo.yaml`]: ../../compose.demo.yaml
[the Seeder's README]: ../../backend/tools/AzureBank.Seeder/README.md
[The BFF's `appsettings.json`]: ../../backend/src/AzureBank.Bff/appsettings.json
[The API's `appsettings.json`]: ../../backend/src/AzureBank.Api/appsettings.json
[`BffAuthController.cs`]: ../../backend/src/AzureBank.Bff/Controllers/BffAuthController.cs
[the BFF's `Program.cs`]: ../../backend/src/AzureBank.Bff/Program.cs
[`ValidationRules.cs`]: ../../backend/src/AzureBank.Shared/Constants/ValidationRules.cs
[`demoWords.ts`]: ../../frontend/src/features/demo/demoWords.ts
[`DemoRefusalException.cs`]: ../../backend/src/AzureBank.Shared/Exceptions/DemoRefusalException.cs
[`IdempotencyConstants.cs`]: ../../backend/src/AzureBank.Shared/Constants/IdempotencyConstants.cs
[`StepUpConstants.cs`]: ../../backend/src/AzureBank.Shared/Constants/StepUpConstants.cs
