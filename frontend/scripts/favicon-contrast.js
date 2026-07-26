/**
 * Re-measure the favicon comparison that docs/brand-assets.md publishes as a table.
 *
 * The table decides which favicon this project ships, and a table nobody can re-run is an opinion
 * with numbers on it. This script IS the measurement: same master, same rasteriser and same pin as
 * generate-icons.js, so a reviewer can disagree with the conclusion on evidence rather than on
 * whether the evidence exists.
 *
 *   npm install --no-save @resvg/resvg-js@2.6.2
 *   node scripts/favicon-contrast.js
 *
 * The metric: of the mark's ink, weighted by alpha, what share falls below 3:1 against whatever sits
 * immediately behind it — the plate for a plated candidate, the tab strip for a transparent one.
 * Partial alpha counts twice over, deliberately. A half-transparent edge pixel contributes half the
 * weight AND lands halfway to its backdrop, so a mark held together by soft edges is penalised
 * exactly as hard as it deserves. 3:1 is WCAG 2.2's non-text contrast threshold.
 */
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const PUBLIC = join(dirname(fileURLToPath(import.meta.url)), '..', 'public');

let Resvg;
try {
  ({ Resvg } = await import('@resvg/resvg-js'));
} catch {
  console.error('Needs the rasteriser:\n\n  npm install --no-save @resvg/resvg-js@2.6.2\n');
  process.exit(1);
}

const master = readFileSync(join(PUBLIC, 'logo.svg'), 'utf8');
const [VX, VY, VW, VH] = /viewBox="([\d.\-\s]+)"/
  .exec(master)[1]
  .trim()
  .split(/\s+/)
  .map(Number);
const inner = master
  .replace(/^[\s\S]*?<svg[^>]*>/, '')
  .replace(/<\/svg>\s*$/, '')
  .replace(/<title>[\s\S]*?<\/title>/, '');

/**
 * Tab-strip colours sampled out of Chrome's own icon cache and window, not invented. The saturated
 * blue is the case that started this: a themed strip whose colour is the mark's own dark blue.
 */
const STRIPS = [
  ['white strip', '#FFFFFF'],
  ['light blue', '#D2E5F4'],
  ['saturated blue', '#4DA6E8'],
  ['dark', '#1F2020'],
  ['dark blue', '#0F222D'],
];

const WHITE = '#FFFFFF';
const BRAND = '#0077B6';
const CYAN = '#39B8DB';
const ARROW_PARTS = new Set([0, 4]); // paths: 0 arrow, 1 A apex, 2 A right leg, 3 A left leg, 4 swoosh

