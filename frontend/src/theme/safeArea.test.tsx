import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { AppLayout } from '../components/layout/AppLayout';
import { BottomNav } from '../components/layout/BottomNav';
import { renderWithProviders } from '../test/renderWithProviders';
import { setViewportWidth, TEST_VIEWPORT_WIDTH } from '../test/viewport';
import { safeArea } from './tokens';

/**
 * The safe area has three parts that must agree, and each fails silently on its own.
 *
 * The meta tag decides whether the insets exist at all; the stylesheet decides what the names mean;
 * the components decide whether anything uses them. Before this PR the app had the third without the
 * first — two components reserved space with `env(safe-area-inset-bottom)` while `index.html` had no
 * `viewport-fit=cover`, so both insets were 0 on every device and the code read as if it worked.
 *
 * Nothing in a browser complains about that. `env()` with a fallback is valid CSS whether or not the
 * UA has a value for it, so the failure is invisible except on the hardware nobody develops on.
 */

const INDEX_HTML = readFileSync('index.html', 'utf8');
const INDEX_CSS = readFileSync('src/index.css', 'utf8');

const SIDES = ['top', 'right', 'bottom', 'left'] as const;

describe('safe-area insets', () => {
  it('asks the browser for the full viewport, without which every inset is zero', () => {
    const viewport = /<meta\s+name="viewport"\s+content="([^"]*)"/.exec(INDEX_HTML);

    expect(viewport).not.toBeNull();
    expect(viewport![1]).toContain('viewport-fit=cover');
  });

  it.each(SIDES)('declares --ab-safe-%s from the matching env()', (side) => {
    expect(INDEX_CSS).toContain(`--ab-safe-${side}: env(safe-area-inset-${side}, 0px);`);
  });

  it('exports a reference to each declared property, and no literal', () => {
    // The same direction the palette is asserted in: a token that resolved to a raw `env()` would
    // work, and would also be unoverridable — which is the property that makes this verifiable in a
    // browser at all.
    for (const side of SIDES) {
      expect(safeArea[side]).toBe(`var(--ab-safe-${side})`);
    }
  });
});

function sourceFiles(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) return sourceFiles(path);
    if (!/\.tsx?$/.test(entry.name) || entry.name.includes('.test.')) return [];
    return [path];
  });
}

describe('the components that reserve the space', () => {
  /**
   * A source scan, because the thing being prevented is a SECOND source of truth.
   *
   * `env(safe-area-inset-bottom, 0px)` appeared inline in two files before this, which is how the
   * missing meta tag stayed unnoticed for so long: there was no single place where an inset was
   * defined and therefore no place the question "does this work?" could be asked. Reaching for the
   * raw `env()` again would rebuild exactly that.
   */
  it('reserves space through the token, never through a second inline env()', () => {
    const offenders = sourceFiles('src')
      // `src/theme` is exempt for the same reason the ESLint colour rule exempts it: this is where
      // the value is DEFINED, so naming it here is the definition, not a second copy. The scan
      // caught this file's own prose on its first run, which is the right kind of over-broad to
      // find early.
      .filter((file) => !file.includes(join('src', 'theme')))
      .filter((file) => /env\(\s*safe-area-inset/.test(readFileSync(file, 'utf8')))
      .map((file) => file.slice(file.indexOf('src')));

    expect(offenders).toEqual([]);
  });

  it('still has consumers, so an empty pass cannot come from nobody using it', () => {
    // The guard the assertion above needs. Deleting every inset would satisfy "no raw env()"
    // perfectly, and would also be the bug.
    const consumers = sourceFiles('src').filter((file) =>
      /safeArea\.(top|right|bottom|left)/.test(readFileSync(file, 'utf8')),
    );

    expect(consumers.length).toBeGreaterThanOrEqual(4);
  });
});

describe('the bar the insets were written for', () => {
  /**
   * The one link in the chain the assertions above cannot reach.
   *
   * They prove the component imports the token, the token is a `var()`, the property is declared
   * from `env()`, and the meta tag is present. What none of them proves is that Griffel EMITS the
   * declaration — a value it refused to handle would vanish with no error, and every other test here
   * would still be green. So this renders the bar and reads the rule back out of the document.
   *
   * jsdom resolves nothing (`getComputedStyle` would hand back the literal `var(...)`), which is
   * fine: the claim is about what was emitted, not what it computes to. What it computes to was
   * measured in a real browser instead.
   */
  it('emits the inset declarations Griffel was asked for', () => {
    renderWithProviders(<BottomNav />);

    // `textContent` is empty and that is not a bug: Griffel inserts through `insertRule`, which
    // never touches the element's text. The rules have to be read off the sheet.
    const emitted = [...document.styleSheets]
      .flatMap((sheet) => [...sheet.cssRules].map((rule) => rule.cssText))
      .join('');

    expect(emitted).toContain('var(--ab-safe-bottom)');
    expect(emitted).toContain('var(--ab-safe-left)');
    expect(emitted).toContain('var(--ab-safe-right)');
  });
});

describe('the shell with no bottom bar', () => {
  /**
   * The variant a reviewer caught, asserted through the class that actually wins.
   *
   * Griffel emits one atomic class per property, and `mergeClasses` keeps only the last class for
   * any given property — so the element carries exactly one `padding-bottom` class and it is the
   * one the browser will use. Reading THAT rule is what distinguishes "the declaration exists
   * somewhere in the stylesheet" from "the declaration applies here", and only the second is the
   * claim: `mobileContent` also sets a padding-bottom, so an emission-level check would pass
   * against a `0` that overrides it.
   */
  it('still clears the home indicator when there is no bar to clear', () => {
    // The suite runs at 1024 — exactly `lg` — so AppLayout renders its DESKTOP tree by default and
    // the mobile classes never appear. The first run of this test asserted against an element that
    // had no padding-bottom class at all, which the non-vacuity check caught rather than passing.
    setViewportWidth(390);

    const { container } = renderWithProviders(
      <AppLayout hideBottomNav>
        <div data-testid="page" />
      </AppLayout>,
    );

    const content = container.querySelector('[data-testid="page"]')!.parentElement!;
    const rules = [...document.styleSheets].flatMap((sheet) => [...sheet.cssRules]);

    const applied = [...content.classList]
      .flatMap((cls) => rules.filter((rule) => rule.cssText.startsWith(`.${cls}`)))
      .map((rule) => rule.cssText)
      .filter((text) => /padding-bottom/.test(text));

    // Non-vacuity: no padding-bottom class at all would satisfy the assertion below by default.
    expect(applied).toHaveLength(1);
    expect(applied[0]).toContain('var(--ab-safe-bottom)');

    setViewportWidth(TEST_VIEWPORT_WIDTH);
  });
});
