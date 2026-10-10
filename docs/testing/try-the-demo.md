# Try the demo

For a person who wants to try everything. You need no knowledge of the code.

Everything below was done on 2026-10-06, on the demo as this repository's two compose files
run it on one machine, in a Chromium browser, in two walks: the first on one copy, the second,
which followed this page, on two more. Where a line says **not seen**, the words come from the
code and neither walk reached them. Three lines describe screens that changed after the walks,
and tests hold them. [The last section](#where-these-facts-come-from) names the files.

## What the demo is, and is not

- **It is a pretend bank.** The money is invented. Nothing you do reaches a real account.
- **You get a private copy.** One click gives you a bank account that no other visitor can see.
- **The copy takes sign-ins for 24 hours.** A session open at that moment goes on until it ends, at
  most 60 minutes after its sign-in by default. It is deleted later, at a run of a job that runs
  every four hours on the public demo: a run leaves a copy alone until five minutes after its end,
  and longer while somebody is still signed in to it; from 48 hours after its end a run deletes it
  whoever is signed in.
- **Do not enter real personal data.** A description, an account name and a handle are yours to
  invent. The demo asks for no name, no address and no card of yours.
- **It is not a product.** The sign-in page says so itself: "Demo project — not a real bank."

The sign-in page also says what is kept, before you press anything: "This browser remembers the
copy's sign-in details so you can come back to it. To limit abuse, a one-way code of your
network address is kept with the copy and removed when the copy is deleted."

## Where it runs

Open **the demo's address** in your browser. For the public demo it is in
[the README](../../README.md#try-it): the entry page, <https://gurgant.github.io/azurebank-v2/>,
starts the server, shows the wait and links to the demo. How long the first page takes after a
quiet spell is under the README's
[Status and known limits](../../README.md#status-and-known-limits).

**For a developer: start it on one machine.** You need Docker and this repository. Two files at
the repository's root start it, and their headers are the instructions:
[`compose.yaml`](../../compose.yaml) and [`compose.demo.yaml`](../../compose.demo.yaml).

1. Set the eight variables the header of `compose.yaml` names. In its words: "Every secret is
   required and has no default: set them in the shell, or in a git-ignored .env next to this
   file".
2. Set one more, `DEMO_CLIENT_KEY_SECRET`. The header of `compose.demo.yaml` says: "32
   characters or more: the API refuses a shorter one at start".
3. Run the command that header gives:

   ```bash
   docker compose -f compose.yaml -f compose.demo.yaml up --build
   ```

4. "Then open http://localhost:5000." Keep the name `localhost`, and use a Chromium browser:
   the session's cookie is marked secure, and Chromium keeps it on `http://localhost`
   ([engineering practices](../engineering-practices.md#local-setup) has the measurement).

Three things those headers say, which you will meet:

- The database gets "the demo pool's free copies", 50 by default.
- "the stack hands out 10 copies (Demo:Claim:MaxPerClientPerDay) in a rolling 24 hours, to all
  its browsers together". On one machine every browser is the same visitor.
- A volume that held the ordinary stack cannot hold the demo, nor the other way round:
  "`docker compose down -v` before changing files".

## Get a copy

1. Open the demo's address. You are on the sign-in page. Its title is "Welcome", and under it:
   "Try the demo with one click. No sign-up needed." In a browser that remembers a copy the
   title is "Welcome back".
2. Press **Try the demo**.
3. You are on the home page of your copy. At its top is a panel, **Your private copy**.

What you are given:

| What | In the copy this walk claimed |
|---|---|
| A person to be | John Smith |
| Two accounts | Main Savings, €12,450.00, and Checking, €2,300.00 |
| A total | €14,750.00, "Across 2 accounts" |
| History | Two months of payments, the newest first |
| Two contacts you can pay | `@jane_02ga` and `@mike_02ga` |
| A PIN | 123456 |
| A way back in | An email and a password, which your browser keeps |

Every copy starts with the same money and the same PIN. The handles differ: your copy's three
handles end in four characters of their own.

The panel says it in three lines:

- "Other visitors can't see this copy. It works until October 7, 2026 · 4:52 PM, then it is closed
  and deleted." The date and the time are your copy's end, in your own time zone. The panel's words
  are short for what the first section says: from that moment no new sign-in is accepted, and the
  deletion comes at a later run.
- "PIN: 123456, unless you changed it"
- `Contacts you can pay: @jane_02ga and @mike_02ga`

There is no sign-up. On the demo the address `/register` leads back to the sign-in page.

## A tour, feature by feature

Each journey is short. "You should see" is what the walk saw. Amounts are the walk's: yours
follow from what you did before.

Two things about the words. Some small labels are drawn in capitals: "Available balance" is on
the screen as "AVAILABLE BALANCE", and a table's headings as "WHEN", "ENTRY", "AMOUNT". And a
button that is only a picture, such as the eye, has no words on it: the name this page gives
it is the one a screen reader reads.

### 1. Look around the home page

1. Read the big figure. You should see "Available balance", €14,750.00 and "Across 2 accounts".
2. Press the eye button beside it, **Hide balances**. You should see every amount on the page
   turn into dots, "••••••". The button is now **Show balances**. Press it.
3. Under the figure are three buttons: "All accounts", "Main Savings" and "Checking". Press
   **Checking**. You should see the big figure become €2,300.00, the words "Checking only" in the
   month's card, and "Recent activity · Checking" with one more column, "Balance". Press
   **All accounts** to go back.
4. Read the month's card: "Money in", "Money out", "Net change".
5. Read "Recent activity": the five newest entries. Press one, for example `To @jane_02ga`. You
   should see the page "Transaction Details": the amount, "Transfer sent", "Completed", a
   transaction number that starts with `TXN-`, the date and time, the description and "Balance
   after". Press **Back**: it leads to History, not back to the home page.
6. The menu on the left has **Transfer**, Home, Accounts, History, Contact and Settings, and
   **Sign out** at the bottom. The menu is as tall as the page, so on a long page "Sign out" is
   below the window: scroll to the bottom of the page to reach it. On the second walk's home
   page it was about 1,600 pixels down, in a window 720 high. "Contact" opens "About this
   project".

### 2. Show a full account number

Account numbers are masked: "AB-••••-••••-50". Showing one in full asks for your PIN.

1. Open **Accounts**.
2. Beside "Main Savings", press the eye button, "Reveal full account number for Main Savings".
3. You should see a dialog, "Verify it's you", with six boxes and the line "Demo PIN: 123456,
   unless you changed it." Its sentence says what the PIN is for: "Enter your 6-digit PIN to
   show the full account number."
4. Type 123456. The sixth digit sends it.
5. You should see the number in full, in the form AB-1234-5678-90, and two new buttons: one
   hides the number again, one copies it.

**Cancel** in the dialog leaves the number masked. For a few minutes after a right PIN,
another number shows without the dialog: the walk's second one showed at once.

### 3. Deposit

A deposit asks for no PIN.

1. On the home page press **Deposit**.
2. You should see the dialog "Deposit Money", with "Main Savings" chosen.
3. Type an amount, for example 50, or press one of €50, €100, €200, €500. Add a description if
   you like.
4. You should see "New balance: €12,500.00" under the amount, and the button now reads
   **Deposit €50.00**. Press it.
5. You should see "Deposit Complete": "Deposit Successful!", "+€50.00", "To Main Savings" and
   "New balance €12,500.00". Press **Done**.
6. The big figure on the home page should be up by exactly what you deposited: €14,750.00
   became €14,800.00.

### 4. Withdraw

1. On the home page press **Withdraw**.
2. In "Withdraw Money" choose an account, for example "Checking". Under the amount box you
   should see "Available: €2,300.00". Type an amount, for example 20: the same line now reads
   "New balance: €2,280.00". "Available" comes back, with a warning, when the amount is more
   than the account holds.
3. Press **Continue · €20.00**.
4. You should see "Verify Withdrawal" and "Enter your 6-digit PIN to confirm withdrawing €20.00
   from Checking."
5. Type 123456, then press **Withdraw €20.00**. Here the last digit does not send by itself.
6. You should see "Withdrawal Complete": "Withdrawal Successful!", "-€20.00", "From Checking",
   "New balance €2,280.00". Press **Done**. The total is down by the amount.

### 5. Pay a contact

1. Press **Transfer** in the menu. You should see the page "Send Money".
2. **The handle.** Under `To (recipient's @handle)` type one of your contacts' handles, without
   the at-sign: for example `jane_02ga`. Yours are in the panel on the home page.
3. **The check.** Press **Verify**. You should see the contact appear: `Jane S. @jane_02ga`.
4. **The amount.** Type it, for example 1. You should see "Available: €12,500.00" beside it.
5. Press **Review Transfer**.
6. **The review.** You should see the page "Review Transfer": "From Main Savings",
   `To Jane S. (@jane_02ga)`, "Amount €1.00", "New balance €12,499.00", and "You'll confirm
   with your PIN on the next step." Press **Continue**.
7. **The PIN.** You should see "Confirm with PIN" and "Enter your 6-digit PIN. The transfer
   sends as soon as the last digit is in." Type 123456.
8. You should see "Transfer Complete": "Transfer Sent!", "-€1.00", `To Jane S. (@jane_02ga)`,
   a reference that starts with `TXN-`, and "New balance €12,499.00".
9. Press **Done**. The total on the home page is down by the amount.

### 6. Move money between your two accounts

1. Open **Transfer**, then press **Between your own accounts** at the bottom. You should see
   the page "Move Money".
2. Choose "From" and "To". The account you move from cannot be chosen as "To": it reads "Source
   account".
3. Type an amount, for example 100, and press **Review Transfer**.
4. You should see "From Main Savings", "To Checking", "Amount €100.00" and "Main Savings balance
   €12,399.00". Press **Continue**.
5. Type 123456. The last digit sends.
6. You should see "Transfer Complete": "Transfer Complete!", "€100.00", "From Main Savings", "To
   Checking". Press **Done**.
7. The total should be what it was: €14,779.00 before and after. One account is down by the
   amount and the other is up by it.

### 7. History and its filters

1. Open **History**. You should see the newest twenty entries, grouped by day, and above them a
   line of three sums: "Income", "Expenses", "Net".
2. Press **Deposits**, **Withdrawals**, **Transfers**, then **All**. You should see only entries
   of that kind. "Transfers" shows money sent, money received and moves between your own
   accounts. The three sums stay as they are: they add up every entry loaded so far, of every
   kind. They moved when "Load more" added entries.
3. Press **Load more** at the bottom. You should see older entries added under the first twenty.
   The second walk's copy held 27 entries by then: one press showed them all, and the button
   went.
4. Press an entry. You should see its "Transaction Details". **Back** returns to History.

What you just did is at the top: the walk's deposit, withdrawal and transfers were the first
rows.

### 8. The accounts page

1. Open **Accounts**. You should see "Total balance" and a card for each account: its name, its
   masked number, "Available Balance", and the buttons Deposit, Withdraw and Transfer for that
   account.
2. Press **Add account**. In "Add New Account" type a name and choose a type: Checking, Savings
   or Investment. Press **Create Account**. You should see a new card with €0.00, and the home
   page then says "Across 3 accounts".
3. Press the menu button of the new card, "Account actions for …". You should see **Rename**,
   **Set as primary** and **Delete**. The primary account's menu has no "Set as primary".
4. Press **Rename**, change the name, press **Save**. You should see the new name on the card.
5. Press **Delete** in the same menu. You should see "Delete account?" and "You're about to
   delete "…". This can't be undone." The walk pressed **Cancel**, and the account stayed.
   Deleting for real asks for your PIN, and an account that still holds money is refused; the
   walk did neither (**not seen**).

### 9. Settings

Open **Settings**. Three things can be changed:

| What | Where | What the walk saw |
|---|---|---|
| Your handle | "Public handle", **Change** | The dialog "Change your handle", then Cancel |
| Your PIN | "PIN", **Change PIN** | The dialog "Change your PIN", then Cancel |
| The theme | "Appearance" | Changed, see below |

Your name and the copy's email are shown and cannot be changed. Four more rows are there for
show, each marked "Coming soon": password and two-factor, notifications, language, linked
accounts. At the bottom is **Log out**.

"Change your PIN" asks for the current PIN, a new one, and the new one again. If you change it,
remember it: the page goes on printing "Demo PIN: 123456, unless you changed it."

### 10. The theme

1. In **Settings**, under "Appearance", choose **Dark**.
2. You should see the whole app turn dark at once, and the line "This device will stay on your
   choice."
3. Reload the page. It should still be dark: the browser keeps the choice.
4. **System** follows your device. The line then reads "Following your device, which is
   currently light."

### 11. A phone's width

Use a phone, or make the browser window about 390 pixels wide.

1. None of these four pages should scroll sideways: the home page, Accounts, History and Send
   Money. On the home page and on History each transaction is on two lines: what it is and
   its amount, then its date and time and its status, whole.
2. The menu on the left is gone. A bar at the bottom has Home, Accounts, **Transfer**, History
   and **More**.
3. Press **More**. You should see Contact, Settings and **Sign out**.

## Coming back

**In the same browser.** Your browser remembers the copy.

1. Sign out, or come back after your session ended.
2. On the sign-in page you should see "This browser remembers a demo copy. It works until
   October 7, 2026 · 4:52 PM." and three buttons: **Continue with my copy**, **Get a new copy**
   and **Forget this copy**.
3. Press **Continue with my copy**. You are signed in to the same copy, with everything you did
   in it.

**From another browser or another device.** Use the copy's own email and password.

1. On the home page of your copy press **Show sign-in details**. You should see one line,
   "Email: … · Password: …". Copy both. Press **Hide sign-in details**.
2. In the other browser open the demo's address. Under "Already have a copy's email and
   password? Sign in here." type them and press **Sign in**.
3. You should see the same copy with the same balances. The panel there is shorter: "This is a
   private demo copy. It works for 24 hours from its first use, then it is closed and deleted."
   and the PIN. It has no "Show sign-in details" and no "Start over": that browser does not keep
   the copy.

Both browsers can be signed in at once. A wrong pair is answered "Invalid email or password."

**Forget this copy** removes the email and the password from this browser, and nothing else.
You should see "This browser no longer remembers the copy." and **Try the demo** again. The
copy itself ends at its time. Without its sign-in details you cannot go back to it.

**Whoever has the email and the password has the copy.** That is all a copy is protected by,
and all it holds is invented money.

## Start over

1. On the home page, in the panel, press **Start over**.
2. You should see a question, "Start over with a new copy?", and under it: "You'll get a fresh
   copy with the starting balances and history. This browser will forget the current copy, and
   it will be deleted later."
3. **Keep this copy** closes the question and changes nothing.
4. **Start over** gives you a new copy, with the starting €14,750.00 and other handles, and the
   message "You have a new copy." The second walk pressed it once and met all three; the browser
   then kept the new copy in the place of the old one. Each new copy counts against the ten a
   day below. The repository's own browser run of the demo presses it too.

"Get a new copy" on the sign-in page asks the same question.

## Sign out

Press **Sign out** at the bottom of the menu: on a long page, scroll down to it (journey 1,
step 6). You should see the sign-in page, offering the copy your browser remembers. Signing
out does not forget the copy.

On a phone the same button is under **More**, and Settings has **Log out**. The walk pressed
the first of the three.

You are also signed out by time: after 15 minutes with nothing done, and one hour after you
signed in.

- **After 13 minutes with nothing done** you should see a dialog, "Session about to expire":
  "You have been inactive for a while. You will be signed out in 1:59." The time counts down.
  **Stay signed in** keeps you in, and **Sign out now** signs you out.
- **Two minutes before the end of the hour** you should see the same dialog with other words:
  "This session has reached its maximum length. For your security it ends on a fixed schedule,
  whether or not you are using it. You will be signed out in 1:59." **Stay signed in** signs
  you in again with the details your browser keeps, and you go on where you were, for another
  hour. In a browser that does not keep the copy the dialog has a box in that button's place,
  "Enter your password to continue", with "This starts a new session." under it, and beside
  **Sign out now** a button **Sign in again**, switched off while the box is empty. With the
  copy's password typed, it signed in again on the same page, for another hour (seen by the
  second walk).

## The limits

| Limit | The number | Its words |
|---|---|---|
| New copies for one network | 10 in any 24 hours | A |
| Copies ready at one time | 50 | B |
| Life of a copy | 24 hours from the click | C |
| Changes in one copy | 200 | D |
| Wrong PINs in a row | 3, then 15 minutes locked | E |
| Wrong passwords in a row | 5, then 15 minutes locked | F |
| Sign-ins and new copies | 10 a minute for one network | G |
| Checks of a handle | 20 a minute | H |
| All requests | 300 a minute for one network | I |
| Money sent to other people | €5,000.00 a day | J |
| Time signed in | 15 minutes idle, 60 minutes in all | K |
| One deposit | €0.01 to €100,000.00 | L |

"One network" is one address as the demo sees it. People behind one office or home connection
share it. On one machine started with the two compose files, every browser shares it.

**A. Ten copies in 24 hours.** "Try the demo", "Get a new copy" and "Start over" each take one.
On the sign-in page the eleventh is answered "This network has used its demo copies for today.
Please try again tomorrow." In the "Start over" question it reads "This network has used its
demo copies for today. Your current copy is unchanged." **Not seen.**

**B. No copy free.** On the sign-in page: "All demo copies are in use right now. New ones are
added regularly. Please try again later." In the "Start over" question: "All demo copies are in
use right now. Please try again later. Your current copy is unchanged." **Not seen.**

**C. 24 hours.** The panel and the sign-in page print your copy's end. After it, "Continue with
my copy" is answered "That demo copy is no longer available. Copies are closed after 24 hours.
You can get a new one." **Not seen.**

**D. 200 changes.** Each thing you send that could change the copy counts: a deposit, a
transfer, a rename, a PIN typed, also when it is refused. Showing a full account number counts
too. Looking does not. Past 200 the server's sentence is "This demo copy has reached its limit
of changes. Start over to get a fresh copy." You can still look, and still sign out. **Not
seen**: the first walk sent 25 such requests on its copy, the second 20 on one copy and 15 on
the other, and none was refused.

**E. Three wrong PINs.** The first two are answered "Invalid PIN." on a transfer, and "Incorrect
PIN. Please try again." where a full account number is shown. The third locks the PIN: "Too
many incorrect PIN attempts. Your PIN is temporarily locked; try again later.", the six boxes
are switched off, and a clock counts down from "Try again in 14:59". It is one count for the
whole copy: the walk's first wrong PIN was typed for an account number and the other two for a
transfer. When the time is over the PIN works again: the walk's right PIN was taken 23 minutes
after the lock. A right PIN is also meant to start the count of wrong ones again (**not
seen**). One wrong PIN does no harm: the second walk typed one on a payment and then the right
one in the same boxes, and the payment went through. It did not lock a PIN.

**F. Five wrong passwords.** "Too many failed sign-in attempts — your account is temporarily
locked.", with a clock. **Not seen.**

**G. Ten a minute.** Signing in, getting a copy and changing your handle share ten tries a
minute. Past them the sign-in page says "Too many attempts from your connection." and counts
down; the "Start over" question says "Too many attempts. Please wait a minute and try again."
**Not seen.**

**H. Twenty checks a minute.** Past them **Verify** is answered "Too many lookups. Please wait
a moment and try again.", or the same with a time to wait. **Not seen.**

**I. 300 requests a minute.** A person clicking does not reach it. **Not seen**, and what a
page shows then was not looked at.

**J. €5,000.00 a day.** It counts money sent to other people in one day, by the clock in UTC.
Moves between your own accounts, deposits and withdrawals do not count. A payment that would
pass it is refused after the PIN, and no money moves: "Daily transfer limit reached — €4,974.00
left today. The limit resets on October 7, 2026 · 2:00 AM." The time is midnight UTC in your
own time zone. Your copy's history can already hold a payment dated today: the walk's held one
of €25.00.

**K. Signed in.** See [Sign out](#sign-out).

**L. One deposit.** €100,000.00 is taken. One cent more is answered "Maximum deposit is
€100,000." and zero is answered "Minimum deposit is €0.01."; the button stays switched off. The
amount box takes two decimals and no third. The walk typed these amounts and sent none.

## Things worth trying

For someone who wants to test it hard. Each line says what should happen, and is what the walk
saw unless it says otherwise.

1. **Send more than the account holds.** On "Send Money" choose **Checking** under "From" and
   type 99999 as the amount. You should see "Exceeds available balance of €2,380.00." under it,
   and **Review Transfer** stays switched off. Nothing is sent. The figure is the balance of
   the account chosen: left on Main Savings it read €12,399.00. A withdrawal of 99999 says the
   same of its account, and its **Continue** stays switched off.
2. **Pay a handle that does not exist.** Type `zz_nobody_here` and press **Verify**. You should
   see `We couldn't find @zz_nobody_here. Check the handle and try again.` Your own handle
   gets the same sentence. One letter gets "We couldn't check that handle. Please try again."
   A handle of another visitor's copy reads as one nobody has: the second walk had two copies,
   and from one of them two handles of the other got that sentence.
3. **Send the same payment twice.** Double-click **Deposit €5.00**. You should see one deposit:
   the total goes up by €5.00, not €10.00.
4. **Reload mid-way.** On "Review Transfer", reload the page. You should see an empty "Send
   Money" form. Nothing was sent, and what you typed is gone.
5. **Go back mid-way.** On "Review Transfer", the page's own **Back** returns to the form with
   the handle and the amount still there. The browser's Back leaves the transfer for the page
   you came from, without a question.
6. **Use two tabs.** Open the home page in two tabs and deposit in the first. The second keeps
   the old total until you reload it. That is a stale figure, not lost money: a fresh load shows
   the new total.
7. **Sign out in one tab.** The other tab still shows its page, balances and all. Press
   something in it that asks the server: **History** in its menu, or one of the account buttons
   under the big figure. You should land on the sign-in page with "Your session has expired.
   Please sign in again." Not every press does: "Hide balances" and opening the deposit dialog
   changed nothing, and "Accounts" in the menu opened the accounts page and did not lead to the
   sign-in page.
8. **Press Back after signing out.** You should stay on the sign-in page. Typing the address of
   a page such as `/accounts` should lead to the sign-in page too. No balance is shown.
9. **Send more than €5,000.00 to a contact in one day.** See limit J: refused, nothing moves.
10. **Type a wrong PIN.** One or two are forgiven. Leave the third for last: it locks the PIN
    for 15 minutes (limit E).
11. **Use the keyboard only.** Tab should reach every control in the order it is drawn, and
    Enter should press it. On the sign-in page of a browser that remembers a copy the order
    was: Continue with my copy, Get a new copy, Forget this copy, Email address, Password, Show
    password, Sign in. The deposit dialog opened with the keyboard on its **Close** button and
    walked the accounts, the amount, the four quick amounts, the description and the button
    that sends. Enter in the amount box sent nothing; Enter on the button did. On the receipt
    the first Tab reached **View Transaction**. Typing a PIN moves from box to box by itself.
12. **Open `/register`.** You should land on the sign-in page.

## When something looks wrong

Tell the repository: open an issue at <https://github.com/Gurgant/azurebank-v2/issues>.

Include:

- what you did, step by step, from "Try the demo" on;
- what you expected, and what you saw, with the exact words on the screen;
- when, with your time zone;
- your browser and its version, the width of the window, and the theme;
- the address the demo ran at, or that you started it yourself and from which commit.

Leave out:

- **the copy's email and password.** Close "Show sign-in details" before you take a picture;
- anything real about yourself.

## Where these facts come from

**The walk.** One copy, claimed on 2026-10-06 on the stack of `compose.yaml` with
`compose.demo.yaml`, at `http://localhost:5000`. A Chromium browser without a window, driven by
a script, 1280 by 720 pixels and 390 by 844, the light theme and the dark. Every "you should
see" above was on the screen then, except the lines marked **not seen**, the lines the second
walk corrected and the three named below. No other browser was tried. The stack was already
running, and the walk did not check which commit it was built from; the code was read at commit
`a3de523a`.

**The second walk.** The same day, on the same stack, in the same kind of browser. Two copies:
one from "Try the demo", and one from a confirmed "Start over" on which this page was followed
from the home page on. It did again what this page tells, but for the third wrong PIN and its
lock: it typed one wrong PIN on each copy. It saw three things the first walk had not: a
confirmed "Start over", another copy's handle, and the end of the hour in a browser that does
not keep the copy. What it corrected it also read in the code:
[`HistoryPage.tsx`](../../frontend/src/pages/HistoryPage.tsx) for the three sums, and
[`WithdrawDialog.tsx`](../../frontend/src/components/dialogs/WithdrawDialog.tsx) for the line
under a withdrawal's amount.

**After the walks.** Three lines describe screens that changed after both walks, and neither saw
them. The repository's tests hold them: the sign-in page's title
([`LoginPage.test.tsx`](../../frontend/src/pages/LoginPage.test.tsx)), the sentence of the
dialog in journey 2
([`account-reveal.test.tsx`](../../frontend/src/pages/account-reveal.test.tsx)), and the first
step of journey 11, at 375 pixels on the home page and on History
([`deposit.spec.ts`](../../frontend/e2e/deposit.spec.ts)).

**The words not seen** are typed as they stand in
[`frontend/src/features/demo/demoWords.ts`](../../frontend/src/features/demo/demoWords.ts),
[`frontend/src/pages/LoginPage.tsx`](../../frontend/src/pages/LoginPage.tsx),
[`frontend/src/pages/TransferPage.tsx`](../../frontend/src/pages/TransferPage.tsx) and
[`DemoRefusalException.cs`](../../backend/src/AzureBank.Shared/Exceptions/DemoRefusalException.cs).
The other lines marked not seen are read from
[`PinService.cs`](../../backend/src/AzureBank.Api/Services/Implementations/PinService.cs) (a
right PIN starting the count again) and
[`deleteAccount.spec.ts`](../../frontend/e2e/deleteAccount.spec.ts) (deleting an account).

**The numbers.**

| Number | Where it is set |
|---|---|
| 10 copies a day, 50 ready | The header of [`compose.demo.yaml`](../../compose.demo.yaml) |
| 24 hours, 200 changes | [The Seeder's README] |
| 3 PINs, 5 passwords, 15 minutes; €100,000.00 | [`ValidationRules.cs`] |
| 10, 20 and 300 a minute; 15 and 60 minutes | [The BFF's `appsettings.json`] |
| 5 minutes for a checked PIN | [The BFF's `appsettings.json`] |
| €5,000.00 a day | [The API's `appsettings.json`] |
| 2 minutes of warning | [`sessionActivity.ts`] |

Of these the walk met six on the stack: 24 hours between the click and the copy's end; the
third wrong PIN and its 15 minutes; €5,000.00 as the day's limit; €100,000.00 as the most one
deposit takes; 15 and 60 minutes as the session's two ends; and the warning two minutes before
each of the two, 13 minutes after the last thing done and 58 minutes after a sign-in.

The second walk met them again, but for the third wrong PIN: 24 hours on both its copies; the
day's limit, with the same €4,974.00 left; the deposit's two bounds; the first warning eight
times, 781 to 783 seconds after the last thing done; and the second warning twice, 3,481 and
3,482 seconds after a sign-in, once in the browser that keeps the copy and once in one that
does not.

Why the demo works this way is in
[ADR-0062](../adr/0062-demo-visitors-get-private-copies-from-a-prepared-pool.md) and
[ADR-0063](../adr/0063-a-visitor-claims-a-prepared-copy-instead-of-registering.md). What to do
about the pool of copies is in [the runbook](../runbooks/demo-pool.md).

[the Seeder's README]: ../../backend/tools/AzureBank.Seeder/README.md
[`ValidationRules.cs`]: ../../backend/src/AzureBank.Shared/Constants/ValidationRules.cs
[The BFF's `appsettings.json`]: ../../backend/src/AzureBank.Bff/appsettings.json
[The API's `appsettings.json`]: ../../backend/src/AzureBank.Api/appsettings.json
[`sessionActivity.ts`]: ../../frontend/src/features/auth/sessionActivity.ts
