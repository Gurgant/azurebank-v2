import { setupWorker } from 'msw/browser';
import { handlers } from './handlers';

/**
 * The BROWSER-side MSW worker — the same contract-faithful handlers the tests use, but running in a
 * real Service Worker so you can click through the whole app in the browser with a fake backend (no
 * BFF / API / DB). Started from main.tsx only when VITE_ENABLE_MSW is set (`npm run dev:mock`); it is
 * never loaded in a normal dev or production build. State resets on every page reload (module re-init).
 */
export const worker = setupWorker(...handlers);
