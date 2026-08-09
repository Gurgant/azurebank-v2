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

const order: string[] = [];

afterEach(() => order.push('registered-first'));
afterEach(() => order.push('registered-second'));

it('primes the hooks (they run after this test, not before it)', () => {
  expect(order).toEqual([]);
});

it('runs afterEach in REVERSE registration order, so registering first runs last', () => {
  expect(order).toEqual(['registered-second', 'registered-first']);
});
