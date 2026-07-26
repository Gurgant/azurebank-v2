import { act, render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { setReducedMotion } from '../test/viewport';
import { useReducedMotion } from './useReducedMotion';

/**
 * The suite reports reduced motion as PREFERRED for every test, deliberately — it is what keeps
 * Fluent's dialog transitions synchronous and stops role queries missing buttons that are really
 * there. The cost is that the animated path is never exercised, and that cost is what these tests
 * pay off: `setReducedMotion(false)` reaches the other branch for the one test that wants it,
 * instead of turning the flake back on across the whole suite.
 */

function Probe() {
  return <div data-testid="motion">{useReducedMotion() ? 'reduced' : 'full'}</div>;
}

const seen = () => screen.getByTestId('motion').textContent;

describe('useReducedMotion', () => {
  it('reports the suite default: reduced', () => {
    render(<Probe />);

    expect(seen()).toBe('reduced');
  });

  it('reports full motion once the preference is cleared', () => {
    setReducedMotion(false);
    render(<Probe />);

    expect(seen()).toBe('full');
  });

  it('follows the preference while mounted', () => {
    render(<Probe />);
    expect(seen()).toBe('reduced');

    act(() => setReducedMotion(false));
    expect(seen()).toBe('full');
  });

  it('is restored between tests', () => {
    // The previous test left it false. afterEach has to have put it back, or this suite has been
    // quietly changing the motion environment for everything that runs after it.
    render(<Probe />);

    expect(seen()).toBe('reduced');
  });
});
