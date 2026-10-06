import './zodConfig';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import './index.css';
import App from './App.tsx';

/**
 * Optionally start the MSW mock backend BEFORE the app renders. Gated on the Vite mode so it only
 * runs under `npm run dev:mock` (vite --mode mock) — a normal `npm run dev` and the production build
 * never load it (the dynamic import keeps MSW out of the app graph when the mode isn't 'mock').
 */
async function enableMocking() {
  if (import.meta.env.MODE !== 'mock') return;
  const { worker } = await import('./mocks/browser');
  const { mockStartupLine } = await import('./mocks/startupLine');
  // Let real dev-server / asset requests through; only the mocked API/BFF routes are intercepted.
  await worker.start({ onUnhandledRequest: 'bypass' });
  // How to get in, which depends on whether the page is the demo (src/mocks/startupLine.ts).
  console.info(`%c${mockStartupLine()}`, 'color:#0a7');
}

void enableMocking().then(() => {
  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <App />
    </StrictMode>,
  );
});
