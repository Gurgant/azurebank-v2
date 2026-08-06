import { defineConfig, configDefaults } from 'vitest/config';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    /*
      TIMING BUDGET, and why it is not just "make the red go away".

      The suite was intermittently red — always a different test, always green on re-run, never
      reproducible in isolation. Measured 2026-08-06 by running two full suites CONCURRENTLY, which
      is what a shared CI runner looks like:

        unloaded          6/6 runs green
        under contention  6/6 runs RED, 8 different files, two signatures:
                            11x "Test timed out in 5000ms"      (vitest default)
                             9x "Unable to find role=..."       (testing-library's 1s default)

      So it was never one flaky test. The suite simply runs close to both defaults, and any CPU
      contention tips the slowest cases over — `register-handoff` most often, because it types six
      fields keystroke by keystroke.

      Raising these limits does not hide a broken assertion: a query that will never match still
      fails, just later. It only removes the failures that would have PASSED given time, which are
      false by construction. The cost is that a genuinely hung test takes 20s to say so.

      Precedent: `vitest.contract.config.ts` already sets 30s for the same reason — a suite whose
      timing it does not control deserves a budget that does not depend on the machine.
    */
    testTimeout: 20_000,
    hookTimeout: 20_000,
    include: ['src/**/*.test.{ts,tsx}'],
    /*
      The contract suite is excluded from the default run because half of it (CONTRACT_TARGET=real)
      needs a live API + BFF, and neither CI nor `npm test` may depend on a running stack. It has
      its own config and its own two scripts; see vitest.contract.config.ts.

      `src/integration/**` is excluded for the same reason and more strongly: unlike the contract
      suite it has NO mock target at all — it exists to run the app's own data layer against the
      real stack, so every file in it fails without one. See vitest.integration.config.ts.

      Spread from `configDefaults.exclude` rather than replacing it — setting this key outright
      would silently drop node_modules and dist from the exclusions.
    */
    exclude: [...configDefaults.exclude, 'src/contract/**', 'src/integration/**'],
  },
});
