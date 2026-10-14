import { rmSync } from 'node:fs';
import { resolve } from 'node:path';
import { defineConfig, type Plugin } from 'vite';
import react from '@vitejs/plugin-react';

/*
  THE MOCK'S SERVICE WORKER STAYS OUT OF A BUILD.

  `public/mockServiceWorker.js` is the script the mock registers in the browser. It has to be in
  `public/`: the mock library's own tooling writes it there (`msw.workerDirectory` in
  package.json), and the dev server of `npm run dev:mock` serves it from there. Vite copies
  `public/` into `dist/` as it is, so a build would ship a service worker script that nothing in
  the built app registers: src/main.tsx starts the mock in the `mock` mode alone. This plugin
  takes the copy out when the bundle is closed. A build made in the `mock` mode keeps it.

  `apply: 'build'`: the dev server never runs this, so `npm run dev:mock` finds the file where it
  is. CI fails a build that holds the file (ci.yml, "No mock service worker in the build").
*/
function noMockWorkerInABuild(): Plugin {
  let copy: string | undefined;
  return {
    name: 'azurebank-no-mock-worker-in-a-build',
    apply: 'build',
    configResolved(config) {
      copy =
        config.mode === 'mock'
          ? undefined
          : resolve(config.root, config.build.outDir, 'mockServiceWorker.js');
    },
    closeBundle() {
      if (copy) rmSync(copy, { force: true });
    },
  };
}

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
    noMockWorkerInABuild(),
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
