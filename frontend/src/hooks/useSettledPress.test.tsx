import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { installFakeClock } from '../test/outage';
import { SETTLE_MS, useSettledPress, type SettledPressOptions } from './useSettledPress';

/**
 * The wait itself, apart from the views that use it. jsdom lays nothing out, so the size of a box
 * never changes here on its own: the observer below stands in for the browser's, records what it
 * was asked to watch, and reports a change of size when a test says one happened. That the hint's
 * words, a banner above the buttons or the view growing do change those boxes is measured in
 * Chromium (`e2e/wentThrough.spec.ts` and the round's probe), not here.
 *
 * Every press is a `fireEvent`, made in the same task as the change it follows: the fake clock
 * follows real time only between tasks (`installFakeClock`), so the times below are exact.
 *
 * jsdom lays out no box either: where the actions are is the box `placed` gives them, which a test
 * moves to stand for a change of layout, and a box's scroll offset is the one a test gives it.
 */
class WatchedSizes {
  static all: WatchedSizes[] = [];
  readonly watched: Element[] = [];
  disconnected = false;
  private readonly report: ResizeObserverCallback;
  constructor(report: ResizeObserverCallback) {
    this.report = report;
    WatchedSizes.all.push(this);
  }
  observe(element: Element) {
    this.watched.push(element);
  }
  unobserve(element: Element) {
    this.watched.splice(this.watched.indexOf(element), 1);
  }
  disconnect() {
    this.disconnected = true;
  }
  /** What the browser does when a watched box changes size. */
  resized() {
    act(() => this.report([], this as unknown as ResizeObserver));
  }
}

/** `element` changes size: every watcher still watching it reports, and no other. */
function sizeChanged(element: Element) {
  for (const watch of WatchedSizes.all) {
    if (!watch.disconnected && watch.watched.includes(element)) watch.resized();
  }
}

/** The actions' box, as a test places it; every other element's is jsdom's empty one. */
const placed = { top: 100, height: 48 };
function stubBoxes() {
  vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(function (
    this: HTMLElement,
  ) {
    return this.dataset.testid === 'actions'
      ? new DOMRect(0, placed.top, 320, placed.height)
      : new DOMRect();
  });
}

/** Gives `element` a scroll offset, as a visitor scrolling it would. */
function scrollTo(element: HTMLElement, top: number) {
  Object.defineProperty(element, 'scrollTop', { configurable: true, get: () => top });
}

/** The actions, the form's content beside them, and with `view` a view drawn above them. */
function Actions({
  active,
  layout,
  options,
  view = false,
  onPress,
}: {
  active: boolean;
  layout: string;
  options?: SettledPressOptions;
  view?: boolean;
  onPress: () => void;
}) {
  const settledPress = useSettledPress(active, layout, options);
  return (
    <div data-testid="around">
      {view && <div data-testid="view">We couldn&apos;t confirm your deposit</div>}
      <div data-testid="actions" {...settledPress.actions}>
        <button onClick={onPress}>Press</button>
      </div>
      <div data-testid="content" {...settledPress.content}>
        <button onClick={onPress}>Beside</button>
      </div>
    </div>
  );
}

const press = () => fireEvent.click(screen.getByRole('button', { name: 'Press' }));
const pressBeside = () => fireEvent.click(screen.getByRole('button', { name: 'Beside' }));
const wait = (ms: number) => act(() => void vi.advanceTimersByTime(ms));

