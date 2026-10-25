#!/usr/bin/env node
/**
 * Rewrite design-tokens/tokens.json from the theme in src/theme.
 *
 *   node scripts/export-design-tokens.mjs        (from frontend/)
 *
 * The file is generated and never edited by hand. src/theme/designTokens.test.ts fails when it
 * differs from what src/theme/designTokens.ts builds, and its message names this command.
 *
 * The theme is TypeScript, and the app's own bundler already knows how to read it, so the module is
 * loaded through the installed Vite in middleware mode (no HTTP server, no watcher) rather than
 * through a second compiler. Nothing here is installed for the purpose.
 */
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createServer } from 'vite';

const frontend = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const target = resolve(frontend, '..', 'design-tokens', 'tokens.json');

const server = await createServer({
  root: frontend,
  // The app's config loads the React plugin and the test setup, and the theme needs neither.
  configFile: false,
  appType: 'custom',
  logLevel: 'error',
  server: { middlewareMode: true, hmr: false, ws: false, watch: null },
  optimizeDeps: { noDiscovery: true, include: [] },
});

try {
  const { buildDesignTokens, serialiseDesignTokens } = await server.ssrLoadModule(
    '/src/theme/designTokens.ts',
  );
  mkdirSync(dirname(target), { recursive: true });
  // Written as bytes, so the file is LF whatever the platform's text mode.
  writeFileSync(target, Buffer.from(serialiseDesignTokens(buildDesignTokens()), 'utf8'));
  console.log(`wrote ${target}`);
} finally {
  await server.close();
}
