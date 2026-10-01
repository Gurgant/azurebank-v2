import { useEffect, useRef } from 'react';

/**
 * Lands lost focus on the words a visitor must read, when the view that carries them appears.
 *
 * A money send disables the control that started it, and the browser hands that control's focus to
 * `body`. The view that answers the send then appears with nothing focused, and a screen reader
 * says nothing about it: a region that mounts already filled is not read. Focus on the sentence is
 * what makes it read, once, before the actions under it are reached.
 *
 * Put the returned ref on the element, which needs `tabIndex={-1}` to take focus without joining
 * the Tab order. `active` is whether the view is on screen: `true` from the first render for a view
 * that is mounted to be shown, the flag that shows it for one drawn inside a host that was already
 * there (a dialog). Focus moves when `active` turns true, and only then.
 *
 * It only gives back focus that was lost, the rule `useWaitLanding` follows: it moves focus only
 * while nothing holds it. A visitor who put focus somewhere during the wait keeps their place, and
 * so does another dialog that holds it; nothing is spoken then.
 */
export function useFocusWhenLost<T extends HTMLElement = HTMLElement>(active: boolean) {
  const landingRef = useRef<T | null>(null);

  useEffect(() => {
    if (active && document.activeElement === document.body) {
      landingRef.current?.focus();
    }
  }, [active]);

  return landingRef;
}
