import { act } from '@testing-library/react';
import { vi } from 'vitest';
import { SETTLE_MS } from '../hooks/useSettledPress';

/**
 * Waits as long as the actions of a view that stands in a money form's place ignore a press after
 * they appear or move (`useSettledPress`), on whichever clock the test runs. A press made sooner
 * is dropped by design, so a test that means a press to act waits for this first.
 */
export function settle() {
  return act(async () => {
    if (vi.isFakeTimers()) await vi.advanceTimersByTimeAsync(SETTLE_MS);
    else await new Promise((resolve) => setTimeout(resolve, SETTLE_MS));
  });
}
