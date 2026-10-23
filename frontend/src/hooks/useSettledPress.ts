import { useCallback, useLayoutEffect, useRef, type MouseEvent } from 'react';

/**
 * How long a press on the actions of a view that stands in a money form's place after a send is
 * ignored: from the moment the view first appears, and again from each change to what it draws.
 *
 * What it covers: the wait counts from the change, and the change comes after the first press of
 * a double click, so every second press that follows the first within 500 ms is dropped. 500 ms is
 * the double-click time Windows uses by default, the longest gap at which it still counts two
 * presses as one double click: Microsoft's `SetDoubleClickTime` page gives the default ("the
 * default double-click time of 500 milliseconds",
 * https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setdoubleclicktime),
 * and its `GetDoubleClickTime` page only the 5000 ms maximum. `GetDoubleClickTime` returned 500
 * on the machine that measured. On the running stack, twelve double clicks on Deposit sent as two
 * clicks with a 130 ms wait between them put the second press 154 to 207 ms after the first, and
 * the four of those that landed on a button of the check view came 86 to 124 ms after the change
 * they followed. A press more than 500 ms after the last change acts on whatever is under it.
 */
export const SETTLE_MS = 500;

export interface SettledPressOptions {
  /**
   * The wait also runs once when `active` turns false: for a next layout that can send, or change
   * what is sent, as the deposit form's own Deposit, which comes back under the point of "try
   * again", and the amounts and accounts of the forms that "try again" and the transfer pages'
   * "start over" bring back.
   */
  afterLeaving?: boolean;
}

/** Where an element is laid out, as its box with every scroll offset above it added back. */
interface Place {
  x: number;
  y: number;
  width: number;
  height: number;
}

/**
 * Where `element` is laid out, apart from scrolling: a box above it that the visitor scrolls moves
 * it on screen, but the visitor aims after scrolling, so that is no move. The root's offset is the
 * page's own scroll.
 */
function placeOf(element: HTMLElement): Place {
  const box = element.getBoundingClientRect();
  let x = box.left;
  let y = box.top;
  for (let above = element.parentElement; above; above = above.parentElement) {
    x += above.scrollLeft;
    y += above.scrollTop;
  }
  return { x, y, width: box.width, height: box.height };
}

/** More than half a pixel apart on any side: less cannot take a button from under a pointer. */
function moved(before: Place, now: Place) {
  return (
    Math.abs(before.x - now.x) > 0.5 ||
    Math.abs(before.y - now.y) > 0.5 ||
    Math.abs(before.width - now.width) > 0.5 ||
    Math.abs(before.height - now.height) > 0.5
  );
}

/** A size watcher and the boxes it watches now. */
interface Watch {
  observer: ResizeObserver;
  watched: Set<Element>;
}

/**
 * Has `watch` watch the actions, the box that holds them and every box beside them in it, as they
 * are now, and no box that has left. A view beside the actions comes and goes with what is drawn,
 * after the actions were first given to the watcher.
 */
function watchAround(watch: Watch, actions: HTMLElement) {
  const parent = actions.parentElement;
  const boxes = new Set<Element>([actions]);
  if (parent) {
    boxes.add(parent);
    for (const child of Array.from(parent.children)) boxes.add(child);
  }
  for (const box of watch.watched) {
    if (boxes.has(box)) continue;
    watch.observer.unobserve(box);
    watch.watched.delete(box);
  }
  for (const box of boxes) {
    if (watch.watched.has(box)) continue;
    watch.observer.observe(box);
    watch.watched.add(box);
  }
}

/**
 * Drops a press aimed at an earlier layout. Spread `actions` on the element that holds a view's
 * actions: while `active`, a click inside it does nothing for `SETTLE_MS` after the view appears,
 * after `layout` changes, and after the actions move. `layout` names what the actions are (the
 * verdict and what it offers), because one button can take another's place at the same size. A
 * move is seen three ways. The size of the actions, of the box that holds them or of a box beside
 * them in it changes: the read's hint gaining its words at 5 s grows the view above them, and a
 * banner above them comes or goes. The viewport changes size and the actions are not where they
 * were. Or, at the press itself, the actions are not where they were last seen. The last covers
 * the frame between a change and the browser's report of it: a size watcher reports only at the
 * next rendering step, and a press can come before that, on the new layout.
 *
 * `content` is the same drop without the watching, for the part of the next layout beside the
 * actions that comes back under the point when the view goes: with `afterLeaving`, a money form's
 * quick amounts and account cards, and the "Use max" of the withdrawal and of the transfer pages,
 * where the second press of a double click on "try again" or "start over" would otherwise change
 * what the form goes on to send. It is a second set of handlers, not a second use of the actions'
 * ref, whose watching starts from the one element it is given, and it can be spread on several
 * elements.
 *
 * Measured in Chromium on the running stack: at 360×800 the second press of a double click on
 * Deposit, 166 ms after the first, came once the check view had grown to say "not yet", and
 * landed on "Check recent transactions": the visitor was taken to the history, and the view that
 * says to check before sending again was gone unread. Where that press lands differs with every
 * size and verdict.
 *
 * A dropped press takes no focus either: its `mousedown` is refused, as the dialog's backdrop
 * refuses its own, so a later Enter does not act on a button the visitor never chose.
 *
 * A click is dropped whatever made it, a key's included. The check view lands focus on its title
 * when it appears, which is no button, but an ask made from "Check again", or at the bound while
 * focus is on it, lands focus on the view's first button once the answer is drawn, and that can
 * be another button than the one the key was meant for. Measured at 1280×900 with no guard: an
 * Enter 33 ms after focus had moved to "Check recent transactions" went to the history. Nothing is
 * disabled or renamed while it waits: a screen reader hears the same buttons throughout, and only
 * the press is lost.
 */
