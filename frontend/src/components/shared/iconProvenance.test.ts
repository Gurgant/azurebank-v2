import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';

/**
 * The gate that could not be a CI step.
 *
 * `public/logo.svg` is the one hand-maintained file; eight icons are rasterised from it by
 * `scripts/generate-icons.js`. The obvious check — regenerate and diff — cannot run here, because
 * the rasteriser is deliberately NOT a dependency: `@resvg/resvg-js` is a native module and the
 * icons change roughly never, so carrying it would tax every install for a script nobody runs
 * (docs/brand-assets.md).
 *
 * So the generator records the answer instead. It hashes the master, itself and every artifact at
 * the moment it runs — where the rasteriser is present — into `scripts/icons.lock.json`, and this
 * recomputes those hashes with nothing installed.
 *
 * What that catches is the drift that actually happens, and none of it is visible in review: the
 * master edited with the icons left stale, an icon hand-edited, or the generator changed without a
 * regeneration. A PNG diff is a wall of binary and `logo.svg` is a single line.
 *
 * The baseline was verified rather than assumed. Before the lock was written, the pinned rasteriser
 * was installed and the whole set regenerated: all eight files came back BYTE-IDENTICAL to what was
 * committed, so these hashes record a state that reproduces, not merely the state that happened to
 * be on disk.
 */

const LOCK_PATH = 'scripts/icons.lock.json';

interface LockEntry {
  path: string;
  role: 'master' | 'generator' | 'generated';
  mode: 'text' | 'binary';
  sha256: string;
}

interface Lock {
  rasteriser: string;
  algorithm: string;
  files: LockEntry[];
}

const lock = JSON.parse(readFileSync(LOCK_PATH, 'utf8')) as Lock;

/**
 * The same rule the generator used, keyed off the entry's OWN recorded mode.
 *
 * Recording the mode per file rather than inferring it from the extension is what stops the two
 * sides disagreeing: neither has to re-derive the decision, so neither can re-derive it differently.
 *
 * Text is stripped of carriage returns because `core.autocrlf` is on in this repo — a fresh Windows
 * clone gets CRLF where CI gets LF, and hashing raw bytes would fail on one of them for a reason
 * that has nothing to do with the icons.
 */
function hashOf(entry: LockEntry): string {
  const bytes = readFileSync(entry.path);
  const payload =
    entry.mode === 'text' ? Buffer.from(bytes.toString('utf8').replace(/\r/g, '')) : bytes;
  return createHash(lock.algorithm).update(payload).digest('hex');
}

describe('the icon set still comes from the master', () => {
  it('covers the master, the generator and every artifact', () => {
    // Non-vacuity, and a specific one: a lock that had lost its `generated` entries would let every
    // icon rot while the file still parsed and the loop below still passed.
    const roles = lock.files.reduce<Record<string, number>>((acc, f) => {
      acc[f.role] = (acc[f.role] ?? 0) + 1;
      return acc;
    }, {});

    expect(roles).toEqual({ master: 1, generator: 1, generated: 8 });
  });

  it.each(lock.files.map((f) => [f.path, f] as const))('%s matches the lock', (_path, entry) => {
    // Named per file so a failure says WHICH one moved, and the message says what to do about it:
    // regeneration is a deliberate act that needs the pinned rasteriser installed.
    expect(hashOf(entry), `${entry.path} changed — run: npm run generate:icons`).toBe(entry.sha256);
  });

  it('pins the rasteriser the docs promise, in one place', () => {
    // The pin is the reason the artifacts are reproducible at all: a newer resvg would rewrite every
    // PNG with no change to `logo.svg` to explain the diff. It used to be written out three times —
    // the generator's header, its error message, and the docs — and a pin stated three times is one
    // that can be bumped in two of them.
    const docs = readFileSync('../docs/brand-assets.md', 'utf8');
    const [pkg, version] = lock.rasteriser.split(/@(?=[\d])/);

    expect(pkg).toBe('@resvg/resvg-js');
    expect(docs).toContain(`npm install --no-save ${pkg}@${version}`);
  });
});
