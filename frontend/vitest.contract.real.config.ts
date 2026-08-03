import { defineConfig } from 'vitest/config';
import { contractTest } from './vitest.contract.config';

/**
 * The same suite, pointed at the REAL API + BFF.
 *
 * A second config rather than a shell variable: `CONTRACT_TARGET=real vitest` is POSIX-only syntax.
 * Under PowerShell and cmd the assignment is parsed as a command NAME and the run dies before vitest
 * starts ("'CONTRACT_TARGET=real' is not recognized..."), so on this repo's own dev platform the
 * real target would simply be unrunnable. Measured, not assumed.
 *
 * Needs the stack up (`azurebank-api` :7215 + `azurebank-bff` :5000, database migrated and seeded).
 * If nothing answers, the setup THROWS rather than skipping — see src/contract/setup.ts.
 */
export default defineConfig({
  test: { ...contractTest, env: { CONTRACT_TARGET: 'real' } },
});
