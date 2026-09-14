#!/usr/bin/env node
/**
 * Fail the real-stack e2e job when any spec was SKIPPED, or when nothing passed.
 *
 * Until 2026-09-11 the two specs that prove the balance guard and the PIN-lock countdown against the
 * real stack — `balanceGuard.spec.ts` and `pinLockExpiry.spec.ts` — skipped on every CI run, because
 * the job never set the throwaway-user variables they need. `test.skip` is quiet by design: the job
 * reported green, and the only browser proofs of two money-safety behaviours had never run in CI.
 * A skip is not a pass. This script makes it a failure, the way the SQL proofs' step already refuses
 * a `.trx` whose executed count is zero.
 *
 * Reads the JSON reporter's `stats`, whose field names are taken from the installed Playwright's own
 * `JSONReport` type (`node_modules/playwright/types/testReporter.d.ts`), not from memory: a wrong
 * field name would read as zero and this script would pass on everything.
 */
import { existsSync, readFileSync } from 'node:fs';

const path = process.argv[2] ?? 'playwright-report/e2e-results.json';

if (!existsSync(path)) {
  console.log(
    `::error::no Playwright JSON report at ${path} — the reporter did not run, so nothing can be asserted`,
  );
  process.exit(1);
}

const { stats } = JSON.parse(readFileSync(path, 'utf8'));
const { expected = 0, unexpected = 0, flaky = 0, skipped = 0 } = stats ?? {};
console.log(`e2e: passed=${expected} failed=${unexpected} flaky=${flaky} skipped=${skipped}`);

if (skipped > 0) {
  console.log(
    `::error::${skipped} e2e test(s) were SKIPPED. A skipped spec proves nothing and the job still ` +
      'reads green — set the variable it asked for, or delete the spec; never leave it quietly off.',
  );
  process.exit(1);
}

if (expected === 0) {
  console.log('::error::no e2e test passed — the suite ran nothing, which is not a pass');
  process.exit(1);
}
