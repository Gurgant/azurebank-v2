import { useEffect, useState } from 'react';

/*
  How long a wait stays silent, and when it says more (ADR-0059).

  Below 5 s a hint is a flicker that tells the visitor nothing: most requests end well inside it,
  and a sentence that appears and vanishes on every load teaches people to ignore it. From 5 s the
  visitor is owed a word that the page has not frozen; from 20 s, that it is still at it, and on a
  read the offer to stop. Neither number comes from measured response times yet; they live here
  and nowhere else, so that a measurement can move them for every wait at once.
*/

/** The wait's first word, "Taking longer than usual…", appears this long after it starts. */
export const SLOW_AFTER_MS = 5_000;

/**
 * The session check at start-up says its first word this long after it starts: a second after
 * every other wait. When the API is slow or down, the BFF answers that check from its cache once
 * its read-through ceiling is up, 5 s (`BffAuthController.ReadThroughTimeout`), so a word due at
 * 5 s raced that answer: measured in Chromium on 2026-10-01, it flashed for 20 to 33 ms on each of
 * five reloads during an outage, then the page came. `timeoutChain.test.ts` reads the ceiling from
 * the file that sets it.
 */
export const SESSION_CHECK_SLOW_AFTER_MS = 6_000;

/** From here the wait says "Still trying…", and a read may offer "Stop waiting". */
export const STILL_TRYING_AFTER_MS = 20_000;

/**
 * How long a wait that said something and then ended keeps its region, for the one word a read
 * says at its end ("Loaded."), before the region goes. Not measured against a screen reader; the
 * word is never shown, so keeping it costs nothing on screen.
 */
export const LOADED_KEPT_MS = 2_000;

/**
 * What a wait has to say so far: nothing, that it is slow, or that it is still trying; and, just
 * after it, `'ended'` if it had said something.
 */
export type WaitPhase = 'none' | 'slow' | 'stillTrying' | 'ended';

/**
 * Where a wait that started when `active` turned true has got to, and, once it is over, whether
 * it had said something: `'ended'` from the render it ends in, for `LOADED_KEPT_MS`, then
 * `'none'`. A wait that ends before its first word (`slowAfterMs`, 5 s unless the host says
 * otherwise) goes straight to `'none'`.
 *
 * `useDelayedFlag`'s pattern with two thresholds: the timers are the only thing that sets the
 * phase, and the cleanup puts it back to `'none'` when `active` flips or the host unmounts. A new
 * wait starts from zero: the cleanup clears the old timers, so a wait that ended at 3 s cannot
 * speak into the next one at 5 s, and a wait that starts drops an `'ended'` at once.
 *
 * The end is noticed in the render that sees `active` turn false, while `phase` is still the
 * wait's own (its cleanup runs after that render), so `'ended'` is there in that very render: the
 * host never draws a frame without its region, which would make the next one a new region, and a
 * new region filled at once is not a change a screen reader reads.
 */
export function useWaitPhase(active: boolean, slowAfterMs: number = SLOW_AFTER_MS): WaitPhase {
  const [phase, setPhase] = useState<'none' | 'slow' | 'stillTrying'>('none');
  const [ended, setEnded] = useState(false);
  const [wasActive, setWasActive] = useState(active);
  if (wasActive !== active) {
    setWasActive(active);
    setEnded(!active && phase !== 'none');
  }

  useEffect(() => {
    if (!active) return;
    const slow = setTimeout(() => setPhase('slow'), slowAfterMs);
    const stillTrying = setTimeout(() => setPhase('stillTrying'), STILL_TRYING_AFTER_MS);
    return () => {
      clearTimeout(slow);
      clearTimeout(stillTrying);
      setPhase('none');
    };
  }, [active, slowAfterMs]);

  useEffect(() => {
    if (!ended) return;
    const done = setTimeout(() => setEnded(false), LOADED_KEPT_MS);
    return () => clearTimeout(done);
  }, [ended]);

  if (active) return phase;
  return ended ? 'ended' : 'none';
}

/** The parts of an RTK Query hook's result that say whether a read is on its way. */
export interface ReadState {
  isLoading: boolean;
  isFetching: boolean;
  error?: unknown;
}

/**
 * Whether a read's page should show its wait, or its error bar.
 *
 * `isLoading` alone misses the wait a Retry starts. A refetch after an error keeps the old error
 * and leaves `isLoading` false, so a page that shows its bar on `error` keeps the bar — the same
 * words, never announced again — for the whole second wait, with no spinner and no hint. So a read
 * is **waiting** on its first load and on any refetch that follows a failure, and it has **failed**
 * only once nothing is fetching. A refetch with data on screen is neither: the stale data stays.
 *
 * An infinite query passes `isFetching: isFetching && !isFetchingNextPage`, so that "Load more"
 * does not put the whole list back into its first-load wait.
 */
export function readWait(q: ReadState): { waiting: boolean; failed: boolean } {
  const hasError = q.error !== undefined;
  return {
    waiting: q.isLoading || (q.isFetching && hasError),
    failed: hasError && !q.isFetching,
  };
}
