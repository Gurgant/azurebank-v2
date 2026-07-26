import { act, render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { breakpoints } from '../theme/breakpoints';
import { setViewportWidth } from '../test/viewport';
import { useResponsive } from './useResponsive';

/**
 * The hook U1.1 rewrote, and until now the only part of U1 with no test at all.
 *
 * What it replaced seeded `isDesktop: true` and corrected itself in an effect, so on a phone render
 * #1 produced the full desktop shell and render #2 tore it down — a real frame of the wrong layout
 * plus a subtree remount, on every load. `useSyncExternalStore` returns the true value on the first
 * render instead. The first two tests below are what prove that: they assert the value a component
 * sees on its **very first** render, which is exactly what the old implementation got wrong.
 *
 * The last test is the one the old `matchMedia` stub made impossible. It could not be written, so
 * it was not, and the subscription half of the hook went unexercised.
 */

/**
 * Prints the flags themselves rather than a label derived from them. Deriving would hide the thing
 * most worth catching: the three size flags are meant to be mutually exclusive, and if two ever
 * came back true together this renders both instead of quietly preferring one.
 */
function Probe() {
  const { isMobile, isTablet, isDesktop, isTouch } = useResponsive();
  const flags = [
    isMobile && 'mobile',
    isTablet && 'tablet',
    isDesktop && 'desktop',
    isTouch && 'touch',
  ];
  return <div data-testid="probe">{flags.filter(Boolean).join(' ')}</div>;
}

const seen = () => screen.getByTestId('probe').textContent;

describe('useResponsive', () => {
  it('reports desktop at the lg breakpoint, on the first render', () => {
    // 1024 is lg exactly, and min-width is inclusive — the boundary belongs to the wider tree.
    setViewportWidth(breakpoints.lg);
    render(<Probe />);

    expect(seen()).toBe('desktop');
  });

  it('reports mobile on a phone width, with no desktop frame first', () => {
    setViewportWidth(375);
    render(<Probe />);

    // No waitFor, no findBy: if this ever needs one, the seed-then-correct bug is back.
    expect(seen()).toBe('mobile touch');
  });

  it('reports tablet between md and lg', () => {
    setViewportWidth(breakpoints.lg - 1);
    render(<Probe />);

    expect(seen()).toBe('tablet touch');
  });

  it('follows the viewport while mounted', () => {
    setViewportWidth(breakpoints.lg);
    render(<Probe />);
    expect(seen()).toBe('desktop');

    act(() => setViewportWidth(375));
    expect(seen()).toBe('mobile touch');

    act(() => setViewportWidth(breakpoints.lg));
    expect(seen()).toBe('desktop');
  });

  it('ignores a resize that does not cross a breakpoint', () => {
    setViewportWidth(1280);
    render(<Probe />);

    act(() => setViewportWidth(1440));

    // Both widths are above lg, so nothing about the answer changed. This is here because the
    // stub only notifies queries whose match actually moved — without that, any re-render would
    // look like a correct reaction and this hook could be wrong in a way tests would applaud.
    expect(seen()).toBe('desktop');
  });
});
