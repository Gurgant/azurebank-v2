/*
  jsdom runs `requestAnimationFrame` on a Node `setInterval`, which it starts on the first frame
  request made while none is pending (jsdom/lib/jsdom/browser/Window.js). Started while the clock
  is fake, that interval dies when the clock is restored, and every later frame on that page waits
  for ever — and RTK delivers a store change to its subscribers on the next frame
  (autoBatchEnhancer's default), so a page stops seeing its own cache. Measured: a test that edited
  the accounts cache passed alone and failed after a fake-clock test in the same file, and a bare
  frame request there never resolved.

  The page outlives the file when Vitest runs without isolation (`--no-isolate`): every file a
  worker runs after that one shares it. Measured: a file that asked for a frame under a fake clock
  and restored it left the next file in the same worker with no frame within a second, while the
  same two files with isolation, a page each, both passed.

  So frame requests are kept pending at all times, made through jsdom's own function while the
  clock is real, and the interval is never stopped for the life of the page. TWO chains, not one:
  jsdom drops a callback — and with the last one, the interval — before it runs it, so a lone
  chain that renews itself starts a new interval on whatever clock is installed at that moment.
  The mark that they run is kept on the page rather than in this module, because a file that
  empties the module registry hands the next file a new copy of this module and the same page.
*/
const FRAMES_KEPT_REAL: unique symbol = Symbol.for('azurebank.test.framesKeptReal');

export function keepAnimationFramesOnTheRealClock(): void {
  const page = window as Window & { [FRAMES_KEPT_REAL]?: true };
  if (page[FRAMES_KEPT_REAL]) return;
  page[FRAMES_KEPT_REAL] = true;
  const jsdomAnimationFrame = window.requestAnimationFrame.bind(window);
  const chain = () => void jsdomAnimationFrame(chain);
  chain();
  chain();
}
