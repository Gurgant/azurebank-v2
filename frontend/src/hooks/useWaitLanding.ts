import { useCallback, useEffect, useRef } from 'react';
import type { AppDispatch } from '../app/store';
import { apiSlice } from '../features/api/apiSlice';

type Endpoints = typeof apiSlice.endpoints;
type FindRunning = typeof apiSlice.util.getRunningQueryThunk;

/** Every read the API has — a query or an infinite query — by endpoint name. */
type ReadName = Parameters<FindRunning>[0];

/**
 * A read named by its cache entry: the endpoint, and the argument the page asked it with. The
 * argument must be the one the page's hook uses, or it names another entry and nothing is stopped.
 */
export type ReadTarget = {
  [K in ReadName]: { endpoint: K; arg: Parameters<Endpoints[K]['initiate']>[0] };
}[ReadName];

/** A read the page started itself and kept, such as what a lazy query's trigger returned. */
export interface StartedRead {
  readonly queryCacheKey: string;
}

export type WaitTarget = ReadTarget | StartedRead;

/*
  `getRunningQueryThunk` is generic in the endpoint, and TypeScript cannot carry a union of
  { endpoint, arg } pairs into it. The pair is checked where a page writes it (`ReadTarget`); this
  one widening only lets the loop below hand it on.
*/
const findRunning = apiSlice.util.getRunningQueryThunk as (
  endpoint: ReadName,
  arg: unknown,
) => ReturnType<FindRunning>;

/**
 * Stops the requests behind a read the visitor gave up on, and says whether any was still running.
 *
 * Every target is tried; none is skipped because an earlier one was stopped (the Dashboard stops
 * three reads with one press). Aborting a read rejects its cache entry at once with an `AbortError`
 * and no status, so the page shows its usual "Could not load…" bar, and it cancels the retry that
 * `problemBaseQuery` may be waiting to send. The server may still finish the read; nothing is
 * written, so that is harmless.
 *
 * A read the page kept (a lazy trigger's result) is found again by its cache key, and the request
 * actually running for that key is stopped — not the kept one's own `abort()`. A trigger that met
 * a read already running for the same key never started a request of its own, and its `abort()`
 * would stop nothing.
 *
 * Returns false when nothing was running: the answer arrived as Stop was pressed. The caller then
 * arms no landing (`useWaitLanding`), because there is no bar coming to land on.
 */
export function abortRunning(dispatch: AppDispatch, targets: readonly WaitTarget[]): boolean {
  let aborted = false;
  for (const target of targets) {
    const running =
      'queryCacheKey' in target
        ? dispatch(apiSlice.util.getRunningQueriesThunk()).find(
            (request) => request.queryCacheKey === target.queryCacheKey,
          )
        : dispatch(findRunning(target.endpoint, target.arg));
    if (!running) continue;
    running.abort();
    aborted = true;
  }
  return aborted;
}

/**
 * Puts focus back where the visitor can act, when a wait they ended or restarted is over.
 *
 * "Stop waiting" unmounts under the pointer, and so does a Retry (its bar hides while the read
 * runs again), so without this focus falls to `body` and a keyboard or screen-reader visitor has to
 * find the page again. The host puts `landingRef` on the control the wait lands on — the Retry of
 * the error bar the failure brings back — and calls `arm()` when the visitor stops a wait
 * (only if `abortRunning` stopped something) or presses Retry.
 *
 * `request` names the request behind the host's reads: the `requestId` its query hook reports, or,
 * for a host with several reads, all of theirs. An RTK read gets a new `requestId` each time it
 * sends a request, and keeps it once that request is answered or aborted.
 *
 * The arm belongs to the request the visitor acted on, and is spent when that request is over:
 * - a wait on screen ends (a Stop's abort, or a Retry's wait that the page showed);
 * - or the page, at rest, is on another request than the one it showed when `arm()` was called.
 *   RTK hands a request's start and its answer to the page together when the answer comes
 *   within one frame, so a Retry can go from failed to failed, or to loaded, without the page
 *   ever showing it as waiting.
 *
 * Spending it moves focus to the landing if one is on screen: a failure lands on the new bar's
 * Retry; a success leaves no bar of its own, so focus stays where it is, unless another read of
 * the same host still shows a failed bar, which is then the landing. Until then a render at rest
 * keeps the arm: a Retry was pressed and its request has not reached the page yet. Once spent, a
 * later wait the visitor did not start — a refetch after a change elsewhere, say — can never pull
 * focus, out of a dialog or anywhere else.
 */
export function useWaitLanding<T extends HTMLElement = HTMLElement>(
  waiting: boolean,
  request: string | undefined | readonly (string | undefined)[],
) {
  const landingRef = useRef<T | null>(null);
  const wasWaiting = useRef(waiting);
  // requestIds never contain a space, so the joined key names exactly one set of requests.
  const requestKey = typeof request === 'object' ? request.join(' ') : request;
  const shownRequest = useRef(requestKey);
  const armedFor = useRef<{ requestKey: string | undefined } | null>(null);

  useEffect(() => {
    const ended = wasWaiting.current && !waiting;
    wasWaiting.current = waiting;
    shownRequest.current = requestKey;

    const armed = armedFor.current;
    if (!armed || waiting) return;
    // Armed by a Retry whose request has not reached the page yet.
    if (!ended && armed.requestKey === requestKey) return;

    armedFor.current = null;
    landingRef.current?.focus();
  });

  const arm = useCallback(() => {
    armedFor.current = { requestKey: shownRequest.current };
  }, []);

  return { landingRef, arm };
}
