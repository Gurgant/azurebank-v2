import { setupServer } from 'msw/node';
import { handlers } from './handlers';

/**
 * Test-only MSW server (Decision D11: NO browser worker in this chapter — dev runs
 * against the real BFF via the Vite proxy; these handlers exist for the test suite).
 */
export const server = setupServer(...handlers);
