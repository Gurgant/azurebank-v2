import { makeStyles } from '@fluentui/react-components';
import { describe, expect, it } from 'vitest';
import { renderWithProviders } from '../test/renderWithProviders';
import { atMedia, breakpoints } from './breakpoints';
import { compareMinWidthMediaQueries } from './mediaQueryOrder';

/** Griffel's default, copied verbatim from @griffel/core 1.19.2, so the contrast is testable. */
const griffelDefault = (a: string, b: string) => (a < b ? -1 : a > b ? 1 : 0);

const QUERIES = [breakpoints.sm, breakpoints.md, breakpoints.lg, breakpoints.xl].map(
  (px) => `(min-width: ${px}px)`,
);

describe('compareMinWidthMediaQueries', () => {
  it('orders breakpoints by width, ascending', () => {
    expect([...QUERIES].reverse().sort(compareMinWidthMediaQueries)).toEqual(QUERIES);
  });

  /**
   * The bug, stated as a test rather than as a comment.
   *
   * This asserts what Griffel's default DOES, not what we want — so it fails the day the default
   * stops being a string sort, which is the day this whole module can be deleted. Without it, a
   * future reader has only my word that the comparator was ever needed.
   */
  it('is not what a string sort gives, which is the whole reason it exists', () => {
    expect([...QUERIES].sort(griffelDefault)).toEqual([
      '(min-width: 1024px)',
      '(min-width: 1366px)',
      '(min-width: 480px)',
      '(min-width: 640px)',
    ]);
  });

  it('leaves queries with no width to Griffel, rather than inventing an order for them', () => {
    // The media bucket is shared with Fluent. These two have no width, no relationship to ours, and
    // an order that is already right; reordering them would be a change nobody asked for.
    const fluent = ['(forced-colors: active)', 'screen and (prefers-reduced-motion: reduce)'];

    expect([...fluent].sort(compareMinWidthMediaQueries)).toEqual([...fluent].sort(griffelDefault));
  });

  it('is a total order — a width against a non-width is deterministic, not zero', () => {
    // Returning 0 for an incomparable pair would make the sort order depend on insertion, which is
    // the class of bug this module removes.
    const a = '(min-width: 480px)';
    const b = '(forced-colors: active)';

    expect(compareMinWidthMediaQueries(a, b)).not.toBe(0);
    expect(Math.sign(compareMinWidthMediaQueries(a, b))).toBe(
      -Math.sign(compareMinWidthMediaQueries(b, a)),
    );
  });
});

const useFixtureStyles = makeStyles({
  probe: {
    padding: '1px',
    [atMedia.sm]: { padding: '2px' },
    [atMedia.lg]: { padding: '3px' },
  },
});

function Probe() {
  return <div className={useFixtureStyles().probe} />;
}

describe('the renderer the app actually installs', () => {
  /**
   * The assertion that catches the real regression.
   *
   * The unit tests above prove the comparator sorts; they say nothing about whether it is WIRED.
   * Remove `RendererProvider` from `ThemeProvider` and every one of them still passes while the app
   * goes back to emitting `lg` before `sm` — which is exactly the state this PR found. This renders
   * through the app's own provider stack and reads the order out of the document.
   */
  it('emits min-width rules in ascending order, so the wider breakpoint wins', () => {
    renderWithProviders(<Probe />);

    const widths = [...document.head.querySelectorAll<HTMLStyleElement>('style[media]')]
      .map((el) => /\(min-width:\s*(\d+)px\)/.exec(el.media)?.[1])
      .filter((width): width is string => width !== undefined)
      .map(Number);

    // Non-vacuity first: an empty list sorts as "ascending" and would pass silently.
    expect(widths).toEqual(expect.arrayContaining([breakpoints.sm, breakpoints.lg]));
    expect(widths).toEqual([...widths].sort((a, b) => a - b));
  });
});
