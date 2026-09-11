import { z } from 'zod';

/*
  NO EVAL, because the BFF's Content-Security-Policy allows none (ADR-0054).

  Zod compiles object parsers with `Function(...)` when it can, and it finds out whether it can by
  TRYING: under `script-src 'self'` that probe throws, Zod falls back to the plain parser, and Chrome
  files a CSP violation anyway. Measured on the build served by the BFF: a six-page walk raised six,
  all `script-src`, blocked `eval`, one per page load. `jitless` makes Zod skip both the probe and the
  compiler, so the policy holds with nothing to explain away.

  Imported FIRST by main.tsx, before any module that builds or runs a schema, and by the test setup,
  so the suite parses the way the browser does.
*/
z.config({ jitless: true });
