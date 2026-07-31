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

/**
 * Fixed here rather than read from the lock, and that is the difference between a check and a
 * formality: hashing with `lock.algorithm` would make the lock its own oracle, so a file that
 * recorded `md5` alongside md5 digests would verify perfectly against itself while the sha256
 * provenance this whole mechanism claims had quietly stopped being true.
 */
const HASH_ALGORITHM = 'sha256';

/**
 * What the generator MUST cover, stated independently of what it happens to have written.
 *
 * A second list is the point rather than a duplication to regret: if the generator loses a target,
 * the lock loses it too, and any assertion derived from the lock would keep passing over a file
 * nobody is checking any more.
 */
const EXPECTED = {
  master: ['public/logo.svg'],
  generator: ['scripts/generate-icons.js'],
  generated: [
    'public/favicon.svg',
    'public/favicon.ico',
    'public/favicon-96x96.png',
    'public/apple-touch-icon.png',
    'public/web-app-manifest-192x192.png',
    'public/web-app-manifest-512x512.png',
    'public/web-app-manifest-any-192x192.png',
    'public/web-app-manifest-any-512x512.png',
  ],
} as const;

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
  return createHash(HASH_ALGORITHM).update(payload).digest('hex');
}

describe('the icon set still comes from the master', () => {
  it('is hashed with the algorithm the mechanism claims', () => {
    // Asserted separately BECAUSE the checker above no longer reads it: without this the field
    // could say anything at all and nothing would notice, which is a different way to be wrong
    // than the one the fixed constant prevents.
    expect(lock.algorithm).toBe(HASH_ALGORITHM);
  });

  it('covers exactly the files it is supposed to, by path', () => {
    // Counted by ROLE at first, and that was too weak to be a guard: eight `generated` entries with
    // one path repeated and another missing satisfies a count perfectly while an icon goes
    // unchecked. Paths, exactly once each, is the claim worth making.
    const byRole = (role: string) =>
      lock.files
        .filter((f) => f.role === role)
        .map((f) => f.path)
        .sort();

    expect(byRole('master')).toEqual([...EXPECTED.master].sort());
    expect(byRole('generator')).toEqual([...EXPECTED.generator].sort());
    expect(byRole('generated')).toEqual([...EXPECTED.generated].sort());
    expect(lock.files).toHaveLength(
      EXPECTED.master.length + EXPECTED.generator.length + EXPECTED.generated.length,
    );
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
