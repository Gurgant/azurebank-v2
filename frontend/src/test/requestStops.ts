/**
 * How to stop the requests of each store a test made, kept on the page rather than in this
 * module: a test that empties the module registry (`mediaQueryOrder.test.tsx` does) makes its
 * stores with new copies of the modules, while `setup.ts` calls the copy it imported first. No
 * imports, so that `setup.ts` loads none of the app before its stubs are in place.
 */
function stops(): Set<() => void> {
  const page = globalThis as { __testRequestStops?: Set<() => void> };
  return (page.__testRequestStops ??= new Set());
}

/** Notes how to stop the requests of a store a test made. `makeTestStore` calls it. */
export function noteRequestsToStop(stop: () => void) {
  stops().add(stop);
}

/**
 * Stops every request that the stores noted since the last call still have on their way, and
 * forgets those stores. `setup.ts` calls it when a test ends: a read waiting for its retry would
 * otherwise go out seconds later, in whichever test runs then, and that test's handlers would
 * answer it. A store made another way than `makeTestStore`, as `not-found.test.tsx` makes the
 * app's own, is not stopped.
 */
export function stopRequestsLeftRunning() {
  const all = stops();
  for (const stop of all) stop();
  all.clear();
}
