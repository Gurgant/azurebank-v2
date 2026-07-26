import { useEffect, useState } from 'react';

/**
 * True only once `active` has held for `delayMs` without interruption.
 *
 * Loading indicators have a threshold below which they hurt. A skeleton that appears for 80ms and
 * vanishes is not feedback, it is a flicker: the reader registers that something moved without
 * learning anything, and a cached response makes every navigation flash. Below roughly 200ms people
 * perceive a transition as instant, so waiting that long before admitting to a wait keeps the fast
 * path visually silent and gives an indicator only to a real delay.
 *
 * The returned value is **derived** rather than stored — `active && fired` — so deactivation needs
 * no state update at all, and the timer is the only thing that ever calls setState. Resetting the
 * flag in the effect body instead would be the cascading-render pattern this codebase just finished
 * removing from `useResponsive`; the compiler lint rejects it, correctly.
 */
export function useDelayedFlag(active: boolean, delayMs = 200): boolean {
  const [fired, setFired] = useState(false);

  useEffect(() => {
    if (!active) return;
    const id = window.setTimeout(() => setFired(true), delayMs);
    // Cleanup, not effect body: this runs when `active` flips or the component unmounts, so a
    // second activation starts from a fresh delay rather than showing instantly.
    return () => {
      window.clearTimeout(id);
      setFired(false);
    };
  }, [active, delayMs]);

  return active && fired;
}
