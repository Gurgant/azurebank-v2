import { setupServer } from 'msw/node';
import { handlers } from './handlers';

/**
 * Test-only MSW server (CONVENTIONS.md, "MSW is a test tool" — dev runs
 * against the real BFF via the Vite proxy; this server feeds the handlers to the test suite).
 */
export const server = setupServer(...handlers);
