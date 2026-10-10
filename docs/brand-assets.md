# Brand assets

Everything visual in this project comes from one file: `frontend/public/logo.svg`. It is the only
one maintained by hand: every icon, at every size, is generated from it, and one command rebuilds
them all.

## The mark

An "A" monogram with an upward arrow sweeping through it and a wave at the lower left, in two flat
colours: **`#0c6ea1`** dark blue and **`#39b8db`** cyan, the mark's own hexes, measured from the
artwork.

They are not the UI brand colour `#0077b6`, the interface accent, but they are the reason it was
picked. The mark's dark blue sits at hue **200.5°** and `#0077b6` at **200.8°**: the same hue at
a different saturation. The alternative that was rejected, `#006DE2`, is at 211.1°, and 10° off
reads as wrong where the two meet: header, sidebar, auth panel.

The mark came from an image model and cannot be generated again, so `logo.svg` is the only
instance. Its vector regularises what was noise in the raster: long edges are exact lines,
corners are exact intersections, the arrow's shaft has constant width, and the A is symmetric
about one axis. The composition is untouched, and so is a slight asymmetry in the arrowhead.

## The plate

The favicon sits on a **white plate with an 18% corner radius, the mark inset 5% on each side**.
`Logo.tsx` renders the same file, `favicon.svg`, so the tile in the browser tab and the tile in
the app are one object.

Four candidates were rendered over five tab-strip colours sampled from Chrome, and scored by the
alpha-weighted share of the mark's ink that falls below 3:1 against whatever is behind it. **At
16px**, the size a tab strip asks for at 100% scaling:

| candidate | white strip | light blue | saturated blue | dark | dark blue | plate vs strip |
| --- | --- | --- | --- | --- | --- | --- |
| transparent (what shipped before) | 70% | 71% | **100%** | 54% | 54% | — |
| white plate | 69% | 69% | 69% | 69% | 69% | 1.00 – 16.33 |
| brand plate, white mark | 33% | 33% | 33% | 33% | 33% | 1.84 – 4.87 |
| brand plate, cyan arrow | 58% | 58% | 58% | 58% | 58% | 1.84 – 4.87 |

The worst strip at each of the four sizes a 16px slot resolves to (100%, 125% and 200% scaling,
and the Windows shortcut):

| candidate | 16px | 20px | 32px | 48px |
| --- | --- | --- | --- | --- |
| transparent (what shipped before) | 100% | 100% | 100% | 100% |
| white plate | 69% | 66% | 64% | 62% |
| brand plate, white mark | 33% | 20% | 15% | 12% |
| brand plate, cyan arrow | 58% | 52% | 50% | 48% |

**Both tables are emitted by the script, not transcribed.** With the rasteriser of
[Regenerating the icons](#regenerating-the-icons) installed,
`node frontend/scripts/favicon-contrast.js` prints them as markdown, ready to paste. The
candidates were judged at a 14% inset; the 5% inset that ships measures **66%**.

- **A plate at all**, because the transparent mark loses 100% of its ink on a saturated blue
  strip: its dark blue and that strip are the same colour. A plate makes legibility independent
  of what the browser paints behind it, which is why the plated rows are flat.
- **White and not brand, against the numbers.** A brand plate scores about twice as well, but
  only by turning the mark into a white silhouette, which is a different logo. The 69% is almost
  all the cyan at 2.32:1 on white, the same cyan the app renders on every white surface it has.
  The white plate is invisible on a white strip (1.00:1): it buys nothing there, and costs
  nothing.
- **5% inset.** 14% left the mark timid, and 2% left no white margin, so on a dark strip the
  plate stopped reading as a plate. The mark is 4:3, so the inset governs the left and right
  margins alone.
- **`theme-color` is `#0077b6` because the plate is white**: the tile reads at 4.87:1 against the
  browser UI it tints. Change one without the other and the icon dissolves into the bar.

## Regenerating the icons

```bash
cd frontend
npm install --no-save @resvg/resvg-js@2.6.2
npm run generate:icons
```

That writes eight files into `frontend/public/`:

- `favicon.svg`: the square plated tile the app and the tab both use
- `favicon.ico`: six frames, 16 through 256
- `favicon-96x96.png`
- `apple-touch-icon.png`
- `web-app-manifest-192x192.png`, `web-app-manifest-512x512.png`: **maskable**, so the mark sits
  inside Android's 40% safe circle
- `web-app-manifest-any-192x192.png`, `web-app-manifest-any-512x512.png`: **any**, drawn larger
  because nothing crops them

**`--no-save`.** `@resvg/resvg-js` is a native module and the icons change roughly never, so it
is not a devDependency: `--no-save` installs it and leaves `package.json` and the lockfile
untouched. The script exits with this exact command if the module is missing.

**The version is pinned.** The committed PNGs and ICO are artifacts of one rasteriser: a release
that changes antialiasing by a hair would rewrite every icon, with no change to `logo.svg` to
explain the diff. To upgrade it, change `RASTERISER` in `frontend/scripts/generate-icons.js`, the
version `favicon-contrast.js` names beside it and the command above, regenerate, and look at the
result.

## What stops the icons going stale

`frontend/scripts/generate-icons.js` also writes `icons.lock.json` beside itself: the
rasteriser's version and the sha256 of the master, of the script itself and of all eight
artifacts. `iconProvenance.test.ts` recomputes those hashes on every test run, with nothing
installed: CI cannot regenerate and diff, because it does not have the rasteriser. The test fails
when:

- `logo.svg` was edited and the icons were left stale;
- an icon was edited by hand;
- the generator changed without a regeneration;
- the pin in the lock is not the one in the command on this page.

For the first three it names the file that moved. The fix is the command above.

## Why the generator exists rather than a favicon website

- **Each size is rasterised from the vector at its own resolution, never downscaled from one
  large PNG.** At 512 the two are indistinguishable; at 16 the difference is a legible mark or a
  smudge, because the rasteriser needs the geometry to decide where the antialiasing goes.
- **Coverage differs by role.** Maskable icons are cropped by Android to a circle of radius 40%,
  so the mark stays at 60% of the width, and the script asserts that the bounding box's
  half-diagonal fits that circle: a wide mark can pass a width check and still lose its corners.
  The apple-touch icon is opaque, square and padded, because iOS composites on black and applies
  its own rounded mask. The favicon family is never cropped, so it carries its own rounded
  corners and runs to 90%.
- **The ICO carries six frames, each rendered at its own size.** 16, 32 and 48 are what a browser
  asks for; 64 through 256 are what Windows asks for, and without them it stretches the 48px
  frame. A writer handed one image and a list of sizes downscales it, and some silently drop
  sizes larger than the base image and save without error.
- **16px is a real limit.** At that size the arrow is one pixel wide, and the wave turns into a
  smudge across the letter. Dropping the wave helps; dropping the arrow does not, because the A's
  legs then detach from its apex. A small-size variant would start there, not from a redraw.
