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
