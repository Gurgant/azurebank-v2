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

/** From here the wait says "Still trying…", and a read may offer "Stop waiting". */
export const STILL_TRYING_AFTER_MS = 20_000;

/** What a wait has to say so far: nothing, that it is slow, or that it is still trying. */
export type WaitPhase = 'none' | 'slow' | 'stillTrying';

/**
 * Where a wait that started when `active` turned true has got to.
 *
 * `useDelayedFlag`'s pattern with two thresholds: the timers are the only thing that sets the
 * phase, the cleanup puts it back to `'none'` when `active` flips or the host unmounts, and the
 * value returned is derived — `active ? phase : 'none'` — so a wait that ends is silent in the very
 * render it ends, with no state update of its own. A new wait starts from zero: the cleanup clears
 * the old timers, so a wait that ended at 3 s cannot speak into the next one at 5 s.
 */
export function useWaitPhase(active: boolean): WaitPhase {
  const [phase, setPhase] = useState<WaitPhase>('none');

  useEffect(() => {
    if (!active) return;
    const slow = setTimeout(() => setPhase('slow'), SLOW_AFTER_MS);
    const stillTrying = setTimeout(() => setPhase('stillTrying'), STILL_TRYING_AFTER_MS);
    return () => {
      clearTimeout(slow);
      clearTimeout(stillTrying);
      setPhase('none');
    };
  }, [active]);

  return active ? phase : 'none';
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
