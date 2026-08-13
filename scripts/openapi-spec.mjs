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
 * THE THREE TRAPS, each of which cost time before it was written down.
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
 * A byte comparison tells you THAT the spec moved. When it has, this prints a SEMANTIC report of
 * every piece of hand-written prose: operation summaries, operation descriptions and response
 * descriptions, each added, removed or reworded — and a path or operation appearing or disappearing
 * shows up as all of its entries doing so. The failure worth catching is a regeneration that
 * silently drops something a human wrote, and a 3,000-line textual diff hides that perfectly.
 *
 * What it does NOT compare: schemas, parameters, examples, tags, security. A change confined to
 * those is reported as "the difference is elsewhere" rather than pretended away — the scope is
 * stated in the fallback message so the report never claims more than it checked.
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
  const text = new TextDecoder('utf-8', { fatal: true }).decode(await response.arrayBuffer());

  /*
    Parsed only to VALIDATE, never to re-serialise — the text returned below is the served bytes,
    which is what trap 2 in the header is about.

    Without this, any 200 carrying something other than an OpenAPI document reaches `regen`, which
    overwrites the committed contract with it and exits 0. That is not far-fetched: point
    OPENAPI_BASE_URL at a dev server, a proxy, or a captive portal and an HTML error page arrives
    with a 200 on it. A command whose whole job is to write a committed artefact has to know what it
    was handed.
  */
  let document;
  try {
    document = JSON.parse(text);
  } catch (cause) {
    throw new Error(
      `${SPEC_URL} answered 200 but the body is not JSON (${cause.message}).\n` +
        `  It starts: ${JSON.stringify(text.slice(0, 120))}\n` +
        `  Something other than the API is probably answering on that address.`,
    );
  }
  if (document === null || typeof document !== 'object' || typeof document.openapi !== 'string') {
    throw new Error(
      `${SPEC_URL} returned JSON with no "openapi" version field, so it is not an OpenAPI document.\n` +
        `  Top-level keys: ${JSON.stringify(Object.keys(document ?? {}).slice(0, 10))}`,
    );
  }
  return text;
}

/** LF, so a CRLF working copy is not reported as drift. Trap 3 in the header. */
const toLf = (text) => text.split('\r\n').join('\n');

/**
 * Every piece of hand-written prose in the document, keyed so a move is not mistaken for a rewrite.
 *
 * Three kinds, and the first two were missing until a review asked whether the report covered what
 * its documentation claimed. It did not, and the gap was not hypothetical: this spec carries a
 * summary on ALL 24 of its operations, none of which were compared — so a regeneration could have
 * dropped every one of them while `check` printed "nothing changed".
 *
 *   "GET /api/accounts [summary]"     -> "List accounts"
 *   "GET /api/accounts [description]" -> "Returns every account the caller owns."
 *   "GET /api/accounts 200"           -> "List of user's accounts"
 *
 * A path or an operation added or removed shows up here too, as the appearance or disappearance of
 * all of its keys — which is why the report does not need a separate notion of them.
 */
function describeDocument(document) {
  const described = new Map();
  for (const [path, operations] of Object.entries(document.paths ?? {})) {
    for (const [method, operation] of Object.entries(operations ?? {})) {
      if (typeof operation !== 'object' || operation === null) continue;
      const where = `${method.toUpperCase()} ${path}`;

      if (typeof operation.summary === 'string') described.set(`${where} [summary]`, operation.summary);
      if (typeof operation.description === 'string') {
        described.set(`${where} [description]`, operation.description);
      }
      for (const [status, response] of Object.entries(operation.responses ?? {})) {
        described.set(`${where} ${status}`, response?.description ?? '');
      }
    }
  }
  return described;
}

/** Added, removed and reworded prose — the report a textual diff cannot give you. */
function semanticReport(committedText, servedText) {
  let committed;
  let served;
  try {
    committed = JSON.parse(committedText);
    served = JSON.parse(servedText);
  } catch (cause) {
    return [`  (could not parse both documents for a semantic diff: ${cause.message})`];
  }

  const before = describeDocument(committed);
  const after = describeDocument(served);
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
      '  No operation summary, operation description or response description changed, and no path,',
      '  operation or response status was added or removed. The difference is elsewhere — a schema, a',
      '  parameter, an example, or serialisation. Diff the file to see it.',
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
