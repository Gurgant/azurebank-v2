import { act, screen, waitFor } from '@testing-library/react';
import userEvent, { type UserEvent } from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '../../mocks/server';
import { problem, serviceUnavailable } from '../../mocks/problem';
import { mockState } from '../../mocks/state';
import { emulateFocusFixup } from '../../test/outage';
import { renderWithProviders } from '../../test/renderWithProviders';
import { requestStepUp, type StepUpOutcome } from './stepUpController';
import { StepUpModal } from './StepUpModal';

/*
  The PIN prompt after a refusal, as a visitor on the keyboard meets it.

  While the check is out the boxes and Verify are disabled, and a browser hands the focus of a
  control that is disabled to `body`. jsdom does not, so `emulateFocusFixup` does it here. Left
  there, the next digits went nowhere, and Escape, which the dialog hears only from inside it, did
  nothing: measured in Chromium on the reveal before this file existed. The interceptor's own
  tests (`stepup-interceptor.test.tsx`) say what each answer does to the request; these say where
  the caret is and what the boxes show.
*/

let stopFocusFixup = () => {};

beforeEach(() => {
  stopFocusFixup = emulateFocusFixup();
});

afterEach(() => {
  stopFocusFixup();
});

const box = (n: number) => screen.getByLabelText(`Digit ${n} of 6`);
const boxes = () => [1, 2, 3, 4, 5, 6].map(box);

/** The prompt, opened through the controller as the reveal opens it, from a focused button. */
function openPrompt() {
  renderWithProviders(
    <>
      <button type="button">Reveal</button>
      <StepUpModal />
    </>,
  );
  const opener = screen.getByRole('button', { name: 'Reveal' });
  opener.focus();
  let outcome!: Promise<StepUpOutcome>;
  act(() => {
    outcome = requestStepUp({ requiredAuthLevel: 2 });
  });
  return { opener, outcome };
}

async function enterPin(user: UserEvent, pin: string) {
  await user.click(box(1));
  await user.paste(pin);
}

/** The element the PIN group's description points at: the bar, or the lock's block. */
function describedBy() {
  const id = screen.getByRole('group', { name: 'Enter your PIN' }).getAttribute('aria-describedby');
  return id ? document.getElementById(id) : null;
}

describe('the PIN prompt after a refusal', () => {
  it('puts the caret back in box 1 after a wrong PIN, with the boxes marked wrong', async () => {
    let checks = 0;
    server.use(
      http.post('*/bff/auth/verify-pin', () => {
        checks += 1;
        // Nothing returned: the mock's own handler answers, 200 with verified:false.
      }),
    );
    const user = userEvent.setup();
    const { opener, outcome } = openPrompt();
    await screen.findByText("Verify it's you");

    await enterPin(user, '000000');
    await screen.findByText('Incorrect PIN. Please try again.');

    await waitFor(() => expect(box(1)).toHaveFocus());
    for (const each of boxes()) expect(each).toHaveAttribute('aria-invalid', 'true');
    // The boxes are new nodes, and their description still reaches the bar.
    expect(describedBy()).toHaveTextContent('Incorrect PIN. Please try again.');

    // The next digit lands in box 1, and the emptied boxes sent nothing by themselves.
    await user.keyboard('1');
    expect(box(1)).toHaveValue('1');
    expect(checks).toBe(1);

    // Escape is heard again, and focus goes back to what opened the prompt.
    await user.keyboard('{Escape}');
    await waitFor(() => expect(screen.queryByText("Verify it's you")).not.toBeInTheDocument());
    await expect(outcome).resolves.toBe('cancelled');
    expect(opener).toHaveFocus();
  });

  it.each([
    [
      'the service is unavailable',
      () => serviceUnavailable({ via: 'bff', instance: '/bff/auth/verify-pin' }),
      'The service is temporarily unavailable. Please try again later.',
    ],
    [
      'the connection is lost',
      () => HttpResponse.error(),
      "Couldn't verify right now — check your connection and try again.",
    ],
    [
      'the check fails otherwise',
      () => problem({ status: 500, errorCode: 'INTERNAL_ERROR', detail: 'boom' }),
      "Couldn't verify right now — please try again.",
    ],
  ])(
    'puts the caret back in box 1 when %s, and leaves the boxes neutral',
    async (_case, answer, words) => {
      server.use(http.post('*/bff/auth/verify-pin', answer));
      const user = userEvent.setup();
      openPrompt();
      await screen.findByText("Verify it's you");

      await enterPin(user, '123456');
      const bar = (await screen.findByText(words)).closest<HTMLElement>('[role="alert"]');

      await waitFor(() => expect(box(1)).toHaveFocus());
      // Nothing said the PIN was wrong, so nothing marks it wrong.
      for (const each of boxes()) expect(each).not.toHaveAttribute('aria-invalid');
      // The bar takes the dialog's width: jsdom draws nothing, so its rule is what can be read
      // here. That the sentence wraps is measured in Chromium only: jsdom cascades Griffel's sheets
      // in the order they were made, so the bar's base `nowrap` wins over its multiline `normal`.
      expect(bar).not.toBeNull();
      expect(getComputedStyle(bar!).alignSelf).toBe('stretch');
    },
  );

  it('puts focus on Cancel after the lock, and Escape then closes the prompt', async () => {
    // Two wrong PINs already counted: the mock's own answer to the third is the lock.
    mockState.pinAttempts = 2;
    const user = userEvent.setup();
    const { opener, outcome } = openPrompt();
    await screen.findByText("Verify it's you");
    // At 320 px the row of boxes is wider than the content, and typing into box 6 scrolls the
    // content 27 px sideways (measured in Chromium). jsdom scrolls nothing, so that is set here.
    const content = document.querySelector<HTMLElement>('.fui-DialogContent')!;
    let scrolled = 27;
    Object.defineProperty(content, 'scrollLeft', {
      configurable: true,
      get: () => scrolled,
      set: (left: number) => {
        scrolled = left;
      },
    });

    await enterPin(user, '000000');
    await screen.findByText('Too many incorrect PIN attempts.');
    // The lock leaves no box to type in, so the content goes back to its start, and the bar and
    // its icon with it.
    expect(scrolled).toBe(0);

    await waitFor(() => expect(screen.getByRole('button', { name: 'Cancel' })).toHaveFocus());
    // The block takes the dialog's width, and only its bar reads from the start edge: the
    // countdown under it keeps the centre it had, so at 320 px it is not pushed off the left.
    const lock = describedBy()!;
    expect(getComputedStyle(lock).alignSelf).toBe('stretch');
    expect(getComputedStyle(lock).textAlign).not.toBe('start');
    const lockBar = screen
      .getByText('Too many incorrect PIN attempts.')
      .closest<HTMLElement>('[role="alert"]');
    expect(getComputedStyle(lockBar!).textAlign).toBe('start');

    await user.keyboard('{Escape}');
    await waitFor(() => expect(screen.queryByText("Verify it's you")).not.toBeInTheDocument());
    await expect(outcome).resolves.toBe('cancelled');
    expect(opener).toHaveFocus();
  });
});
