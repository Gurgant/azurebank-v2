/**
 * Render the whole icon set from public/logo.svg.
 *
 * logo.svg is the ONE master: a tight viewBox around the mark and nothing else. Everything here is
 * derived from it, favicon.svg included — a square, centred copy on a white plate. Keeping the
 * plated version as a second hand-maintained file would mean two geometries to keep in step, and
 * they would drift the first time only one of them was edited.
 *
 * Every size is rasterised from the vector at its native resolution. Downscaling one large PNG is
 * the usual shortcut and it is why small favicons look muddy: at 16px the rasteriser needs the
 * geometry in hand to decide where the antialiasing goes. At 512 it makes no visible difference,
 * which is exactly why the shortcut survives unnoticed.
 *
 *   npm install --no-save @resvg/resvg-js@2.6.2   (pinned, and not a dependency — see docs/brand-assets.md)
 *   npm run generate:icons
 */
import { readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const PUBLIC = join(dirname(fileURLToPath(import.meta.url)), '..', 'public');

let Resvg;
try {
  ({ Resvg } = await import('@resvg/resvg-js'));
} catch {
  console.error(
    'This script needs @resvg/resvg-js, which is not a permanent devDependency because it is a\n' +
      'native module and the icons change roughly never — carrying it would tax every CI install\n' +
      'for a script nobody runs. Install it when you need it — and keep the pin: the committed\n' +
      'icons are artifacts of one rasteriser, so a newer release would rewrite every one of them\n' +
      'with no change to logo.svg to explain the diff.\n\n' +
      '  npm install --no-save @resvg/resvg-js@2.6.2\n',
  );
  process.exit(1);
}

const master = readFileSync(join(PUBLIC, 'logo.svg'), 'utf8');
const viewBox = /viewBox="([\d.\-\s]+)"/.exec(master);
if (!viewBox) throw new Error('logo.svg has no viewBox');
// The origin is carried through rather than assumed to be "0 0". It is zero in the master today,
// so this changes no output — but a master round-tripped through a vector editor can come back with
// a shifted origin, and hardcoding "0 0" would then place the mark off-centre in every icon at once
// with nothing to indicate why.
const [VX, VY, VW, VH] = viewBox[1].trim().split(/\s+/).map(Number);
// The master's own <title> is dropped on the way in. Every document built below supplies its own
// at the root, and carrying this one through nests a second <title> inside the inner <svg> — inert
// behind an <img>, but wrong the moment anyone inlines the file or points <use> at it, and the kind
// of thing an SVG sanitiser resolves by picking the wrong one.
const inner = master
  .replace(/^[\s\S]*?<svg[^>]*>/, '')
  .replace(/<\/svg>\s*$/, '')
  .replace(/<title>[\s\S]*?<\/title>/, '');

/**
 * Place the mark on a square canvas at `coverage` of its width, centred, over `bg`.
 *
 * The master is wrapped in an outer <svg> rather than having its coordinates rewritten, so one
 * geometry serves every box and there is no arithmetic to get wrong per size.
 */
function compose(size, coverage, bg, radiusRatio = 0) {
  const scale = (size * coverage) / VW;
  const w = VW * scale;
  const h = VH * scale;
  const r = size * radiusRatio;
  const doc =
    `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="0 0 ${size} ${size}">` +
    (bg ? `<rect width="${size}" height="${size}" rx="${r}" ry="${r}" fill="${bg}"/>` : '') +
    `<svg x="${(size - w) / 2}" y="${(size - h) / 2}" width="${w}" height="${h}" viewBox="${VX} ${VY} ${VW} ${VH}">${inner}</svg>` +
    `</svg>`;

  // Always transparent, even when a plate is drawn. The plate is a rect inside the document, and a
  // rounded one leaves its four corners uncovered — a canvas background would fill them back in and
  // silently square off every rounded icon in the set.
  const png = new Resvg(doc, {
    fitTo: { mode: 'width', value: size },
    background: 'rgba(0,0,0,0)',
  })
    .render()
    .asPng();
  return { png, w, h };
}

/**
 * Android crops a maskable icon to a circle of radius 40%. What has to fit is therefore the
 * bounding box's half-diagonal, not its width — a wide mark can pass a width check and still lose
 * its corners. Asserted rather than assumed, because the failure only shows on a real device.
 */
function assertMaskable(size, w, h) {
  const halfDiag = Math.hypot(w / 2, h / 2);
  const safe = 0.4 * size;
  if (halfDiag > safe) {
    throw new Error(
      `maskable ${size}px: half-diagonal ${halfDiag.toFixed(1)} exceeds safe radius ${safe.toFixed(1)}`,
    );
  }
  return { halfDiag, safe };
}

/**
 * Write a multi-image ICO. PNG payloads are legal in ICO from Windows Vista onward, so each frame
 * goes in as the natively-rendered PNG rather than being re-encoded to a BMP.
 *
 * Frames must be built at their own size beforehand: an ICO assembled by resizing one image is
 * precisely the muddy result this whole script exists to avoid.
 */
function encodeIco(frames) {
  const header = Buffer.alloc(6);
  header.writeUInt16LE(0, 0); // reserved
  header.writeUInt16LE(1, 2); // type: icon
  header.writeUInt16LE(frames.length, 4);

  const dir = Buffer.alloc(16 * frames.length);
  let offset = header.length + dir.length;
  frames.forEach((f, i) => {
    const e = i * 16;
    dir.writeUInt8(f.size >= 256 ? 0 : f.size, e + 0); // 0 encodes 256
    dir.writeUInt8(f.size >= 256 ? 0 : f.size, e + 1);
    dir.writeUInt8(0, e + 2); // palette size: not paletted
    dir.writeUInt8(0, e + 3); // reserved
    dir.writeUInt16LE(1, e + 4); // colour planes
    dir.writeUInt16LE(32, e + 6); // bits per pixel
    dir.writeUInt32LE(f.png.length, e + 8);
    dir.writeUInt32LE(offset, e + 12);
    offset += f.png.length;
  });

  return Buffer.concat([header, dir, ...frames.map((f) => f.png)]);
}

/**
 * The favicon sits on a white plate, and that was measured rather than assumed.
 *
 * Four candidates were rendered at 16/20/32/48px over five tab-strip colours sampled from real
 * Chrome — transparent, white plate, brand plate with a white mark, brand plate keeping the cyan
 * arrow — and scored by the alpha-weighted share of the mark's ink falling below 3:1 against
 * whatever sits behind it. The transparent mark loses **100 %** of its ink on a saturated blue
 * strip: it does not degrade, it vanishes, because the mark's dark blue and the strip are the same
 * colour. A plate makes legibility independent of the strip, and that is the entire argument for
 * having one.
 *
 * White rather than brand, decided against the contrast numbers: a brand plate scores roughly twice
 * as well (33 % versus 66 %), but only by turning the mark into a white silhouette — a different
 * logo. The white plate keeps the mark's own two colours, and the cyan it is scored against sits at
 * 2.32:1 on white, which is the same cyan this app already renders on every white surface it has.
 * Holding the favicon to a stricter standard than the product would have been incoherent.
 */
const PLATE = '#ffffff';
const PLATE_RADIUS = 0.18;

/**
 * The mark spans 90 % of the tile: a 5 % inset on each side.
 *
 * 14 % was the first attempt and left the mark visibly timid beside the unplated original — a plate
 * that eats 28 % of the tile gives back a smaller mark. 2 % left no white margin at all, so on a
 * dark strip the mark's edge met the dark directly and the plate stopped reading as a plate. 5 % is
 * where the mark has the presence of the original and a margin still survives.
 *
 * Note what the inset actually governs: the mark is 4:3, so a square plate always has generous room
 * above and below whatever this number is. It controls the left and right margins alone, and at
 * 16px a 5 % inset is 0.8px — sub-pixel. The plate reads as a plate there because of the vertical
 * margin, not the horizontal one.
 */
const FAVICON_COVERAGE = 0.9;

// Coverage differs by role, deliberately.
//   maskable — must survive the circular crop, so the mark stays inside the safe zone, and the
//              plate is square and full-bleed because Android supplies the mask
//   apple    — iOS applies its own rounded-rect mask and composites on black, so: opaque, square,
//              padded. Rounding it here would round it twice.
//   favicon  — never cropped by anyone, so the plate carries its own corners
const TARGETS = [
  {
    file: 'favicon-96x96.png',
    size: 96,
    coverage: FAVICON_COVERAGE,
    bg: PLATE,
    radius: PLATE_RADIUS,
  },
  { file: 'apple-touch-icon.png', size: 180, coverage: 0.72, bg: PLATE },
  { file: 'web-app-manifest-192x192.png', size: 192, coverage: 0.6, bg: PLATE, maskable: true },
  { file: 'web-app-manifest-512x512.png', size: 512, coverage: 0.6, bg: PLATE, maskable: true },
  // The `purpose: "any"` pair, and they have to be their own files rather than the maskable ones
  // relabelled. A maskable icon is drawn at 60% coverage so Android's circular crop cannot clip it;
  // rendered UNcropped that same image is a mark floating in a field of white, visibly smaller than
  // every other icon on the launcher. `any` means "use this as-is", so it gets the favicon geometry.
  {
    file: 'web-app-manifest-any-192x192.png',
    size: 192,
    coverage: FAVICON_COVERAGE,
    bg: PLATE,
    radius: PLATE_RADIUS,
  },
  {
    file: 'web-app-manifest-any-512x512.png',
    size: 512,
    coverage: FAVICON_COVERAGE,
    bg: PLATE,
    radius: PLATE_RADIUS,
  },
];

// 16/32/48 are what a browser asks for. 64 through 256 are what Windows asks for — Explorer's large
// and extra-large views, the desktop shortcut, the taskbar at high DPI — and without them Windows
// stretches the 48px frame and shows a soft one. Six frames cost about 16 KB, on a file no modern
// browser reaches for anyway: every one of them takes the SVG from the link tag first.
const ICO_SIZES = [16, 32, 48, 64, 128, 256];

// favicon.svg is logo.svg centred on a square white plate. Browsers scale an SVG favicon into a
// square slot, so a 4:3 mark handed over untouched is letterboxed by the browser with padding it
// chose rather than padding we chose — and on a themed tab strip that padding is the strip's own
// colour showing through the mark.
//
// This file is also the app icon: src/components/shared/Logo.tsx renders it directly. The tile in
// the tab and the tile in the sidebar being the same object is the point, not a coincidence, and
// pointing the component at this file rather than re-deriving the plate in CSS is what keeps them
// from drifting apart.
{
  const n = (v) => v.toFixed(2).replace(/\.?0+$/, '');
  const box = VW / FAVICON_COVERAGE;
  const r = box * PLATE_RADIUS;
  const doc =
    `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${n(box)} ${n(box)}" role="img" aria-label="AzureBank">` +
    `<title>AzureBank</title>` +
    `<rect width="${n(box)}" height="${n(box)}" rx="${n(r)}" ry="${n(r)}" fill="${PLATE}"/>` +
    `<svg x="${n((box - VW) / 2)}" y="${n((box - VH) / 2)}" width="${n(VW)}" height="${n(VH)}" viewBox="${n(VX)} ${n(VY)} ${n(VW)} ${n(VH)}">${inner}</svg>` +
    `</svg>\n`;
  writeFileSync(join(PUBLIC, 'favicon.svg'), doc);
  console.log(
    `${'favicon.svg'.padEnd(30)}       plate   viewBox 0 0 ${n(box)} ${n(box)}  ${doc.length.toLocaleString()} B`,
  );
}

for (const t of TARGETS) {
  // A maskable icon hands its corners to Android's crop, so it must be square and opaque: a radius
  // leaves transparent corners under the circular mask, and no plate leaves the whole tile
  // transparent. Both fail silently on a device and nowhere else — the same class of defect
  // assertMaskable already exists to catch, so it is guarded the same way.
  if (t.maskable && (t.radius || !t.bg)) {
    throw new Error(`maskable ${t.file}: needs a square opaque plate (radius=${t.radius}, bg=${t.bg})`);
  }

  const { png, w, h } = compose(t.size, t.coverage, t.bg, t.radius ?? 0);

  // Assert BEFORE writing. Asserting afterwards leaves a half-updated public/ on failure — some
  // icons new, some old, and a non-zero exit that looks like nothing happened.
  const note = t.maskable
    ? (({ halfDiag, safe }) => `  safe-zone ${halfDiag.toFixed(0)}/${safe.toFixed(0)} ok`)(
        assertMaskable(t.size, w, h),
      )
    : '';

  writeFileSync(join(PUBLIC, t.file), png);
  console.log(
    `${t.file.padEnd(34)} ${String(t.size).padStart(3)}px  mark ${w.toFixed(0)}x${h.toFixed(0)}  ${png.length.toLocaleString()} B${note}`,
  );
}

const frames = ICO_SIZES.map((size) => ({
  size,
  png: compose(size, FAVICON_COVERAGE, PLATE, PLATE_RADIUS).png,
}));
const ico = encodeIco(frames);
writeFileSync(join(PUBLIC, 'favicon.ico'), ico);
console.log(
  `${'favicon.ico'.padEnd(30)}       ${ICO_SIZES.join('/')}  ${ico.length.toLocaleString()} B`,
);
