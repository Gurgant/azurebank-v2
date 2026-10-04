/*
  What the demo says to a visitor: each sentence once, under a name, and nothing else in this file.

  One owner per sentence, so a change of wording is made in one place and reaches every surface
  that says it. The tests do not import these: each types its sentences out, so that a test fails
  the day the words on screen are no longer the ones it was written for.

  Apostrophes are ASCII.
*/

// The dialog that asks before a new copy takes the place of the one this browser keeps.

export const START_OVER_TITLE = 'Start over with a new copy?';

export const START_OVER_MESSAGE =
  "You'll get a fresh copy with the starting balances and history. This browser will forget the current copy, and it will be deleted later.";

export const START_OVER_CONFIRM = 'Start over';

export const START_OVER_CANCEL = 'Keep this copy';

/**
 * Every copy is taken. Not the server's sentence for it: this one adds what the visitor in front
 * of this dialog needs to know, that the copy they have is as it was.
 */
export const START_OVER_POOL_EMPTY =
  'All demo copies are in use right now. Please try again later. Your current copy is unchanged.';

/** This network has had its copies for the day. The same addition, for the same reason. */
export const START_OVER_DAILY_LIMIT =
  'This network has used its demo copies for today. Your current copy is unchanged.';

/** The limiter on sign-ins and claims turned the request away before any copy was looked for. */
export const START_OVER_RATE_LIMITED = 'Too many attempts. Please wait a minute and try again.';

/** A refusal that came with no sentence of its own. */
export const START_OVER_FAILED = 'Something went wrong. Please try again.';

/** Said once the dialog's claim has succeeded. */
export const NEW_COPY_READY = 'You have a new copy.';

// The sign-in page, on the demo.

/** Under the page's title, in place of the line that asks for a sign-in. */
export const DEMO_SUBTITLE = 'Try the demo with one click. No sign-up needed.';

export const TRY_THE_DEMO = 'Try the demo';

/**
 * What a visitor gets, said before a button that gets it is pressed. The first sentence alone is
 * what "Try the demo" is described by; the rest is read under it. Beside a copy the browser keeps
 * the whole notice is words to read, and describes no button.
 */
export const DEMO_NOTICE_FIRST =
  'You get a private copy of a demo bank account with invented money.';

export const DEMO_NOTICE_REST =
  "It has two accounts, two months of history and two contacts you can pay. Don't enter real personal data. The copy works for 24 hours, then it is closed and deleted. This browser remembers the copy's sign-in details so you can come back to it. To limit abuse, a one-way code of your network address is kept with the copy and removed when the copy is deleted.";

/** Above the form, which on the demo is the second way in. Words to read, not a control. */
export const HAVE_A_COPY_SIGN_IN = "Already have a copy's email and password? Sign in here.";

/**
 * Every copy is taken, said to a visitor who has none yet. Not the server's sentence for it: this
 * one adds that copies come back.
 */
export const CLAIM_POOL_EMPTY =
  'All demo copies are in use right now. New ones are added regularly. Please try again later.';

/** This network has had its copies for the day. Not the server's sentence either. */
export const CLAIM_DAILY_LIMIT =
  'This network has used its demo copies for today. Please try again tomorrow.';

// The sign-in page, on the demo, in a browser that keeps a copy.

/**
 * What the browser keeps, and until when: one sentence with one blank. `until` is the copy's end,
 * already written out as the app writes an instant (src/utils/format.ts): this file holds words,
 * and how a date reads is not its to decide.
 */
export const rememberedCopy = (until: string) =>
  `This browser remembers a demo copy. It works until ${until}.`;

export const CONTINUE_WITH_MY_COPY = 'Continue with my copy';

export const GET_A_NEW_COPY = 'Get a new copy';

export const FORGET_THIS_COPY = 'Forget this copy';

/** Said once "Forget this copy" has been pressed, in a polite region and not as an alert. */
export const COPY_FORGOTTEN = 'This browser no longer remembers the copy.';

/**
 * "Continue with my copy" was answered as a wrong password is. For a pair the browser kept and
 * nobody typed, that answer means the copy is gone, and this is said in the place of "Invalid
 * email or password."
 */
export const COPY_NO_LONGER_AVAILABLE =
  'That demo copy is no longer available. Copies are closed after 24 hours. You can get a new one.';

// The dashboard's panel about the copy the visitor is signed in to.

export const YOUR_PRIVATE_COPY = 'Your private copy';

/**
 * Said to the copy's owner: one sentence with one blank. `until` is the copy's end, already
 * written out as the app writes an instant (src/utils/format.ts).
 */
export const copyIsYours = (until: string) =>
  `Other visitors can't see this copy. It works until ${until}, then it is closed and deleted.`;

/**
 * Said to whoever is signed in to a copy on a browser that does not keep it. That browser has no
 * end to print, so the sentence gives the copy's life instead. "24 hours" is typed out: it is the
 * life a copy has unless a deployment sets another (`Demo:CopyLifetimeHours`,
 * backend/src/AzureBank.Shared/Options/DemoOptions.cs), and the sentence does not follow that
 * setting.
 */
export const COPY_IS_A_DEMO =
  'This is a private demo copy. It works for 24 hours from its first use, then it is closed and deleted.';

/**
 * The copy's PIN, for everyone signed in to it. A line of the panel and not a sentence, so it
 * ends on no full stop. `pin` is the digits (src/features/demo/demoPin.ts).
 */
export const copyPin = (pin: string) => `PIN: ${pin}, unless you changed it`;

/**
 * Whom the copy can pay: each handle with its `@`, joined by " and ", however many there are.
 * The caller leaves the line out when there are none.
 */
export const contactsYouCanPay = (handles: readonly string[]) =>
  `Contacts you can pay: ${handles.map((handle) => `@${handle}`).join(' and ')}`;

export const SHOW_SIGN_IN_DETAILS = 'Show sign-in details';

export const HIDE_SIGN_IN_DETAILS = 'Hide sign-in details';

/** What signs in to the copy, shown to its owner once asked for. */
export const signInDetails = (email: string, password: string) =>
  `Email: ${email} · Password: ${password}`;
