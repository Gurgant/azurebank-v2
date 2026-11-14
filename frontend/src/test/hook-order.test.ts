/**
 * `setup.ts` depends on vitest's hook ORDER, so the order is pinned rather than assumed.
 *
 * The console.error gate in `setup.ts` throws, and a throwing `afterEach` skips the hooks still to
 * run. It must therefore run AFTER the teardown that unmounts the tree and resets MSW, the mock
 * state, the session mirrors and the viewport — otherwise one failing test hands all of that to the
 * next one, and the second failure is fiction.
 *
 * Vitest's default is `sequence.hooks: 'stack'`: `afterEach` runs in REVERSE registration order. So
 * "runs last" is bought by registering FIRST, which is the opposite of how it reads, and is exactly
 * the kind of assumption that rots silently — setting `sequence.hooks: 'list'` in
 * `vitest.config.ts` would invert it with nothing else to notice. This test is what notices: it
 * fails, and the fix is to move the gate hook to the other end of `setup.ts`.
 *
 * Measured rather than quoted: instrumenting both of `setup.ts`'s hooks and printing the order gave
 * `file-hook -> setup:assertion -> setup:teardown` while the gate was registered last, i.e. it ran
 * before the teardown it was written to follow.
 */

import { http } from 'msw';
import { apiSlice } from '../features/api/apiSlice';
import { serviceUnavailable } from '../mocks/problem';
import { server } from '../mocks/server';
import { makeTestStore } from './renderWithProviders';

// Each pair below is in order on purpose, and `shuffle: false` keeps it so when the run shuffles.
describe('the order of afterEach hooks', { shuffle: false }, () => {
  const order: string[] = [];

  afterEach(() => order.push('registered-first'));
  afterEach(() => order.push('registered-second'));

  it('primes the hooks (they run after this test, not before it)', () => {
    expect(order).toEqual([]);
  });

  it('runs afterEach in REVERSE registration order, so registering first runs last', () => {
    expect(order).toEqual(['registered-second', 'registered-first']);
  });
});

/*
  `setup.ts` puts the real clock back after every test, so no file has to. The first test leaves
  the clock fake on purpose; the next one must find it real.
*/
describe('the clock between tests', { shuffle: false }, () => {
  it('leaves the clock fake at the end of a test', () => {
    vi.useFakeTimers();
    expect(vi.isFakeTimers()).toBe(true);
  });

  it('starts the next test on the real clock, which setup.ts put back', () => {
    expect(vi.isFakeTimers()).toBe(false);
  });
});

/*
  `setup.ts` stops the requests a test leaves running. The first test leaves a read of the session
  waiting for its retry; the next one must hear nothing from it. Left running, the retry goes out
  in whichever test runs a second later, which can be in another file (`isolate: false` gives the
  files of a worker one page and one mock server), and that test's handlers answer it.
*/
describe('the requests between tests', { shuffle: false }, () => {
  it('leaves a read of the session waiting for its retry', async () => {
    let answered = 0;
    server.use(
      http.get('*/bff/auth/me', () => {
        answered += 1;
        return serviceUnavailable({ via: 'bff', instance: '/bff/auth/me', retryAfterSeconds: 1 });
      }),
    );
    void makeTestStore().dispatch(
      apiSlice.endpoints.getMe.initiate(undefined, { subscribe: false }),
    );
    await vi.waitFor(() => expect(answered).toBe(1));
  });

  it('hears nothing from it in the next test', async () => {
    let arrived = 0;
    server.use(
      http.get('*/bff/auth/me', () => {
        arrived += 1;
        return undefined;
      }),
    );
    // The retry would go out a second after the 503, and up to a fifth of a second later.
    await new Promise((resolve) => setTimeout(resolve, 1_500));
    expect(arrived).toBe(0);
  });
});
