import { act, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { expect, vi } from 'vitest';
import { problem } from '../mocks/problem';

/**
 * What a visitor reads while a request is slow or the service is down, typed out here and NOT
 * imported from `api/problemMessages.ts`. A test that imported the product's constant would pass
 * whatever the constant said; these fail the day the words on screen drift from the approved
 * ones — the ellipsis is U+2026, the apostrophes are ASCII.
 */
export const COPY = {
  slow: 'Taking longer than usual…',
  stillTrying: 'Still trying…',
  stopWaiting: 'Stop waiting',
  noDoubleCharge: "Retrying won't charge you twice.",
  noDoubleMove: "Retrying won't move the money twice.",
  unavailable: 'The service is temporarily unavailable. Please try again later.',
  nothingChanged:
    'The service is temporarily unavailable, and nothing was changed. Please try again later.',
  depositUnknown:
    "The service is temporarily unavailable, and we can't tell yet whether your deposit went through. Tap Deposit again to check.",
  withdrawalUnknown:
    "The service is temporarily unavailable, and we can't tell yet whether your withdrawal went through. Tap Withdraw again to check.",
  noMoneyMoved:
    'The service is temporarily unavailable, and no money was moved. Please try again later.',
  saveUnknown:
    "The service is temporarily unavailable, and we can't tell yet whether your change was saved. Please try again later.",
  accountUnknown:
    "The service is temporarily unavailable, and we can't tell yet whether your account was opened. Check your accounts before trying again.",
  tryAgain: 'Try again',
  unavailableTitle: 'Temporarily unavailable',
  loaded: 'Loaded.',
  keepPageOpen: 'Still trying… Keep this page open.',
  signOutFailed: "We couldn't sign you out. You're still signed in.",
  transferWentThrough: "Your transfer went through, but we couldn't show its receipt.",
  depositWentThrough: "Your deposit went through, but we couldn't show its receipt.",
  withdrawalWentThrough: "Your withdrawal went through, but we couldn't show its receipt.",
} as const;

/**
 * The API's 409 for a money send that was committed and whose answer was lost, once the claim is
 * past its stale age (ADR-0009): `IDEMPOTENCY_RESULT_UNKNOWN` with `applied: true`, the one answer
 * that says the payment went through. `instance` is the request's path. `application/json`, as the
 * API sends this 409 (the mock's default for a problem is `application/problem+json`).
 */
export const committedAnswerLost = (instance: string) =>
  problem({
    status: 409,
    errorCode: 'IDEMPOTENCY_RESULT_UNKNOWN',
    detail:
      'The operation sent with this idempotency key was applied, but this request cannot return its result. Do not send it again with a new key: look for it with GET /api/transactions.',
    instance,
    extensions: { applied: true },
    headers: { 'Content-Type': 'application/json; charset=utf-8' },
  });

/**
 * The browser's focus fixup, which jsdom does not run. Measured in headless Chromium 151 on
 * 2026-10-01: a focused button that is disabled hands focus to `body`, and enabling it again does
 * not give it back. In jsdom the disabled button keeps focus, so a test of where focus goes after
 * a pending button comes back would pass whatever the page did. Returns the function that stops
 * the emulation.
 */
export function emulateFocusFixup(): () => void {
  const observer = new MutationObserver((records) => {
    for (const { target } of records) {
      const control = target as HTMLButtonElement;
      if (control !== document.activeElement || !control.disabled) continue;
      // jsdom's blur() ignores an element that cannot take focus, so it is let go for a moment.
      control.disabled = false;
      control.blur();
      control.disabled = true;
    }
  });
  observer.observe(document.body, {
    attributes: true,
    attributeFilter: ['disabled'],
    subtree: true,
  });
  return () => observer.disconnect();
}

/**
 * A read page's one alert region, in `scope`: on the page before anything has failed, empty until
 * then, and atomic, so that whatever fills it is read whole.
 */
export function alertSlot(scope: HTMLElement = document.body): HTMLElement {
  const alerts = Array.from(scope.querySelectorAll<HTMLElement>('[role="alert"]'));
  expect(alerts, 'not exactly one role="alert"').toHaveLength(1);
  expect(alerts[0]).toHaveAttribute('aria-atomic', 'true');
  return alerts[0];
}

/*
  jsdom runs `requestAnimationFrame` on a Node `setInterval`, which it starts on the first frame
  request made while none is pending (jsdom/lib/jsdom/browser/Window.js). Started while the clock
  is fake, that interval dies when the clock is restored, and every later frame in the file waits
  for ever — and RTK delivers a store change to its subscribers on the next frame
  (autoBatchEnhancer's default), so a page stops seeing its own cache. Measured: a test that edited
  the accounts cache passed alone and failed after a fake-clock test in the same file, and a bare
  frame request there never resolved.

  So frame requests are kept pending at all times, made through jsdom's own function while the
  clock is real, and the interval is never stopped for the rest of the file. TWO chains, not one:
  jsdom drops a callback — and with the last one, the interval — before it runs it, so a lone
  chain that renews itself starts a new interval on whatever clock is installed at that moment.
*/
const jsdomAnimationFrame = window.requestAnimationFrame.bind(window);
let framesKeptReal = false;

function keepAnimationFramesOnTheRealClock() {
  if (framesKeptReal) return;
  framesKeptReal = true;
  const chain = () => void jsdomAnimationFrame(chain);
  chain();
  chain();
}

/**
 * A fake clock that still lets requests resolve. `shouldAdvanceTime` is the suite's rule
 * (SessionExpiryWarning.test.tsx): a frozen clock deadlocks anything that awaits a response.
 * Install it BEFORE the request starts, or the request's own timers run on the real clock.
 */
export function installFakeClock() {
  keepAnimationFramesOnTheRealClock();
  vi.useFakeTimers({ shouldAdvanceTime: true });
}

/** Advance the fake clock inside `act`, so every timer-driven render is inside a scope. */
export const advance = (ms: number) =>
  act(async () => void (await vi.advanceTimersByTimeAsync(Math.max(0, ms))));

/**
 * Advance until `ms` have passed since `from`, a fake `Date.now()` taken earlier.
 *
 * The clock also moves with real time, so a fixed advance would overshoot by however long the
 * test took to get here. Measuring from a recorded instant keeps each threshold where it is said
 * to be: a "not yet" is measured from before the wait could have started, a "by now" from after
 * it certainly had.
 */
export const advanceUntil = (from: number, ms: number) => advance(from + ms - Date.now());

/** A user whose own pauses run on the fake clock. */
export const fakeClockUser = () => userEvent.setup({ advanceTimers: vi.advanceTimersByTime });

/** A handler answer that never comes: the request stays pending until something aborts it. */
export const never = () => new Promise<Response>(() => {});

/** A pause on the fake clock, for a handler that answers late. */
export const sleep = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

/**
 * The hint's words, and the live region that carries them — which must hold those words and
 * nothing else: not the countdown's timer, not an alert, not a region shared with a button.
 */
export function hintRegion(text: string, scope: HTMLElement = document.body): HTMLElement {
  const words = Array.from(scope.querySelectorAll<HTMLElement>('*')).find(
    (el) => el.textContent === text && el.children.length === 0,
  );
  if (!words) {
    throw new Error(`No element reads exactly "${text}".`);
  }
  const region = words.closest<HTMLElement>('[role="status"]');
  expect(region, `"${text}" is not inside a role="status" region`).not.toBeNull();
  expect(region?.textContent).toBe(text);
  expect(region?.closest('[role="alert"]')).toBeNull();
  return region as HTMLElement;
}

/**
 * Waits for a wait's hint to be on screen — still empty, as it is for its first 5 s — and returns
 * that instant, a fake `Date.now()`. The hint's clock starts when it mounts, so a "by now" counted
 * from here cannot be reached before the wait was drawn; a hint that restarted later would still
 * be silent at this instant plus 5 s.
 */
export async function hintShownAt(scope: HTMLElement = document.body): Promise<number> {
  await waitFor(() => expect(scope.querySelector('[data-wait-hint]')).not.toBeNull());
  return Date.now();
}

/**
 * A hint with no words yet moves nothing on the page: the outermost box that is there only to hold
 * it — the hint itself, or a wrapper around nothing else — is out of the page's flow. jsdom lays
 * nothing out, so this reads the computed position rather than measuring a shift.
 */
export function expectSilentHintTakesNoRoom(scope: HTMLElement = document.body) {
  const hint = scope.querySelector<HTMLElement>('[data-wait-hint]');
  expect(hint, 'no wait hint on screen').not.toBeNull();
  expect(hint?.textContent).toBe('');
  let box = hint as HTMLElement;
  while (box.parentElement && box.parentElement.childElementCount === 1) {
    box = box.parentElement;
  }
  expect(getComputedStyle(box).position).toBe('absolute');
}

/**
 * Whether the page would ask before a reload or a tab close: a `beforeunload` that a listener
 * cancelled is the browser's "Leave site?" prompt.
 */
export function reloadAsksFirst(): boolean {
  const event = new Event('beforeunload', { cancelable: true });
  window.dispatchEvent(event);
  return event.defaultPrevented;
}

/** The six PIN boxes' values, in order. */
export const pinBoxValues = () =>
  Array.from(
    { length: 6 },
    (_, i) => (screen.getByLabelText(`Digit ${i + 1} of 6`) as HTMLInputElement).value,
  );

/** A settled-or-not view of a promise, readable at any instant without awaiting it. */
export function track<T>(promise: Promise<T>) {
  const view: {
    state: 'pending' | 'fulfilled' | 'rejected';
    value?: T;
    error?: unknown;
    at?: number;
  } = { state: 'pending' };
  promise.then(
    (value) => Object.assign(view, { state: 'fulfilled', value, at: Date.now() }),
    (error: unknown) => Object.assign(view, { state: 'rejected', error, at: Date.now() }),
  );
  return view;
}
