import { MOCK_PASSWORD, MOCK_PIN, MOCK_USER, mockDemoEnabled } from './state';

/**
 * What the console says once the mock is on (`npm run dev:mock`, src/main.tsx): how to get in.
 *
 * It follows the demo's tag, because the mock's sign-in does. Without the tag the mock signs its
 * own user in, and the line names that pair. With it (`AZUREBANK_DEMO=true`, vite.config.ts) the
 * mock signs in claimed copies only and answers its own user 401 (`accountForLogin` in
 * ./handlers.ts): the way in is "Try the demo", and a line that still named the pair would send
 * whoever read it to a refusal. The PIN is the same in both: the demo's copies start with the
 * mock's own.
 *
 * A module of its own so that a test can ask for the line: src/main.tsx mounts the app when it
 * is loaded, and loads this only in Vite's `mock` mode, beside the worker.
 */
export function mockStartupLine(): string {
  return mockDemoEnabled()
    ? `[MSW] Mock backend ON, as the public demo: press "Try the demo" (PIN ${MOCK_PIN})`
    : `[MSW] Mock backend ON — sign in with ${MOCK_USER.email} / ${MOCK_PASSWORD} (PIN ${MOCK_PIN})`;
}