describe('useSettledPress', () => {
  beforeEach(() => {
    WatchedSizes.all = [];
    placed.top = 100;
    placed.height = 48;
    vi.stubGlobal('ResizeObserver', WatchedSizes);
    stubBoxes();
    installFakeClock();
  });
  afterEach(() => {
    cleanup();
    vi.useRealTimers();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('watches the actions, the box that holds them and the boxes beside them, and stops when they go', () => {
    const { unmount } = render(<Actions active layout="" onPress={() => {}} />);
    expect(WatchedSizes.all).toHaveLength(1);
    const [watch] = WatchedSizes.all;
    expect(watch.watched).toEqual([
      screen.getByTestId('actions'),
      screen.getByTestId('around'),
      screen.getByTestId('content'),
    ]);
    unmount();
    expect(watch.disconnected).toBe(true);
  });

  it('watches a view drawn beside the actions after them: one press after it grew and the wait acts', () => {
    /*
      The check view comes after the dialog's footer was given to the watcher, and in a surface
      cut to its `maxHeight` the hint's words grow that view alone: the surface and the footer
      keep their sizes, and the footer moves down. Seen as it happens, the place is recorded then,
      and the one press after the wait finds the actions where they were last seen.
    */
    const onPress = vi.fn();
    const { rerender } = render(<Actions active={false} layout="off" onPress={onPress} />);
    rerender(<Actions active layout="notYet" view onPress={onPress} />);
    const view = screen.getByTestId('view');
    expect(WatchedSizes.all[0].watched).toContain(view);
    wait(SETTLE_MS);

    placed.top += 24;
    sizeChanged(view);
    wait(SETTLE_MS);
    press();
    expect(onPress).toHaveBeenCalledTimes(1);

    // And the view that goes is no longer watched.
    rerender(<Actions active={false} layout="off" onPress={onPress} />);
    expect(WatchedSizes.all[0].watched).not.toContain(view);
  });

  it('starts the wait again when a watched box changes size while the view is up', () => {
    const onPress = vi.fn();
    render(<Actions active layout="notYet" onPress={onPress} />);
    wait(SETTLE_MS);
    press();
    expect(onPress).toHaveBeenCalledTimes(1);

    // The hint gains its words, or a banner above the buttons goes: the buttons move.
    WatchedSizes.all[0].resized();
    press();
    wait(SETTLE_MS - 1);
    press();
    expect(onPress).toHaveBeenCalledTimes(1);
    wait(1);
    press();
    expect(onPress).toHaveBeenCalledTimes(2);
  });

  it('does not wait on a change of size while no view is up', () => {
    const onPress = vi.fn();
    render(<Actions active={false} layout="" onPress={onPress} />);
    WatchedSizes.all[0].resized();
    press();
    expect(onPress).toHaveBeenCalledTimes(1);
  });

  it('refuses the mousedown of a press it drops, so that the press takes no focus, and not after', () => {
    render(<Actions active layout="" onPress={() => {}} />);
    const button = screen.getByRole('button', { name: 'Press' });
    // `fireEvent` returns false when a handler refused the event's default.
    expect(fireEvent.mouseDown(button)).toBe(false);
    wait(SETTLE_MS);
    expect(fireEvent.mouseDown(button)).toBe(true);
  });

  it('waits once more as the view goes only when asked to, for actions whose next layout sends', () => {
    const onPress = vi.fn();
    const { rerender } = render(
      <Actions active layout="none" options={{ afterLeaving: true }} onPress={onPress} />,
    );
    wait(SETTLE_MS);
    rerender(
      <Actions active={false} layout="off" options={{ afterLeaving: true }} onPress={onPress} />,
    );
    press();
    wait(SETTLE_MS - 1);
    press();
    expect(onPress).not.toHaveBeenCalled();
    wait(1);
    press();
    expect(onPress).toHaveBeenCalledTimes(1);

    // Without the option the form's actions act at once.
    cleanup();
    const plain = vi.fn();
    const second = render(<Actions active layout="none" onPress={plain} />);
    wait(SETTLE_MS);
    second.rerender(<Actions active={false} layout="off" onPress={plain} />);
    press();
    expect(plain).toHaveBeenCalledTimes(1);
  });

  it('drops a press on actions that moved since they were last seen, before any watcher says so', () => {
    /*
      A size watcher reports at the browser's next rendering step, and a press can come between a
      change and that step, on the new layout. At the press the actions are looked at again: found
      elsewhere, the press is dropped and the wait starts from there.
    */
    const onPress = vi.fn();
    render(<Actions active layout="notYet" onPress={onPress} />);
    wait(SETTLE_MS);
    press();
    expect(onPress).toHaveBeenCalledTimes(1);

    // The hint gains its words and the dialog grows around its centre: the actions are 16 px
    // lower, and nothing has reported it yet.
    placed.top += 16;
    expect(fireEvent.mouseDown(screen.getByRole('button', { name: 'Press' }))).toBe(false);
    press();
    wait(SETTLE_MS - 1);
    press();
    expect(onPress).toHaveBeenCalledTimes(1);
    wait(1);
    press();
    expect(onPress).toHaveBeenCalledTimes(2);
  });

  it('takes a scroll of a box above the actions for no move: the visitor aims after scrolling', () => {
    const onPress = vi.fn();
    render(<Actions active layout="notYet" onPress={onPress} />);
    wait(SETTLE_MS);

    // The dialog's surface scrolled 104 px: the actions are drawn 104 px higher.
    placed.top -= 104;
    scrollTo(screen.getByTestId('around'), 104);
    press();
    expect(onPress).toHaveBeenCalledTimes(1);
  });

  it('starts the wait again when the viewport changes size, and sees the actions where that left them', () => {
    const onPress = vi.fn();
    render(<Actions active layout="notYet" onPress={onPress} />);
    wait(SETTLE_MS);

    // A centred dialog moves with the viewport, and keeps its size: no box it watches changes.
    placed.top += 40;
    act(() => void window.dispatchEvent(new Event('resize')));
    wait(SETTLE_MS - 1);
    press();
    expect(onPress).not.toHaveBeenCalled();
    // Seen where the resize left them, so the press after the wait acts.
    wait(1);
    press();
    expect(onPress).toHaveBeenCalledTimes(1);
  });

  it('takes a change of the viewport that leaves the actions where they were for no move', () => {
    // A phone's address bar hiding as the page scrolls: the viewport is taller, and the actions
    // in the page's flow are where they were.
    const onPress = vi.fn();
    render(<Actions active layout="notYet" onPress={onPress} />);
    wait(SETTLE_MS);
    act(() => void window.dispatchEvent(new Event('resize')));
    press();
    expect(onPress).toHaveBeenCalledTimes(1);
  });

  it('drops a press beside the actions for as long as on them once the view goes, and only then', () => {
    const onPress = vi.fn();
    const { rerender } = render(
      <Actions active layout="none" options={{ afterLeaving: true }} onPress={onPress} />,
    );
    wait(SETTLE_MS);
    rerender(
      <Actions active={false} layout="off" options={{ afterLeaving: true }} onPress={onPress} />,
    );
    expect(fireEvent.mouseDown(screen.getByRole('button', { name: 'Beside' }))).toBe(false);
    pressBeside();
    wait(SETTLE_MS - 1);
    pressBeside();
    expect(onPress).not.toHaveBeenCalled();
    wait(1);
    pressBeside();
    expect(onPress).toHaveBeenCalledTimes(1);

    // Nor does it make a watcher of its own: one, the actions', which watches the content as a
    // box beside them.
    expect(WatchedSizes.all).toHaveLength(1);
  });
});
