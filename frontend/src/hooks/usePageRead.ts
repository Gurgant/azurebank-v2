import type { ApiProblem } from '../api/problemBaseQuery';
import { useAppDispatch } from '../app/hooks';
import { abortRunning, useWaitLanding, type ReadTarget } from './useWaitLanding';
import { readWait, type ReadState } from './useWaitPhase';

/** What a page's query hook reports about its read, as much of it as the page's wait needs. */
type PageRead = ReadState & { refetch(): unknown; requestId?: string };

/**
 * The wait of a page that shows one read, and the two ways out of it (ADR-0059): whether the read
 * is waiting or has failed (`readWait`), the failure itself for the page's `ReadErrorBar`, and
 * "Stop waiting" and Retry, each of which arms the landing that brings focus back to the bar's
 * Retry (`useWaitLanding`). The page puts `landingRef` on that Retry.
 *
 * `target` must name the cache entry the page's query hook reads, with the same argument, or Stop
 * finds nothing running and stops nothing. An infinite query passes its result with
 * `isFetching: isFetching && !isFetchingNextPage`, and with no `error` while a next page has failed
 * or is on its way (`isFetchNextPageError || isFetchingNextPage`), so that "Load more" is neither
 * the page's wait nor its failure.
 *
 * A page whose reads share one landing and one Stop, as the Dashboard's three do, builds its wait
 * from the same parts itself.
 */
export function usePageRead(query: PageRead, target: ReadTarget) {
  const { waiting, failed } = readWait(query);
  const problem = failed ? (query.error as ApiProblem) : undefined;

  const dispatch = useAppDispatch();
  const { landingRef, arm } = useWaitLanding<HTMLButtonElement>(waiting, query.requestId);
  // Nothing was running if the answer came as Stop was pressed: no bar is coming to land on.
  const stopWaiting = () => {
    if (abortRunning(dispatch, [target])) arm();
  };
  const retry = () => {
    arm();
    void query.refetch();
  };

  return { waiting, failed, problem, landingRef, stopWaiting, retry };
}
