import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

/**
 * Phase 2 — the app's OWN data layer against the real backend.
 *
 * Distinct from the contract suite in one decisive way: that one uses a raw fetch client on purpose,
 * to see what the SERVER sent. This one runs the real `problemBaseQuery`, the real Zod response
 * schemas and the real idempotency hook, to see what the APP MAKES of it. Drift lives in both gaps.
 *
 * `environmentOptions.jsdom.url` is load-bearing rather than cosmetic: `problemBaseQuery` pins
 * `baseUrl` to `window.location.origin`, so setting the document's URL to the BFF is what points the
 * unmodified data layer at the real stack. No production code is aware this suite exists.
 */
export default defineConfig({
  plugins: [react()],
  test: {
    environment: 'jsdom',
    environmentOptions: { jsdom: { url: 'http://localhost:5000' } },
    globals: true,
    include: ['src/integration/**/*.test.ts?(x)'],
    setupFiles: ['./src/integration/setup.ts'],
    // Sequential: the suite shares one server-side session and a rate-limited login.
    fileParallelism: false,
  },
});
