/**
 * Publishes the pictures the README uses, from what `npm run capture:screenshots` left in
 * screenshots/output/: resized, compressed, and copied into docs/images/, where they are committed.
 * Everything else in output/ stays there, one command away from being taken again.
 *
 * Needs ffmpeg (resizing, and the GIF) and pngquant (compression) on the PATH. It stops at the first
 * tool that is missing or fails, and prints each file it wrote with its size, against the budget the
 * README's images are held to.
 */
import { spawnSync } from 'node:child_process';
import { mkdirSync, rmSync, statSync } from 'node:fs';
import { join } from 'node:path';

const OUT = 'screenshots/output';
const DEST = '../docs/images';
const KB = 1024;

function run(tool: string, args: string[], okCodes: number[] = [0]): number {
  const result = spawnSync(tool, args, { encoding: 'utf8' });
  if (result.error) {
    throw new Error(`${tool} could not be started — is it on the PATH? (${result.error.message})`);
  }
  if (!okCodes.includes(result.status ?? -1)) {
    throw new Error(`${tool} ${args.join(' ')} exited ${result.status}: ${result.stderr}`);
  }
  return result.status ?? 0;
}

/**
 * A screen, `width` pixels wide, compressed. Downscaled with Lanczos from the 2x capture (a README
 * shows about 800px, a retina screen wants twice that), then quantised by pngquant. pngquant exits
 * 99 when it cannot reach the quality floor; the file is then kept unquantised rather than degraded.
 */
function publishPng(source: string, target: string, width: number) {
  const scaled = join(OUT, `.scaled-${target}`);
  run('ffmpeg', [
    '-y',
    '-v',
    'error',
    '-i',
    join(OUT, source),
    '-vf',
    `scale=${width}:-1:flags=lanczos`,
    scaled,
  ]);
  const code = run(
    'pngquant',
    [
      '--quality=80-95',
      '--speed',
      '1',
      '--strip',
      '--force',
      '--output',
      join(DEST, target),
      scaled,
    ],
    [0, 99],
  );
  if (code === 99) {
    run('ffmpeg', ['-y', '-v', 'error', '-i', scaled, join(DEST, target)]);
  }
  rmSync(scaled);
}

/** The recorded transfer as a GIF: 800px, 12 frames a second, one palette for the whole clip. */
function publishGif(source: string, target: string) {
  const filter =
    'fps=12,scale=800:-1:flags=lanczos,split[a][b];' +
    '[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle';
  run('ffmpeg', [
    '-y',
    '-v',
    'error',
    '-i',
    join(OUT, source),
    '-vf',
    filter,
    '-loop',
    '0',
    join(DEST, target),
  ]);
}

mkdirSync(DEST, { recursive: true });

// What the README and the repository settings use. The budgets are the README's own (at most about
// 400 KB a screen, a few MB for the animation) and GitHub's for the social preview (under 1 MB).
const published: Array<[target: string, budget: number]> = [];
for (const theme of ['light', 'dark']) {
  publishPng(`dashboard-desktop-${theme}.png`, `dashboard-${theme}.png`, 1600);
  published.push([`dashboard-${theme}.png`, 400 * KB]);
}
publishGif('transfer-flow.webm', 'transfer-flow.gif');
published.push(['transfer-flow.gif', 5 * KB * KB]);
for (const card of ['social-preview', 'linkedin-card']) {
  publishPng(`${card}.png`, `${card}.png`, card === 'social-preview' ? 1280 : 1200);
  published.push([`${card}.png`, 1 * KB * KB]);
}

let over = 0;
for (const [target, budget] of published) {
  const size = statSync(join(DEST, target)).size;
  const verdict = size <= budget ? 'ok' : 'OVER BUDGET';
  if (size > budget) over++;
  console.log(
    `${target.padEnd(24)} ${(size / KB).toFixed(0).padStart(6)} KB  (budget ${(budget / KB).toFixed(0)} KB)  ${verdict}`,
  );
}
if (over > 0) {
  console.error(
    `${over} file(s) over budget: shorten the recording or lower the quality before committing.`,
  );
  process.exit(1);
}
