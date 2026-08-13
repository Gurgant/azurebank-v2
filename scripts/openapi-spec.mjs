#!/usr/bin/env node
/**
 * Regenerate or verify docs/api/openapiv1.json against a running API.
 *
 *   node scripts/openapi-spec.mjs check    # fail if the committed spec is not what the API serves
 *   node scripts/openapi-spec.mjs regen    # overwrite the committed spec with what the API serves
 *
 * WHY THIS EXISTS
 * The frontend's generated artifacts (`schema.d.ts`, `apiSchemas.ts`) are produced FROM the
 * committed spec, and CI already fails if they drift from it. That gate proves the generated code
 * matches the spec; it can never prove the spec matches the server. Until this script existed, the
 * only way to close that loop was a sequence somebody had worked out once and written into a commit
 * message — which is not a procedure, it is a rumour.
 *
 * THE TWO TRAPS, both of which cost time before this was written down.
 *
 * 1. `MapOpenApi()` is registered ONLY when the environment is Development
 *    (AzureBank.Api/Program.cs). Start the API any other way and `/openapi/v1.json` is a 404 that
 *    looks like a routing bug.
 * 2. Do NOT round-trip the document through a JSON parser before writing it. `JSON.stringify` will
 *    reformat the whole 106 KB file — different indentation, different key escaping, a trailing
 *    newline where there is none — and produce a diff in which a real change is invisible. This
 *    script writes the served text through unchanged apart from line endings, which is why `regen`
 *    on an up-to-date tree is a no-op rather than a reformat.
 * 3. LINE ENDINGS, and this one produced a false measurement before it was understood. The API
 *    serves LF. A Windows checkout with `core.autocrlf=true` holds the same document as CRLF — on
 *    2026-08-13 that was 109,944 bytes on disk against 106,506 served, with 3,438 line endings
 *    differing and not one byte of content. A raw comparison therefore reports drift on a tree that
 *    has none, and a text-mode read in a language that silently normalises newlines (Python does,
 *    Node does not) reports a match that is not a byte match either. Both sides are normalised to LF
 *    here before comparing, and `regen` writes LF, which is what git stores.
 *
 * WHAT `check` DOES THAT A TEXT DIFF DOES NOT
 * A byte comparison tells you THAT the spec moved. When it has, this prints a SEMANTIC report —
 * paths, operations and responses added or removed, and descriptions changed — because the failure
 * worth catching is a regeneration that silently drops a hand-written response description. That is
 * a real thing that happened, and a 3,000-line textual diff hides it perfectly.
 *
 * NOT wired into CI here, deliberately. Closing that loop needs a running API in the pipeline and is
 * tracked as its own decision in the working repo's backlog; this script is what such a job would
 * call, and it is useful on its own long before that.
 */

import { readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const REPO_ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const SPEC_PATH = join(REPO_ROOT, 'docs', 'api', 'openapiv1.json');

/**
 * Plain HTTP on the port CI's real-stack job uses. Override with OPENAPI_BASE_URL.
 *
 * Trailing slashes are stripped: `http://host:5068/` would otherwise concatenate to
 * `http://host:5068//openapi/v1.json`, and a doubled slash is not the route MapOpenApi registered —
 * so the script would report a 404 and blame the Development gate for a typo in an env var.
 */
const BASE_URL = (process.env.OPENAPI_BASE_URL ?? 'http://localhost:5068').replace(/\/+$/, '');
const SPEC_URL = `${BASE_URL}/openapi/v1.json`;

const HOW_TO_START = `
  Start the API first — the document is only mapped in Development:

    cd backend/src/AzureBank.Api
    ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=${BASE_URL} dotnet run --no-launch-profile

  Then wait for /health/ready to answer 200 (about six seconds on a warm build).`;

async function fetchServedSpec() {
  let response;
  try {
    response = await fetch(SPEC_URL);
  } catch (cause) {
    throw new Error(`Could not reach ${SPEC_URL} — ${cause.message}\n${HOW_TO_START}`);
  }

  if (response.status === 404) {
    throw new Error(
      `${SPEC_URL} answered 404. The API is running but NOT in Development, so MapOpenApi() was ` +
        `never registered.\n${HOW_TO_START}`,
    );
  }
  if (!response.ok) {
    throw new Error(`${SPEC_URL} answered ${response.status} ${response.statusText}.`);
  }

  // Bytes, then one explicit UTF-8 decode. Never JSON.parse -> JSON.stringify: see the header.
  return new TextDecoder('utf-8', { fatal: true }).decode(await response.arrayBuffer());
}

/** LF, so a CRLF working copy is not reported as drift. Trap 3 in the header. */
const toLf = (text) => text.split('\r\n').join('\n');

/**
 * Every response description in the document, keyed so a move is not mistaken for a rewrite.
 * Shape: "GET /api/accounts 200" -> "The caller's accounts."
 */
function describeResponses(document) {
  const described = new Map();
  for (const [path, operations] of Object.entries(document.paths ?? {})) {
    for (const [method, operation] of Object.entries(operations ?? {})) {
      if (typeof operation !== 'object' || operation === null) continue;
      for (const [status, response] of Object.entries(operation.responses ?? {})) {
        described.set(`${method.toUpperCase()} ${path} ${status}`, response?.description ?? '');
      }
    }
  }
  return described;
}

/** Added, removed and reworded responses — the report a textual diff cannot give you. */
function semanticReport(committedText, servedText) {
  let committed;
  let served;
  try {
    committed = JSON.parse(committedText);
    served = JSON.parse(servedText);
  } catch (cause) {
    return [`  (could not parse both documents for a semantic diff: ${cause.message})`];
  }

  const before = describeResponses(committed);
  const after = describeResponses(served);
  const lines = [];

  for (const key of after.keys()) {
    if (!before.has(key)) lines.push(`  + ADDED    ${key}`);
  }
  for (const [key, description] of before) {
    if (!after.has(key)) {
      lines.push(`  - REMOVED  ${key}  ("${description}")`);
    } else if (after.get(key) !== description) {
      lines.push(
        `  ~ CHANGED  ${key}\n      was: "${description}"\n      now: "${after.get(key)}"`,
      );
    }
  }

  if (lines.length === 0) {
    lines.push(
      '  No path, operation, status or description changed — the difference is elsewhere in the',
      '  document (a schema, an example, or serialisation). Diff the file to see it.',
    );
  }
  return lines;
}

async function main(command) {
  if (command !== 'check' && command !== 'regen') {
    console.error('Usage: node scripts/openapi-spec.mjs <check|regen>');
    return 2;
  }

  const served = toLf(await fetchServedSpec());
  const committed = toLf(await readFile(SPEC_PATH, 'utf-8'));

  if (served === committed) {
    console.log(`docs/api/openapiv1.json matches ${SPEC_URL} (${served.length} chars, LF-normalised).`);
    return 0;
  }

  if (command === 'regen') {
    await writeFile(SPEC_PATH, served, 'utf-8');
    console.log('Rewrote docs/api/openapiv1.json from the running API. What moved:');
    console.log(semanticReport(committed, served).join('\n'));
    console.log(
      '\nNow regenerate the frontend artifacts, or CI will fail on the drift gate:\n' +
        '  cd frontend && npm run generate:api && npm run generate:zod',
    );
    return 0;
  }

  console.error(`docs/api/openapiv1.json does NOT match what ${SPEC_URL} serves.`);
  console.error(semanticReport(committed, served).join('\n'));
  console.error('\nRegenerate with: node scripts/openapi-spec.mjs regen');
  return 1;
}

/*
  The message, never a stack — every throw above is a diagnosis written for a human, and burying it
  under ten frames of node internals is how a script that explains itself stops being read.

  And `process.exitCode`, never `process.exit()`. Measured on Windows: calling `process.exit()` while
  stdout still holds buffered output aborts libuv with
  "Assertion failed: !(handle->flags & UV_HANDLE_CLOSING), file src/win/async.c" and exits 127 — so a
  passing check reported failure and a failing one reported the wrong code. Setting the code and
  letting the process end on its own flushes first.
*/
process.exitCode = await main(process.argv[2]).catch((failure) => {
  console.error(failure.message);
  return 1;
});
