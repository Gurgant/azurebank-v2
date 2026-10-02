import { useEffect, useRef } from 'react';

/**
 * Lands lost focus on the words a visitor must read, when the view that carries them appears.
 *
 * A money send disables the control that started it, and the browser hands that control's focus to
 * `body`. The view that answers the send then appears with nothing focused, and a screen reader
 * says nothing about it: a region that mounts already filled is not read. Focus on the sentence is
 * there so that it is read, before the actions under it are reached, and one landing is meant to be
 * one reading. What a screen reader was heard to say is in ADR-0059's Validation.
 *
 * Put the returned ref on the element, which needs `tabIndex={-1}` to take focus without joining
 * the Tab order. `active` is whether the view is on screen: `true` from the first render for a view
 * that is mounted to be shown, the flag that shows it for one drawn inside a host that was already
 * there (a dialog). Focus moves when `active` turns true, and only then.
 *
 * It only gives back focus that was lost: it moves focus only while nothing the visitor chose
 * holds it. Lost is on `body`, or on a container that holds the landing itself: a dialog's own
 * surface, a page's `main`. A press on a disabled control puts focus there, on the nearest
 * ancestor that can hold it. Measured in Chromium on the running stack: the second press of a
 * double click came on the send button while its send was still out, focus went to the dialog,
 * and the view then appeared with its sentence unfocused. (Until 2026-10-02 lost was `body`
 * alone, the test `useWaitLanding` makes.) A visitor who put focus on a control during the wait
 * keeps their place, and so does another dialog that holds it; nothing is spoken then.
 */
export function useFocusWhenLost<T extends HTMLElement = HTMLElement>(active: boolean) {
  const landingRef = useRef<T | null>(null);

  useEffect(() => {
    const landing = landingRef.current;
    // `body` holds everything on the page, so it is the first of the containers, not a case apart.
    if (active && landing && document.activeElement?.contains(landing)) {
      landing.focus();
    }
  }, [active]);

  return landingRef;
}
