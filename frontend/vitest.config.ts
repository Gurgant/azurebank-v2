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
    /*
      The files of a worker share one jsdom page and one module registry, instead of each file
      getting a new pair. Measured on a run with isolation, import, environment and setup made up
      58% of the summed worker time (686 s of 1181 s), and a new page for every file paid them
      again each time. The price is that a test must leave nothing behind for the next file: see
      "Testing with Fluent and jsdom" in CONVENTIONS.md, and `src/test/setup.ts` for what is reset.
      Only this configuration: the contract and integration ones keep isolation.
    */
    isolate: false,
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
    /*
      Off unless asked for: `npm test` measures nothing, and `npm run test:coverage` is the same
      run with `--coverage`.

      The figure is for the hand-written product code. `include` names every source file, so a
      file that no test loads counts with all its lines unrun and is not left out. What is
      excluded is what a test of the product cannot be asked to run:
        - the tests, and the helpers under `src/test` that only tests import;
        - the mock backend under `src/mocks`, which is a test tool (CONVENTIONS.md);
        - `src/contract` and `src/integration`, the two suites this run excludes above, with
          their clients and harnesses;
        - what a generator writes: `src/api/generated` (`npm run generate:zod`) and every
          `.d.ts`, of which `src/api/schema.d.ts` is `npm run generate:api`.

      `json-summary` is what `infra/coverage_summary.py` reads in CI, where the floor is held.
      No threshold is set here, so that there is one floor in one place.
    */
    coverage: {
      provider: 'v8',
      include: ['src/**/*.{ts,tsx}'],
      exclude: [
        'src/**/*.test.{ts,tsx}',
        'src/test/**',
        'src/mocks/**',
        'src/contract/**',
        'src/integration/**',
        'src/api/generated/**',
        'src/**/*.d.ts',
      ],
      reporter: ['text-summary', 'json-summary'],
      reportsDirectory: './coverage',
    },
  },
});
