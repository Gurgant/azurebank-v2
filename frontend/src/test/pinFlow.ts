import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MOCK_PIN } from '../mocks/state';

/**
 * Filling a PIN, in one place instead of five.
 *
 * Before ADR-0042 this idiom existed as `confirmWithPinAndSend` — **byte-identical in three test
 * files** — plus three more named copies (`enterPin` in the step-up interceptor and withdraw suites,
 * `pasteDigits` in PIN setup) and two inline repeats. When the sixth digit became the submit, every
 * one of those copies became wrong in the same way at the same moment. That is the argument for
 * this file: not tidiness, but that a change to how a PIN is entered should be one edit.
 *
 * ── Why `paste` and never `type` ──────────────────────────────────────────────────────────────
 *
 * `userEvent.type` TRUNCATES a Fluent `Input`, so a six-digit PIN typed that way arrives short and
 * the test fails for a reason that has nothing to do with the behaviour under test. Every helper in
 * this repo already worked around it; the workaround now lives here with its reason attached.
 */

/**
 * The seeded PIN every mock fixture accepts.
 *
 * RE-EXPORTED, not restated. A copy would drift silently the day the mock reseeds, and it would
 * take every PIN test with it at once — with a failure that points at the pages rather than at the
 * fixture. One value, one home.
 */
export const TEST_PIN = MOCK_PIN;

/**
 * Fill the PIN boxes.
 *
 * Since ADR-0042 this is ALSO the submit: the sixth digit sends, so there is no button to press
 * afterwards and callers must not look for one. A caller that wants to reach the PIN step WITHOUT
 * sending passes a short value — five digits leaves `onComplete` unfired, which is the only honest
 * way left to express "on the PIN step, not yet sent".
 */
export async function enterPin(pin: string = TEST_PIN): Promise<void> {
  await userEvent.click(screen.getByLabelText('Digit 1 of 6'));
  await userEvent.paste(pin);
}

/**
 * Review → PIN → sent.
 *
 * The review step's primary button is `Continue`; entering the PIN completes the transfer. Callers
 * used to pass the Send button's label — that parameter is gone with the button, and its absence is
 * what makes every call site declare the new behaviour rather than merely tolerate it.
 */
export async function confirmWithPin(pin: string = TEST_PIN): Promise<void> {
  await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
  await enterPin(pin);
}