export function useSettledPress(
  active: boolean,
  layout: string,
  { afterLeaving = false }: SettledPressOptions = {},
) {
  // When what is drawn last changed, on `performance.now()`'s clock.
  const drawnAt = useRef(-Infinity);
  // Read by the size observer, which outlives renders.
  const activeNow = useRef(active);
  // The element the actions are in, and where it was when last looked at while the view is up.
  const actionsNow = useRef<HTMLElement | null>(null);
  const seenAt = useRef<Place | null>(null);
  const watchNow = useRef<Watch | null>(null);

  useLayoutEffect(() => {
    const left = activeNow.current && !active;
    activeNow.current = active;
    if (active || (left && afterLeaving)) drawnAt.current = performance.now();
    seenAt.current = active && actionsNow.current ? placeOf(actionsNow.current) : null;
  }, [active, layout, afterLeaving]);

  // After every render, not only a change of `layout`: whichever render puts a view beside the
  // actions, its size is watched from that commit on.
  useLayoutEffect(() => {
    if (watchNow.current && actionsNow.current) watchAround(watchNow.current, actionsNow.current);
  });

  /*
    The actions, the box that holds them and the boxes beside them: in a dialog its surface, its
    header and the view or form drawn there. The surface is centred, so while it is shorter than
    its `maxHeight` a view that grows grows it too, and moves the actions with it. Once it is cut
    to that height it keeps its size and scrolls, the footer keeps its own, and only the view's
    size says that the actions moved. Measured at 320×568 in Chromium: the hint's words grew the
    check view from 306 to 338 px and moved the footer 32 px down, while the surface stayed
    511.19 px tall and the footer 149 px. With only those two watched, one press on "Check recent
    transactions" 976 ms after the words, and nothing pressed before it, was dropped.
  */
  const ref = useCallback((actions: HTMLElement | null) => {
    if (actions === null) return;
    actionsNow.current = actions;
    const rearm = () => {
      if (!activeNow.current) return;
      drawnAt.current = performance.now();
      seenAt.current = placeOf(actions);
    };
    const watch: Watch = { observer: new ResizeObserver(rearm), watched: new Set() };
    watchNow.current = watch;
    watchAround(watch, actions);
    /*
      A change of the viewport's size is a move only where it moves the actions: a centred dialog
      moves with the viewport, the actions in a page's flow do not. On a phone the address bar
      hides as the page scrolls, which is how a visitor reaches the transfer pages' actions, and a
      press made once they are reached must not wait on the viewport that changed under it.
      Measured in Chromium with the viewport made 56 px taller at 360×640: on the transfer page's
      check view "Check recent transactions" stayed where it was, and a press on it 23 ms later
      was dropped while every change of size started the wait again; in the deposit dialog it
      moved 17.25 px, and a press there 26 ms later is dropped as before.
    */
    const resized = () => {
      if (!activeNow.current) return;
      const now = placeOf(actions);
      if (seenAt.current && !moved(seenAt.current, now)) return;
      drawnAt.current = performance.now();
      seenAt.current = now;
    };
    window.addEventListener('resize', resized);
    return () => {
      watch.observer.disconnect();
      window.removeEventListener('resize', resized);
      if (actionsNow.current === actions) actionsNow.current = null;
      if (watchNow.current === watch) watchNow.current = null;
    };
  }, []);

  /** Whether a press now is dropped; actions found moved start the wait again first. */
  const waiting = useCallback(() => {
    const actions = actionsNow.current;
    if (activeNow.current && actions && seenAt.current) {
      const now = placeOf(actions);
      if (moved(seenAt.current, now)) {
        drawnAt.current = performance.now();
        seenAt.current = now;
      }
    }
    return performance.now() - drawnAt.current < SETTLE_MS;
  }, []);

  const onClickCapture = useCallback(
    (event: MouseEvent) => {
      if (!waiting()) return;
      event.preventDefault();
      event.stopPropagation();
    },
    [waiting],
  );

  const onMouseDownCapture = useCallback(
    (event: MouseEvent) => {
      if (waiting()) event.preventDefault();
    },
    [waiting],
  );

  return {
    actions: { ref, onClickCapture, onMouseDownCapture },
    content: { onClickCapture, onMouseDownCapture },
  };
}