const paths = [...master.matchAll(/<path fill="(#[0-9a-fA-F]{6})" d="/g)].map((m) => m[1]);

/** Repaint the master's five paths, or keep them. */
function recolour(colours) {
  if (!colours) return inner;
  let i = -1;
  return inner.replace(/fill="#[0-9a-fA-F]{6}"/g, () => `fill="${colours[++i]}"`);
}

/** One candidate at one size: the mark alone, so the returned alpha IS the ink. */
function renderInk(colours, coverage, size) {
  const w = size * coverage;
  const h = (VH / VW) * w;
  const doc =
    `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="0 0 ${size} ${size}">` +
    `<svg x="${(size - w) / 2}" y="${(size - h) / 2}" width="${w}" height="${h}" viewBox="${VX} ${VY} ${VW} ${VH}">${recolour(colours)}</svg>` +
    `</svg>`;
  const img = new Resvg(doc, {
    fitTo: { mode: 'width', value: size },
    background: 'rgba(0,0,0,0)',
  }).render();
  return img.pixels;
}

const rgb = (hex) => [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16));

function luminance([r, g, b]) {
  const f = (c) => {
    c /= 255;
    return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  };
  return 0.2126 * f(r) + 0.7152 * f(g) + 0.0722 * f(b);
}

function contrast(a, b) {
  const [la, lb] = [luminance(a), luminance(b)];
  return (Math.max(la, lb) + 0.05) / (Math.min(la, lb) + 0.05);
}

/**
 * Share of ink, alpha-weighted, that falls below 3:1 against `backdrop`.
 *
 * `RenderedImage.pixels` is PREMULTIPLIED — verified, not assumed: a 50%-alpha `#ff0000` reads
 * `[128,0,0,128]` there and `[255,0,0,128]` through `asPng()`. That makes the source-over composite
 * simpler rather than harder, because the premultiplied channel is already `colour × alpha`:
 *
 *     straight:      c × α + backdrop × (1 − α)
 *     premultiplied: c     + backdrop × (1 − α)
 *
 * Multiplying by alpha again here would darken every soft edge and quietly change the answer, which
 * for this mark is most of the ink — the generative source's edges rise over about six pixels, so
 * at 16px very little of it is fully opaque.
 */
function weakPercent(pixels, backdrop) {
  let weak = 0;
  let total = 0;
  for (let i = 0; i < pixels.length; i += 4) {
    const a = pixels[i + 3];
    if (!a) continue;
    total += a;
    const clear = 1 - a / 255;
    const over = [0, 1, 2].map((c) => Math.round(pixels[i + c] + backdrop[c] * clear));
    if (contrast(over, backdrop) < 3) weak += a;
  }
  return total ? (100 * weak) / total : 0;
}

// The four candidates, at the coverage each was JUDGED at. The plated three were compared at 0.72
// (a 14% inset) — the inset itself was a separate decision taken afterwards, and settled at 5%.
// Reproducing the comparison means reproducing its parameters, not today's.
const JUDGED_COVERAGE = 0.72;
const CANDIDATES = [
  ['transparent (what shipped before)', null, null, 0.96],
  ['white plate', WHITE, null, JUDGED_COVERAGE],
  ['brand plate, white mark', BRAND, Array(5).fill(WHITE), JUDGED_COVERAGE],
  [
    'brand plate, cyan arrow',
    BRAND,
    Array.from({ length: 5 }, (_, i) => (ARROW_PARTS.has(i) ? CYAN : WHITE)),
    JUDGED_COVERAGE,
  ],
];

// 16 is what a tab strip asks for at 100% scaling and the size the choice was made on; 20, 32 and
// 48 are the same slot at 125%, 200%, and the Windows shortcut. All four are measured because the
// claim that 16 is the worst case is one a reader should be able to check rather than take.
const SIZES = [16, 20, 32, 48];
const DECIDING_SIZE = 16;

/** Every candidate × every strip × every size, computed once and read from twice. */
const matrix = new Map();
for (const [name, plate, colours, coverage] of CANDIDATES) {
  for (const size of SIZES) {
    const ink = renderInk(colours, coverage, size);
    for (const [strip, hex] of STRIPS) {
      // A plate makes the backdrop constant. That is the whole point of one, and it is why the
      // plated rows come out identical across every column.
      matrix.set(`${name}|${size}|${strip}`, weakPercent(ink, rgb(plate ?? hex)));
    }
  }
}

const pct = (v) => `${v.toFixed(0)}%`;
const plateRange = (plate) => {
  if (!plate) return '—';
  const v = STRIPS.map(([, hex]) => contrast(rgb(plate), rgb(hex)));
  return `${Math.min(...v).toFixed(2)} – ${Math.max(...v).toFixed(2)}`;
};

console.log(`master ${paths.length} paths · ink below 3:1, alpha-weighted\n`);
for (const size of SIZES) {
  console.log(
    `${String(size).padStart(3)}px`.padEnd(36) + STRIPS.map(([n]) => n.padStart(15)).join(''),
  );
  for (const [name, plate] of CANDIDATES) {
    const row = STRIPS.map(([s]) => pct(matrix.get(`${name}|${size}|${s}`)).padStart(15));
    console.log(`  ${name}`.padEnd(36) + row.join(''));
  }
  console.log('');
}

// What actually ships, which is the white plate at a 5% inset rather than the 14% it was judged at.
const shipped = weakPercent(renderInk(null, 0.9, DECIDING_SIZE), rgb(WHITE));
console.log(`shipped geometry (white plate, 5% inset, 16px): ${pct(shipped)} of ink below 3:1\n`);

// Does the tile separate from the strip at all? Distinct from the question above, which asks
// whether the mark separates from the tile.
console.log('plate vs strip'.padEnd(36) + STRIPS.map(([n]) => n.padStart(15)).join(''));
for (const [label, plate] of [
  ['white', WHITE],
  ['brand', BRAND],
]) {
  const row = STRIPS.map(([, hex]) => contrast(rgb(plate), rgb(hex)).toFixed(2).padStart(15));
  console.log(`  ${label}`.padEnd(36) + row.join(''));
}

// ---------------------------------------------------------------------------------------------
// The block below is what docs/brand-assets.md publishes, emitted rather than transcribed.
//
// A doc that says "re-run this" and then shows a table the run does not produce is worse than one
// that shows nothing: it borrows the authority of a measurement without accepting its discipline.
// The first version of that section did exactly this — four columns against the script's five, and
// a size claim the script never made. Emitting the markdown makes the drift impossible rather than
// merely discouraged.
// ---------------------------------------------------------------------------------------------
const emphasise = (name, strip, v) =>
  name.startsWith('transparent') && v >= 99.5 ? `**${pct(v)}**` : pct(v);

console.log('\n--- markdown for docs/brand-assets.md (paste verbatim) ---\n');
console.log(`| candidate | ${STRIPS.map(([n]) => n).join(' | ')} | plate vs strip |`);
console.log(`| --- |${' --- |'.repeat(STRIPS.length + 1)}`);
for (const [name, plate] of CANDIDATES) {
  const cells = STRIPS.map(([s]) => emphasise(name, s, matrix.get(`${name}|${DECIDING_SIZE}|${s}`)));
  console.log(`| ${name} | ${cells.join(' | ')} | ${plateRange(plate)} |`);
}

console.log(`\n| candidate | ${SIZES.map((s) => `${s}px`).join(' | ')} |`);
console.log(`| --- |${' --- |'.repeat(SIZES.length)}`);
for (const [name] of CANDIDATES) {
  // Worst strip at each size: the number that decides whether a candidate survives anywhere.
  const cells = SIZES.map((size) =>
    pct(Math.max(...STRIPS.map(([s]) => matrix.get(`${name}|${size}|${s}`)))),
  );
  console.log(`| ${name} | ${cells.join(' | ')} |`);
}
