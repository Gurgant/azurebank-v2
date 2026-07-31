import { defineConfig } from 'vitest/config';

/**
 * The contract suite, run separately from the unit suite on purpose.
 *
 * Separate because the `real` target needs a live API+BFF, which CI and a plain `npm test` must
 * never depend on. The default unit run excludes `src/contract/**` (see vitest.config.ts), so these
 * files execute only through the two `test:contract:*` scripts.
 *
 * `node`, not `jsdom`: there is no DOM here — only status codes, headers and bodies. Parallelism is
 * off because the real target shares one seeded database and one session per run, so parallel files
 * would race on the PIN lockout counter and on account balances.
 *
 * The target is carried in `test.env` rather than by a shell variable, because the shell-prefix form
 * is POSIX-only and this repo is developed on Windows. Measured, after an earlier draft of this
 * comment guessed wrong about it:
 *   PowerShell -> The term 'CONTRACT_TARGET=real' is not recognized as a name of a cmdlet...
 *   cmd        -> 'CONTRACT_TARGET' is not recognized as an internal or external command
 * It fails before vitest starts rather than quietly running the wrong target, so the risk is an
 * unrunnable real suite, not a false pass. Two configs make both targets work identically on every
 * shell, and cost no extra dependency.
 */
export const contractTest = {
  environment: 'node' as const,
  globals: true,
  include: ['src/contract/**/*.contract.test.ts'],
  setupFiles: ['./src/contract/setup.ts'],
  fileParallelism: false,
  // The real stack is slower than MSW, and the first request pays for JIT plus a DB connection.
  testTimeout: 30_000,
  hookTimeout: 30_000,
};

export default defineConfig({
  test: { ...contractTest, env: { CONTRACT_TARGET: 'mock' } },
});
