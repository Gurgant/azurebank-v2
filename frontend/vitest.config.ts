import { defineConfig, configDefaults } from 'vitest/config';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
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
