import { act, waitFor } from '@testing-library/react';
import { http } from 'msw';
import { expect } from 'vitest';
import { server } from '../mocks/server';
import { mockState } from '../mocks/state';
import { sleep } from './outage';

/** How long a send that the answer to the mint sets off is given to leave, once it is answered. */
const TIME_FOR_A_SEND_MS = 100;

/**
 * Holds the answer to a money flow's request for an authorisation, so that a test can leave the
 * page while it is out: the PIN is being checked, no key is held yet, and the send follows.
 *
 * `arrived` settles when the request has reached the mock, and nothing answers it before
 * `release()`. It is then answered by the mock's own handler, as it is when nothing is held, or by
 * `answer` when the test gives one (a refusal). A test that fails before it releases leaves the
 * request pending, as `never` does, and the next test finds nothing of it.
 *
 * `answered()` is for a test that expects no send, and for the mock's own answer: it waits until
 * the mock has answered the mint, and then for as long as a send made on that answer takes to
 * leave. Without it the absence of a send could be the send not having come yet.
 */
export function holdTheMint(url: string, answer?: () => Response) {
  let arrive!: () => void;
  let release!: () => void;
  const arrived = new Promise<void>((resolve) => {
    arrive = resolve;
  });
  const held = new Promise<void>((resolve) => {
    release = resolve;
  });
  server.use(
    http.post(url, async () => {
      arrive();
      await held;
      // Without `answer`, no answer of its own: the next handler, the mock's, gives it.
      return answer?.();
    }),
  );

  return {
    arrived,
    release,
    async answered() {
      await waitFor(() => expect(mockState.stepUpAuthorizations.size).toBe(1));
      await act(async () => {
        await sleep(TIME_FOR_A_SEND_MS);
      });
    },
  };
}
