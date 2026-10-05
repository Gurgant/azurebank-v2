import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// https://vite.dev/config/
export default defineConfig({
  plugins: [
    react(),
    /*
      THE DEMO'S TAG, IN THE DEV SERVER ONLY.

      The app learns that it is the demo from one tag of the page it is served,
      `<meta name="azurebank-demo" content="true">` (src/features/demo/demoMode.ts). On a
      deployment the BFF puts that tag in when its `Demo:Enabled` is set (`DemoTag` in
      backend/src/AzureBank.Bff/Extensions/SpaHostingExtensions.cs). In the dev loop vite serves
      the page and no BFF does, so without this plugin nothing puts the tag on the page here.
      Start the dev server (`npm run dev`, or `npm run dev:mock` for the demo's screens with no
      backend) with `AZUREBANK_DEMO=true` in its environment and the page gets the same tag, last
      in its head, which is where the BFF puts it. With any other value, or with none, nothing
      is added.

      `apply: 'serve'`: a build never runs this plugin, so `dist/index.html` has no tag whatever
      the environment of the build held, and `vite preview` serves that file as it is. Whether a
      deployment is the demo is for the BFF that serves the build to say; a tag built into the
      page would say it everywhere.

      The variable's name has no `VITE_` prefix, on purpose. A variable with that prefix reaches
      the app in `import.meta.env` (vite's `envPrefix`), and the app would then have two places
      to learn the mode from. It has one: the tag.
    */
    {
      name: 'azurebank-demo-tag',
      apply: 'serve',
      transformIndexHtml: () =>
        process.env.AZUREBANK_DEMO === 'true'
          ? [{ tag: 'meta', attrs: { name: 'azurebank-demo', content: 'true' }, injectTo: 'head' }]
          : undefined,
    },
  ],
  server: {
    // Dev topology (D18): BOTH surfaces forward to the BFF origin so the session cookie
    // stays first-party. Prod equivalent: the BFF serves the SPA dist/ itself (BE-3).
    proxy: {
      '/api': 'http://localhost:5000',
      '/bff': 'http://localhost:5000',
    },
  },
  preview: {
    // Same forwarding for `vite preview` — lets the PROD bundle run against the local
    // BFF before BE-3 exists.
    proxy: {
      '/api': 'http://localhost:5000',
      '/bff': 'http://localhost:5000',
    },
  },
});
