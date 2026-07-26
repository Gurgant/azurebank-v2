/**
 * Render the whole icon set from public/logo.svg.
 *
 * logo.svg is the ONE master: a tight viewBox around the mark and nothing else. Everything here is
 * derived from it, favicon.svg included — a square, centred, padded copy. Keeping the padded
 * version as a second hand-maintained file would mean two geometries to keep in step, and they
 * would drift the first time only one of them was edited.
 *
 * Every size is rasterised from the vector at its native resolution. Downscaling one large PNG is
 * the usual shortcut and it is why small favicons look muddy: at 16px the rasteriser needs the
 * geometry in hand to decide where the antialiasing goes. At 512 it makes no visible difference,
 * which is exactly why the shortcut survives unnoticed.
 *
 *   npm install --no-save @resvg/resvg-js   (not a dependency — see docs/brand-assets.md)
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
      'for a script nobody runs. Install it when you need it:\n\n' +
      '  npm install --no-save @resvg/resvg-js\n',
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
const inner = master.replace(/^[\s\S]*?<svg[^>]*>/, '').replace(/<\/svg>\s*$/, '');

/**
 * Place the mark on a square canvas at `coverage` of its width, centred, over `bg`.
 *
 * The master is wrapped in an outer <svg> rather than having its coordinates rewritten, so one
 * geometry serves every box and there is no arithmetic to get wrong per size.
 */
function compose(size, coverage, bg) {
  const scale = (size * coverage) / VW;
  const w = VW * scale;
  const h = VH * scale;
  const doc =
    `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="0 0 ${size} ${size}">` +
    (bg ? `<rect width="${size}" height="${size}" fill="${bg}"/>` : '') +
    `<svg x="${(size - w) / 2}" y="${(size - h) / 2}" width="${w}" height="${h}" viewBox="${VX} ${VY} ${VW} ${VH}">${inner}</svg>` +
    `</svg>`;

  const png = new Resvg(doc, {
    fitTo: { mode: 'width', value: size },
    background: bg ? undefined : 'rgba(0,0,0,0)',
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

// Coverage differs by role, deliberately.
//   maskable — must survive the circular crop, so the mark stays inside the safe zone
//   apple    — iOS applies its own rounded-rect mask and composites on black, so: opaque, padded
//   favicon  — never cropped, and at 16px every pixel of mark is worth having
const TARGETS = [
  { file: 'favicon-96x96.png', size: 96, coverage: 0.86, bg: null },
  { file: 'apple-touch-icon.png', size: 180, coverage: 0.72, bg: '#ffffff' },
  { file: 'web-app-manifest-192x192.png', size: 192, coverage: 0.6, bg: '#ffffff', maskable: true },
  { file: 'web-app-manifest-512x512.png', size: 512, coverage: 0.6, bg: '#ffffff', maskable: true },
];
const ICO_SIZES = [16, 32, 48];
const ICO_COVERAGE = 0.94;

// favicon.svg is logo.svg on a square canvas, centred, with 8% breathing room. Browsers scale an
// SVG favicon to a square slot, so a 4:3 mark handed over untouched would be letterboxed by the
// browser with padding it chose rather than padding we chose.
{
  const n = (v) => v.toFixed(2).replace(/\.?0+$/, '');
  const pad = Math.max(VW, VH) * 0.08;
  const box = Math.max(VW, VH) + 2 * pad;
  const doc =
    `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${n(box)} ${n(box)}" role="img" aria-label="AzureBank">` +
    `<title>AzureBank</title>` +
    `<svg x="${n((box - VW) / 2)}" y="${n((box - VH) / 2)}" width="${n(VW)}" height="${n(VH)}" viewBox="${n(VX)} ${n(VY)} ${n(VW)} ${n(VH)}">${inner}</svg>` +
    `</svg>\n`;
  writeFileSync(join(PUBLIC, 'favicon.svg'), doc);
  console.log(
    `${'favicon.svg'.padEnd(30)}       square  viewBox 0 0 ${n(box)} ${n(box)}  ${doc.length.toLocaleString()} B`,
  );
}

for (const t of TARGETS) {
  const { png, w, h } = compose(t.size, t.coverage, t.bg);
  writeFileSync(join(PUBLIC, t.file), png);
  const note = t.maskable
    ? (({ halfDiag, safe }) => `  safe-zone ${halfDiag.toFixed(0)}/${safe.toFixed(0)} ok`)(
        assertMaskable(t.size, w, h),
      )
    : '';
  console.log(
    `${t.file.padEnd(30)} ${String(t.size).padStart(3)}px  mark ${w.toFixed(0)}x${h.toFixed(0)}  ${png.length.toLocaleString()} B${note}`,
  );
}

const frames = ICO_SIZES.map((size) => ({ size, png: compose(size, ICO_COVERAGE, null).png }));
const ico = encodeIco(frames);
writeFileSync(join(PUBLIC, 'favicon.ico'), ico);
console.log(
  `${'favicon.ico'.padEnd(30)}       ${ICO_SIZES.join('/')}  ${ico.length.toLocaleString()} B`,
);
