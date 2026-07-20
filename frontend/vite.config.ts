import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    // Dev topology (D18): BOTH surfaces forward to the BFF origin so the session cookie
    // stays first-party. Prod equivalent: the BFF serves the SPA dist/ itself (BE-3).
    proxy: {
      '/api': 'http://localhost:5000',
      '/bff': 'http://localhost:5000',
    },
  },
});
