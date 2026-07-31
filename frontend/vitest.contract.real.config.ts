import { defineConfig } from 'vitest/config';
import { contractTest } from './vitest.contract.config';

/**
 * The same suite, pointed at the REAL API + BFF.
 *
 * A second config rather than a shell variable: `CONTRACT_TARGET=real vitest` is bash-only syntax,
 * and under cmd/PowerShell it does nothing at all — the run would silently execute against the MOCK
 * and report success, which is the one outcome this whole directory exists to make impossible.
 *
 * Needs the stack up (`azurebank-api` :7215 + `azurebank-bff` :5000, database migrated and seeded).
 * If nothing answers, the setup THROWS rather than skipping — see src/contract/setup.ts.
 */
export default defineConfig({
  test: { ...contractTest, env: { CONTRACT_TARGET: 'real' } },
});
