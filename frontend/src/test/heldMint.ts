import { act, waitFor } from '@testing-library/react';
import { http } from 'msw';
import { expect } from 'vitest';
import type { apiSlice } from '../features/api/apiSlice';
import { server } from '../mocks/server';
import { actionsSeen, type TestStore } from './renderWithProviders';

/** The endpoints that ask for an authorisation. Every other mutation a flow makes is its send. */
const MINTS: readonly (keyof typeof apiSlice.endpoints)[] = [
  'authoriseTransfer',
  'authoriseInternalTransfer',
  'authoriseWithdrawal',
  'authoriseAccountDeletion',
];

/**
 * Holds the answer to a money flow's request for an authorisation, so that a test can leave the
 * page while it is out: the PIN is being checked, no key is held yet, and the send follows.
 *
 * `arrived` settles when the request has reached the mock, and nothing answers it before
 * `release()`. It is then answered by the mock's own handler, as it is when nothing is held, or by
 * `answer` when the test gives one (a refusal). A test that fails before it releases leaves the
 * request pending, as `never` does, and the next test finds nothing of it.
 *
 * `answered(store)` is for a test that expects no send. It waits until the client has taken the
 * answer, which the store shows as the mint's fulfilled action, or its rejected one when the test
 * says `'rejected'` (the answer it gave was a refusal), and then one turn of the event loop. What
 * the flow does with the answer is a chain of promises, and a chain settles within the turn: either
 * it has asked for its send, which the store shows as the send's own action before any request
 * leaves, or it has refused. Only then does it expect that the store saw no send. A fixed wait
 * could end before the client had the answer, and the absence of a send would then be the send not
 * having come yet. The outcome is named, so that a mint refused where it should have passed is a
 * failure and not a quiet absence of a send.
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
    async answered(store: TestStore, outcome: 'fulfilled' | 'rejected' = 'fulfilled') {
      const mutations = () =>
        actionsSeen(store).filter(
          ({ type, endpointName }) =>
            type.startsWith('api/executeMutation/') && endpointName !== undefined,
        );
      const isMint = ({ endpointName }: { endpointName?: string }) =>
        MINTS.some((mint) => mint === endpointName);
      await waitFor(() =>
        expect(
          mutations().some((action) => isMint(action) && action.type.endsWith(`/${outcome}`)),
        ).toBe(true),
      );
      await act(async () => {
        await new Promise((resolve) => setTimeout(resolve, 0));
      });

      expect(mutations().filter((action) => !isMint(action))).toEqual([]);
    },
  };
}
