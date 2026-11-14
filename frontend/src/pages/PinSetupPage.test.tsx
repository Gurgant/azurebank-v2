import { Route, Routes } from 'react-router-dom';
import { act, cleanup, fireEvent, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { getResponse, http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { PinInput } from '../components/PinInput';
import { enableDemoMode } from '../test/demoMode';
import { makeTestStore, renderWithProviders, type TestStore } from '../test/renderWithProviders';
import { PinSetupPage } from './PinSetupPage';
import { MOCK_PASSWORD, MOCK_USER, mockState, seedMockSession } from '../mocks/state';
import { handlers } from '../mocks/handlers';
import { server } from '../mocks/server';
import { problem, serviceUnavailable } from '../mocks/problem';
import {
  COPY,
  advanceUntil,
  emulateFocusFixup,
  fakeClockUser,
  hintRegion,
  hintShownAt,
  installFakeClock,
  loseAnswerAfterCommit,
  never,
} from '../test/outage';
import { CONNECTION_FAILED } from '../api/problemMessages';
import { selectCurrentUser } from '../features/auth/authSlice';

/**
 * PR-10 — the PIN onboarding wizard (enter → confirm → done). Pins: the confirm-must-match
 * guard, the success → returnTo hand-back, and the "already has a PIN → bounce" redirect.
 */

function renderPinSetup(
  store: TestStore = makeTestStore(),
  entry = '/pin-setup?returnTo=/accounts',
) {
  return renderWithProviders(
    <Routes>
      <Route path="/pin-setup" element={<PinSetupPage />} />
      <Route path="/accounts" element={<div>ACCOUNTS PAGE</div>} />
      <Route path="/dashboard" element={<div>DASHBOARD PAGE</div>} />
    </Routes>,
    { store, routerEntries: [entry] },
  );
}

function storeWithUser(hasPin: boolean) {
  const store = makeTestStore();
  store.dispatch({
    type: 'api/executeQuery/fulfilled',
    meta: { arg: { endpointName: 'getMe' } },
    payload: {
      user: {
        id: '019f7b3f-0000-7000-8000-0000000000a1',
        azureTag: 'demo_user',
        email: 'demo@azurebank.dev',
        firstName: 'Demo',
        lastName: 'User',
        hasPin,
      },
    },
  });
  return store;
}

async function pasteDigits(pin: string) {
  await userEvent.click(screen.getByLabelText('Digit 1 of 6'));
  await userEvent.paste(pin);
}

/*
  A session, seeded, because the step-up routes now demand one.

  The mock used to answer /bff/auth/verify-pin for a caller with NO session at all, so these
  tests never had to establish one and quietly exercised a state the product cannot reach: the
  real BFF reads the session first and answers 401. Aligning the mock (contract gate) made that
  visible. Seeding is the faithful fix — a user reaching a PIN prompt is, by construction,
  already signed in.
*/
beforeEach(() => {
  /*
    Seeded WITHOUT a PIN, which is the only state this page is reachable in: PinSetupPage bounces a
    user whose `hasPin` is already true. The default seed (MOCK_USER, hasPin: true) described a user
    who could never arrive here, and the server-side mock now enforces the difference — changing a
    PIN requires the current one, enrolling does not.
  */
  seedMockSession({ ...MOCK_USER, hasPin: false });
});

describe('PIN setup wizard (PR-10)', () => {
  /** The account password now gates the commit (T7/#201) — typed on the confirm step. */
  async function typePassword(value = MOCK_PASSWORD) {
    await userEvent.click(screen.getByLabelText('Account password'));
    await userEvent.paste(value);
  }

  it('enter → confirm → success, then hands back to returnTo', async () => {
    renderPinSetup();

    expect(screen.getByText('Create your PIN')).toBeInTheDocument();
    await pasteDigits('123456');
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));

    expect(await screen.findByText('Confirm your PIN')).toBeInTheDocument();
    // Password FIRST: the auto-submit on the sixth digit is deliberately suppressed until one is
    // present, so that a wizard cannot spend a refusal to tell the user about a field it can see
    // is empty.
    await typePassword();
    await pasteDigits('123456'); // onComplete auto-submits once the password is in hand

    expect(await screen.findByText('PIN Setup Complete!')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
    expect(await screen.findByText('ACCOUNTS PAGE')).toBeInTheDocument();
  });

  it('says everything the PIN confirms, on the first step and on the success screen', async () => {
    /*
      The sentences are typed out, not built from the product's list, so that a change to what the
      PIN confirms fails here until the words on screen are read again. The first step's apostrophe
      is the typographic one and the success screen's the ASCII one, as each is written in the page.
    */
    renderPinSetup();
    expect(
      screen.getByText(
        'Choose a 6-digit PIN. You’ll enter it to confirm withdrawals, transfers and account closures, and to show full account numbers.',
      ),
    ).toBeInTheDocument();

    await pasteDigits('123456');
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
    await screen.findByText('Confirm your PIN');
    await typePassword();
    await pasteDigits('123456');

    expect(
      await screen.findByText(
        "Your 6-digit PIN is set. You'll use it to confirm withdrawals, transfers and account closures, and to show full account numbers.",
      ),
    ).toBeInTheDocument();
  });

  it('will not commit a PIN until the account password is supplied', async () => {
    /*
      The client half of T7. The server refuses a password-less enrolment with 422
      PASSWORD_REQUIRED — measured — but a form that lets the user press the button and then
      reports a refusal has taught them nothing they could not have been told for free.
    */
    renderPinSetup();
    await pasteDigits('123456');
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
    await screen.findByText('Confirm your PIN');

    await pasteDigits('123456');

    // Six digits in, PINs matching, and still nothing has been sent.
    expect(screen.queryByText('PIN Setup Complete!')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Set PIN' })).toBeDisabled();

    await typePassword();
    expect(screen.getByRole('button', { name: 'Set PIN' })).toBeEnabled();
  });

  it('says which credential was wrong when the password is rejected', async () => {
    // The PIN boxes are fine; asking the user to retype six digits because a password was
    // mistyped is the kind of friction that produces shorter passwords.
    renderPinSetup();
    await pasteDigits('123456');
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
    await screen.findByText('Confirm your PIN');
    await typePassword('WrongPassword1!');
    await pasteDigits('123456');

    expect(
      await screen.findByText(
        'Incorrect password. After 5 failed attempts, sign-in is paused for 15 minutes.',
      ),
    ).toBeInTheDocument();
    expect(screen.queryByText('PIN Setup Complete!')).not.toBeInTheDocument();
  });

  it('rejects a mismatched confirmation and keeps the user on the confirm step', async () => {
    renderPinSetup();
    await pasteDigits('123456');
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
    await screen.findByText('Confirm your PIN');
    await typePassword();
    await pasteDigits('654321');

    expect(await screen.findByText('PINs do not match. Please try again.')).toBeInTheDocument();
    expect(screen.getByText('Confirm your PIN')).toBeInTheDocument();
    expect(screen.queryByText('PIN Setup Complete!')).not.toBeInTheDocument();
  });

  it('each step and the finished view has one heading, its title', async () => {
    const headings = () => screen.getAllByRole('heading').map((h) => [h.tagName, h.textContent]);
    renderPinSetup();
    const choosing = headings();

    await pasteDigits('123456');
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
    await screen.findByText('Confirm your PIN');
    const confirming = headings();

    await typePassword();
    await pasteDigits('123456');
    await screen.findByText('PIN Setup Complete!');

    expect({ choosing, confirming, finished: headings() }).toEqual({
      choosing: [['H1', 'Create your PIN']],
      confirming: [['H1', 'Confirm your PIN']],
      finished: [['H1', 'PIN Setup Complete!']],
    });
  });

  it('puts focus on the finished title when the PIN is set from the keyboard', async () => {
    /*
      The sixth digit sends, the boxes are disabled while the save is out, and the confirm step
      goes with them: the finished view appears with nothing focused, and a view that mounts with
      its words in it is not read. Its title takes that lost focus.
    */
    renderPinSetup();
    await userEvent.click(screen.getByLabelText('Digit 1 of 6'));
    await userEvent.keyboard('123456');
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
    await screen.findByText('Confirm your PIN');
    await typePassword();
    await userEvent.click(screen.getByLabelText('Digit 1 of 6'));
    await userEvent.keyboard('123456');

    const title = await screen.findByRole('heading', { name: 'PIN Setup Complete!' });
    await waitFor(() => expect(title).toHaveFocus());
    // It takes focus without joining the Tab order.
    expect(title).toHaveAttribute('tabindex', '-1');
  });

  it('bounces a user who already has a PIN straight to returnTo', async () => {
    renderPinSetup(storeWithUser(true));

    expect(await screen.findByText('ACCOUNTS PAGE')).toBeInTheDocument();
    expect(screen.queryByText('Create your PIN')).not.toBeInTheDocument();
  });

  it('lets the user skip setup for now', async () => {
    renderPinSetup();
    await userEvent.click(screen.getByRole('button', { name: 'Skip for now' }));
    expect(await screen.findByText('ACCOUNTS PAGE')).toBeInTheDocument();
  });

  it('in the demo, choosing a PIN shows no hint', async () => {
    /*
      On the demo the page prints the PIN every demo copy starts with under boxes that ask for a
      PIN the visitor already has. Both steps of this page take a PIN that is being chosen: the
      starting digits under them would read as the PIN to choose.

      The sentence is typed out, not imported from the product.
    */
    const hint = 'Demo PIN: 123456, unless you changed it.';
    const hints = () => screen.queryAllByText(hint).length;
    const describedBy = (name: string) =>
      screen.getByRole('group', { name }).getAttribute('aria-describedby');
    enableDemoMode();

    // Boxes that ask for a PIN the visitor has, on this same page of the demo: seen first, so that
    // the zeros below are counts of something that is drawn.
    const asked = renderWithProviders(<PinInput value="" onChange={() => {}} />);
    const underBoxesThatAskForOne = hints();
    asked.unmount();

    renderPinSetup();
    const choosing = { hints: hints(), describedBy: describedBy('Create your PIN') };
    await pasteDigits('123456');
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
    await screen.findByText('Confirm your PIN');
    const confirming = { hints: hints(), describedBy: describedBy('Confirm your PIN') };

    expect({ underBoxesThatAskForOne, choosing, confirming }).toEqual({
      underBoxesThatAskForOne: 1,
      choosing: { hints: 0, describedBy: null },
      confirming: { hints: 0, describedBy: null },
    });
  });
});

describe('PIN setup during an outage', () => {
  // Unmount first, then restore the clock: a render still mounted on a fake clock leaks into the
  // next test.
  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  /** The confirm step with the password in hand: the next six digits send. */
  async function toTheConfirmation(store?: TestStore) {
    renderPinSetup(store);
    await pasteDigits('123456');
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
    await screen.findByText('Confirm your PIN');
    await userEvent.click(screen.getByLabelText('Account password'));
    await userEvent.paste(MOCK_PASSWORD);
  }

  it('a 503 says the PIN may have been saved', async () => {
    server.use(
      http.post('*/bff/auth/set-pin', () =>
        serviceUnavailable({ via: 'bff', instance: '/bff/auth/set-pin' }),
      ),
    );
    await toTheConfirmation();
    await pasteDigits('123456');

    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent(COPY.saveUnknown));
    expect(screen.queryByText('PIN Setup Complete!')).not.toBeInTheDocument();
  });

  it('a 503 empties the boxes: a PIN that may be saved is not one click away', async () => {
    server.use(
      http.post('*/bff/auth/set-pin', () =>
        serviceUnavailable({ via: 'bff', instance: '/bff/auth/set-pin' }),
      ),
    );
    await toTheConfirmation();
    await pasteDigits('123456');
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent(COPY.saveUnknown));

    const boxes = Array.from({ length: 6 }, (_, index) =>
      screen.getByLabelText(`Digit ${index + 1} of 6`),
    );
    expect({
      digits: boxes.map((box) => (box as HTMLInputElement).value).join(''),
      invalid: boxes.map((box) => box.getAttribute('aria-invalid')),
      setPin: (screen.getByRole('button', { name: 'Set PIN' }) as HTMLButtonElement).disabled,
    }).toEqual({ digits: '', invalid: Array(6).fill('true'), setPin: true });
  });

  it('no answer in 65 s says the same, not "check your connection"', async () => {
    let sentAt = 0;
    server.use(
      http.post('*/bff/auth/set-pin', () => {
        sentAt = Date.now();
        return never();
      }),
    );
    await toTheConfirmation();
    installFakeClock();
    const user = fakeClockUser();
    await user.click(screen.getByLabelText('Digit 1 of 6'));
    await user.paste('123456');
    await waitFor(() => expect(sentAt).toBeGreaterThan(0));

    await advanceUntil(sentAt, 65_100);

    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent(COPY.saveUnknown));
    expect(screen.queryByText(CONNECTION_FAILED)).not.toBeInTheDocument();
  });

  it('says it is slow while the PIN is being saved, under the button, with no way to stop it', async () => {
    let sentAt = 0;
    server.use(
      http.post('*/bff/auth/set-pin', () => {
        sentAt = Date.now();
        return never();
      }),
    );
    await toTheConfirmation();
    installFakeClock();
    const user = fakeClockUser();
    const enteredAt = Date.now();
    await user.click(screen.getByLabelText('Digit 1 of 6'));
    await user.paste('123456');
    await waitFor(() => expect(sentAt).toBeGreaterThan(0));
    const shownAt = await hintShownAt();

    await advanceUntil(enteredAt, 4_900);
    expect(screen.queryByText(COPY.slow)).not.toBeInTheDocument();
    await advanceUntil(shownAt, 5_000);
    hintRegion(COPY.slow);
    await advanceUntil(shownAt, 20_000);
    const region = hintRegion(COPY.stillTrying);

    expect(screen.queryByRole('button', { name: COPY.stopWaiting })).not.toBeInTheDocument();
    const boxes = screen.getByRole('group', { name: 'Confirm your PIN' });
    expect(boxes.compareDocumentPosition(region) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('a PIN saved while the answer was lost is found, and the page says it is set', async () => {
    loseAnswerAfterCommit('/bff/auth/set-pin', () =>
      serviceUnavailable({ via: 'bff', instance: '/bff/auth/set-pin' }),
    );
    await toTheConfirmation();
    await pasteDigits('123456');

    const heading = await screen.findByRole('heading', { name: 'PIN Setup Complete!' });
    await waitFor(() => expect(heading).toHaveFocus());
  });

  it('a connection that drops after the PIN is saved is found, and the page says it is set', async () => {
    loseAnswerAfterCommit('/bff/auth/set-pin', () => HttpResponse.error());
    await toTheConfirmation();
    await pasteDigits('123456');

    const heading = await screen.findByRole('heading', { name: 'PIN Setup Complete!' });
    await waitFor(() => expect(heading).toHaveFocus());
  });

  it('a connection that drops says the PIN may have been saved, not "check your connection"', async () => {
    server.use(http.post('*/bff/auth/set-pin', () => HttpResponse.error()));
    await toTheConfirmation();
    await pasteDigits('123456');

    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent(COPY.saveUnknown));
    expect(screen.queryByText(CONNECTION_FAILED)).not.toBeInTheDocument();
  });

  it('when the session cannot be read either, typing the PIN again finds it', async () => {
    loseAnswerAfterCommit('/bff/auth/set-pin', () =>
      serviceUnavailable({ via: 'bff', instance: '/bff/auth/set-pin' }),
    );
    server.use(
      http.get('*/bff/auth/me', () => serviceUnavailable({ via: 'bff', instance: '/bff/auth/me' })),
    );
    await toTheConfirmation();
    await pasteDigits('123456');

    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent(COPY.saveUnknown));
    expect(screen.queryByRole('heading', { name: 'PIN Setup Complete!' })).not.toBeInTheDocument();

    server.resetHandlers();
    await pasteDigits('123456');

    expect(await screen.findByRole('heading', { name: 'PIN Setup Complete!' })).toBeInTheDocument();
    expect(screen.queryByText('The current PIN is required to change it.')).toBeNull();
  });

  /**
   * The API saves the first PIN and its answer is lost before the BFF, whose session therefore
   * still says there is none: the page reads a 503, and a session with no PIN.
   */
  function loseAnswerBeforeTheBff() {
    loseAnswerAfterCommit('/bff/auth/set-pin', () => {
      mockState.session!.hasPin = false;
      mockState.pinUnknownToSession = true;
      return serviceUnavailable({ via: 'bff', instance: '/bff/auth/set-pin' });
    });
  }

  /** "Back" to the first step, then `pin` chosen and confirmed: the kept password sends it. */
  async function chooseAgain(pin: string) {
    await userEvent.click(screen.getByRole('button', { name: 'Back' }));
    await screen.findByText('Create your PIN');
    await pasteDigits(pin);
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
    await screen.findByText('Confirm your PIN');
    await pasteDigits(pin);
  }

  it('other digits chosen after a lost answer are not said to be set: the first PIN is kept', async () => {
    loseAnswerBeforeTheBff();
    const store = makeTestStore();
    await toTheConfirmation(store);
    await pasteDigits('123456');
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent(COPY.saveUnknown));

    await chooseAgain('654321');

    const sentence = await screen.findByText(
      'Your account already has a PIN: the one you chose first. To change it, go to Settings.',
    );
    await waitFor(() => expect(sentence).toHaveFocus());
    expect(sentence).toHaveAttribute('tabindex', '-1');
    // The session read after the refusal has landed, and says there is a PIN: still not "set".
    await waitFor(() => expect(selectCurrentUser(store.getState())?.hasPin).toBe(true));
    expect(screen.queryByRole('heading', { name: 'PIN Setup Complete!' })).not.toBeInTheDocument();
    expect(sentence).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
    expect(await screen.findByText('ACCOUNTS PAGE')).toBeInTheDocument();
  });

  it('other digits refused after a lost answer keep the first PIN when the session cannot be read', async () => {
    loseAnswerBeforeTheBff();
    const store = makeTestStore();
    await toTheConfirmation(store);
    await pasteDigits('123456');
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent(COPY.saveUnknown));
    server.use(
      http.get('*/bff/auth/me', () => serviceUnavailable({ via: 'bff', instance: '/bff/auth/me' })),
    );

    await chooseAgain('654321');

    const sentence = await screen.findByText(
      'Your account already has a PIN: the one you chose first. To change it, go to Settings.',
    );
    await waitFor(() => expect(sentence).toHaveFocus());
    // The refusal alone found the PIN: the session still says there is none.
    expect(selectCurrentUser(store.getState())?.hasPin).not.toBe(true);
  });

  it('the same digits chosen again after a lost answer are said to be set', async () => {
    loseAnswerBeforeTheBff();
    await toTheConfirmation();
    await pasteDigits('123456');
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent(COPY.saveUnknown));

    await chooseAgain('123456');

    const heading = await screen.findByRole('heading', { name: 'PIN Setup Complete!' });
    await waitFor(() => expect(heading).toHaveFocus());
    expect(screen.queryByText(/the one you chose first/)).not.toBeInTheDocument();
  });

  it('the same digits refused after a lost answer are said to be set when the session cannot be read', async () => {
    loseAnswerBeforeTheBff();
    const store = makeTestStore();
    await toTheConfirmation(store);
    await pasteDigits('123456');
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent(COPY.saveUnknown));
    server.use(
      http.get('*/bff/auth/me', () => serviceUnavailable({ via: 'bff', instance: '/bff/auth/me' })),
    );

    await chooseAgain('123456');

    const heading = await screen.findByRole('heading', { name: 'PIN Setup Complete!' });
    await waitFor(() => expect(heading).toHaveFocus());
    expect(selectCurrentUser(store.getState())?.hasPin).not.toBe(true);
  });

  /**
   * The first save is answered 503 before it reaches the API, so nothing is saved; the saves after
   * it go on to the handlers registered before this one.
   */
  function loseTheFirstSaveUnsaved() {
    let saves = 0;
    server.use(
      http.post('*/bff/auth/set-pin', () => {
        saves += 1;
        return saves === 1
          ? serviceUnavailable({ via: 'bff', instance: '/bff/auth/set-pin' })
          : undefined;
      }),
    );
  }

  it.each(['654321', '123456'])(
    'after two lost answers for different digits, %s refused names no PIN',
    async (digits) => {
      // 123456 is never saved; 654321 is, and the BFF does not learn of it. Either digits could be
      // the PIN on file as far as the page can tell, and neither is named or said to be set.
      loseAnswerBeforeTheBff();
      loseTheFirstSaveUnsaved();
      await toTheConfirmation();
      await pasteDigits('123456');
      await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent(COPY.saveUnknown));
      await chooseAgain('654321');
      await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent(COPY.saveUnknown));

      await chooseAgain(digits);

      const sentence = await screen.findByText(
        'Your account already has a PIN. To change it, go to Settings.',
      );
      await waitFor(() => expect(sentence).toHaveFocus());
      expect(sentence).toHaveAttribute('tabindex', '-1');
      expect(screen.queryByText(/the one you chose first/)).not.toBeInTheDocument();
      expect(
        screen.queryByRole('heading', { name: 'PIN Setup Complete!' }),
      ).not.toBeInTheDocument();
      expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    },
  );

  it('after two lost answers for different digits, a session read alone finds a PIN and names none', async () => {
    // 654321 is saved and the BFF learns of it before the answer is lost: the read after it says
    // there is a PIN, and no save is refused.
    loseAnswerAfterCommit('/bff/auth/set-pin', () =>
      serviceUnavailable({ via: 'bff', instance: '/bff/auth/set-pin' }),
    );
    loseTheFirstSaveUnsaved();
    await toTheConfirmation();
    await pasteDigits('123456');
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent(COPY.saveUnknown));

    await chooseAgain('654321');

    const sentence = await screen.findByText(
      'Your account already has a PIN. To change it, go to Settings.',
    );
    await waitFor(() => expect(sentence).toHaveFocus());
    expect(screen.queryByText(/the one you chose first/)).not.toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'PIN Setup Complete!' })).not.toBeInTheDocument();
  });

  it.each([
    ['saved, and its answer arrives', 'PIN Setup Complete!'],
    [
      'saved, and its answer is lost',
      'Your account already has a PIN. To change it, go to Settings.',
    ],
  ])(
    'a session read that lands while other digits are being saved waits for that save: %s',
    async (outcome, said) => {
      // The first save, 123456, is answered 503 before it reaches the API: nothing is saved. The
      // read of the session it starts is held. The second save, 654321, is saved, and its answer
      // is held. The held read is let go first, and says there is a PIN: the one 654321 put there.
      let saves = 0;
      let answerTheSave!: () => void;
      const saveAnswered = new Promise<void>((resolve) => (answerTheSave = resolve));
      server.use(
        http.post('*/bff/auth/set-pin', async ({ request }) => {
          saves += 1;
          if (saves === 1) return serviceUnavailable({ via: 'bff', instance: '/bff/auth/set-pin' });
          const real = await getResponse(handlers, request.clone());
          await saveAnswered;
          return outcome === 'saved, and its answer arrives'
            ? real
            : serviceUnavailable({ via: 'bff', instance: '/bff/auth/set-pin' });
        }),
      );
      let reads = 0;
      let answerTheRead!: () => void;
      const readAnswered = new Promise<void>((resolve) => (answerTheRead = resolve));
      server.use(
        http.get('*/bff/auth/me', async () => {
          reads += 1;
          if (reads === 1) await readAnswered;
          return undefined;
        }),
      );
      // Every text the page shows, kept: each node it adds, and each value a text node held, as
      // React reuses an element of the same type and changes only its text. A view that mounts
      // and goes again within one task is still seen.
      const shown: string[] = [];
      const watch = new MutationObserver((records) => {
        for (const record of records) {
          if (record.type === 'characterData') {
            shown.push(record.oldValue ?? '', record.target.textContent ?? '');
          } else {
            record.addedNodes.forEach((node) => shown.push(node.textContent ?? ''));
          }
        }
      });
      watch.observe(document.body, {
        childList: true,
        subtree: true,
        characterData: true,
        characterDataOldValue: true,
      });

      try {
        const store = makeTestStore();
        await toTheConfirmation(store);
        await pasteDigits('123456');
        await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent(COPY.saveUnknown));
        await waitFor(() => expect(reads).toBe(1));

        await chooseAgain('654321');
        await waitFor(() => expect(saves).toBe(2));
        await waitFor(() => expect(mockState.session?.hasPin).toBe(true));

        answerTheRead();
        await waitFor(() => expect(selectCurrentUser(store.getState())?.hasPin).toBe(true));
        // The store tells React on the next frame: past it, the page has drawn the read.
        await act(() => new Promise((resolve) => setTimeout(resolve, 50)));
        // The read says there is a PIN, and the save that put it there has not answered: the page
        // names no PIN yet.
        expect(screen.getByText('Confirm your PIN')).toBeInTheDocument();
        expect(screen.queryByText(/already has a PIN/)).not.toBeInTheDocument();

        answerTheSave();
        const view =
          outcome === 'saved, and its answer arrives'
            ? await screen.findByRole('heading', { name: said })
            : await screen.findByText(said);
        await waitFor(() => expect(view).toHaveFocus());
        // The watch saw the view that ends the page mount, and never the one that names the
        // first PIN.
        expect(shown.some((text) => text.includes(said))).toBe(true);
        expect(shown.some((text) => text.includes('the one you chose first'))).toBe(false);
      } finally {
        watch.disconnect();
      }
    },
  );

  /**
   * Answers with no verdict on the PIN that come after the BFF's own answer, so the BFF's session
   * knows of the PIN the API saved: a page from something in front of the BFF, and a 200 whose
   * body was cut off.
   */
  const afterTheBffKnows: [string, () => Response][] = [
    ['a page from in front of the BFF', () => new HttpResponse('crash page', { status: 500 })],
    [
      'a 200 cut off mid-body',
      () =>
        new HttpResponse('{"message":"PIN se', {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
    ],
  ];

  /**
   * Answers with no verdict on the PIN from the BFF itself: a 500 it forwards from the API, and its
   * own 502. The BFF learns of a PIN only from the API's 200 or from a refusal because the account
   * has one, so after either of these its session still says there is none.
   */
  const fromTheBff: [string, () => Response][] = [
    ['a 500', () => problem({ status: 500, detail: 'An unexpected error occurred.' })],
    [
      "the BFF's 502",
      () => problem({ status: 502, detail: 'Upstream service returned an invalid response' }),
    ],
  ];

  /** The API saves the PIN, and the BFF answers `answer` without learning of it. */
  function saveUnknownToTheBff(answer: () => Response) {
    loseAnswerAfterCommit('/bff/auth/set-pin', () => {
      mockState.session!.hasPin = false;
      mockState.pinUnknownToSession = true;
      return answer();
    });
  }

  it.each(afterTheBffKnows)(
    'a PIN saved behind %s is found, and the page says it is set',
    async (_, answer) => {
      loseAnswerAfterCommit('/bff/auth/set-pin', answer);
      await toTheConfirmation();
      await pasteDigits('123456');

      const heading = await screen.findByRole('heading', { name: 'PIN Setup Complete!' });
      await waitFor(() => expect(heading).toHaveFocus());
    },
  );

  it.each(afterTheBffKnows)(
    'when the session cannot be read either, typing the PIN again after %s finds it',
    async (_, answer) => {
      loseAnswerAfterCommit('/bff/auth/set-pin', answer);
      server.use(
        http.get('*/bff/auth/me', () =>
          serviceUnavailable({ via: 'bff', instance: '/bff/auth/me' }),
        ),
      );
      await toTheConfirmation();
      await pasteDigits('123456');

      await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent(COPY.saveUnknown));
      expect(
        screen.queryByRole('heading', { name: 'PIN Setup Complete!' }),
      ).not.toBeInTheDocument();

      server.resetHandlers();
      await pasteDigits('123456');

      expect(
        await screen.findByRole('heading', { name: 'PIN Setup Complete!' }),
      ).toBeInTheDocument();
    },
  );

  it.each(fromTheBff)(
    'a PIN saved behind %s, which the session does not learn, is found when typed again',
    async (_, answer) => {
      saveUnknownToTheBff(answer);
      await toTheConfirmation();
      await pasteDigits('123456');

      await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent(COPY.saveUnknown));
      expect(
        screen.queryByRole('heading', { name: 'PIN Setup Complete!' }),
      ).not.toBeInTheDocument();

      await pasteDigits('123456');

      const heading = await screen.findByRole('heading', { name: 'PIN Setup Complete!' });
      await waitFor(() => expect(heading).toHaveFocus());
    },
  );

  it.each(afterTheBffKnows)(
    'after a lost answer, other digits saved behind %s name no PIN',
    async (_, answer) => {
      // 123456 is answered 503 before it reaches the API. 654321 is saved and answered with no
      // verdict. The read the first save started is held: let go after the second save, it says
      // there is a PIN, and a page that had not kept the second save's digits named 123456.
      let saves = 0;
      server.use(
        http.post('*/bff/auth/set-pin', async ({ request }) => {
          saves += 1;
          if (saves === 1) return serviceUnavailable({ via: 'bff', instance: '/bff/auth/set-pin' });
          await getResponse(handlers, request.clone());
          return answer();
        }),
      );
      let reads = 0;
      let answerTheRead!: () => void;
      const readAnswered = new Promise<void>((resolve) => (answerTheRead = resolve));
      server.use(
        http.get('*/bff/auth/me', async () => {
          reads += 1;
          if (reads === 1) await readAnswered;
          return undefined;
        }),
      );
      await toTheConfirmation();
      await pasteDigits('123456');
      await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent(COPY.saveUnknown));

      await chooseAgain('654321');
      await waitFor(() => expect(saves).toBe(2));
      await waitFor(() => expect(mockState.session?.hasPin).toBe(true));
      answerTheRead();

      const sentence = await screen.findByText(
        'Your account already has a PIN. To change it, go to Settings.',
      );
      await waitFor(() => expect(sentence).toHaveFocus());
      expect(screen.queryByText(/the one you chose first/)).not.toBeInTheDocument();
    },
  );

  it.each(fromTheBff)(
    'after a lost answer, other digits saved behind %s name no PIN when typed again',
    async (_, answer) => {
      // 123456 is answered 503 before it reaches the API. 654321 is saved by the API, and the BFF
      // answers with no verdict and does not learn of it. Typed again, 654321 is refused because
      // the account has a PIN: a page that had not kept its digits named 123456.
      let saves = 0;
      server.use(
        http.post('*/bff/auth/set-pin', async ({ request }) => {
          saves += 1;
          if (saves === 1) return serviceUnavailable({ via: 'bff', instance: '/bff/auth/set-pin' });
          if (saves > 2) return undefined;
          await getResponse(handlers, request.clone());
          mockState.session!.hasPin = false;
          mockState.pinUnknownToSession = true;
          return answer();
        }),
      );
      await toTheConfirmation();
      await pasteDigits('123456');
      await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent(COPY.saveUnknown));

      await chooseAgain('654321');
      await waitFor(() => expect(saves).toBe(2));
      await screen.findByRole('alert');
      await pasteDigits('654321');

      const sentence = await screen.findByText(
        'Your account already has a PIN. To change it, go to Settings.',
      );
      await waitFor(() => expect(sentence).toHaveFocus());
      expect(screen.queryByText(/the one you chose first/)).not.toBeInTheDocument();
      expect(saves).toBe(3);
    },
  );

  it('after a refusal, digits saved behind a lost answer are not called the ones chosen first', async () => {
    // 123456 is refused by the BFF's limiter: nothing is saved. 222222 is saved by the API, and
    // the BFF's answer is lost without the session learning of it. 333333 is then refused because
    // the account has a PIN: that PIN is 222222, which the visitor did not choose first.
    let saves = 0;
    server.use(
      http.post('*/bff/auth/set-pin', async ({ request }) => {
        saves += 1;
        if (saves === 1) {
          return problem({
            status: 429,
            errorCode: 'RATE_LIMIT_EXCEEDED',
            detail: 'Too many requests. Please retry later.',
          });
        }
        if (saves > 2) return undefined;
        await getResponse(handlers, request.clone());
        mockState.session!.hasPin = false;
        mockState.pinUnknownToSession = true;
        return serviceUnavailable({ via: 'bff', instance: '/bff/auth/set-pin' });
      }),
    );
    await toTheConfirmation();
    await pasteDigits('123456');
    await screen.findByText('Too many requests. Please retry later.');

    await chooseAgain('222222');
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent(COPY.saveUnknown));
    await chooseAgain('333333');

    const sentence = await screen.findByText(
      'Your account already has a PIN. To change it, go to Settings.',
    );
    await waitFor(() => expect(sentence).toHaveFocus());
    expect(screen.queryByText(/the one you chose first/)).not.toBeInTheDocument();
    expect(saves).toBe(3);
  });

  it('after a 503 focus goes back to the first box', async () => {
    const stop = emulateFocusFixup();
    try {
      server.use(
        http.post('*/bff/auth/set-pin', () =>
          serviceUnavailable({ via: 'bff', instance: '/bff/auth/set-pin' }),
        ),
      );
      await toTheConfirmation();
      await pasteDigits('123456');

      await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent(COPY.saveUnknown));
      await waitFor(() => expect(screen.getByLabelText('Digit 1 of 6')).toHaveFocus());
    } finally {
      stop();
    }
  });
});

/*
  What the page says for each refusal of the save, and the two buttons under the boxes. One
  refusal had a test, the wrong password; the others were sentences no test had read, and a
  mapping that sent "your account is locked" to the wrong code would have shipped green.

  Where the mock answers a case by itself the test lets it. The others are answered here in the
  API's own envelope (`problem`), with the status and code of the row quoted beside each.
*/
describe('PIN setup: what each refusal says, and the buttons under the boxes', () => {
  /** The confirm step with the first PIN chosen: what is left is the password and the PIN again. */
  async function toTheConfirmation() {
    const view = renderPinSetup();
    await pasteDigits('123456');
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
    await screen.findByText('Confirm your PIN');
    return view;
  }

  async function typePassword(value = MOCK_PASSWORD) {
    await userEvent.click(screen.getByLabelText('Account password'));
    await userEvent.paste(value);
  }

  /** Every save sent from now on, as its body; the handler that answers is left alone. */
  function watchSaves() {
    const bodies: unknown[] = [];
    server.use(
      http.post('*/bff/auth/set-pin', async ({ request }) => {
        bodies.push(await request.clone().json());
      }),
    );
    return bodies;
  }

  /** The save is refused as `refusal` answers, and the page's one alert is returned. */
  async function refusedWith(refusal: () => Response) {
    server.use(http.post('*/bff/auth/set-pin', refusal));
    await toTheConfirmation();
    await typePassword();
    await pasteDigits('123456');
    return screen.findByRole('alert');
  }

  it('"Set PIN" saves when the password is typed after the PIN, and sends both', async () => {
    const saves = watchSaves();
    await toTheConfirmation();
    // The PIN first: with no password yet, the sixth digit sends nothing.
    await pasteDigits('123456');
    await typePassword();
    expect(saves).toEqual([]);

    await userEvent.click(screen.getByRole('button', { name: 'Set PIN' }));

    expect(await screen.findByText('PIN Setup Complete!')).toBeInTheDocument();
    expect(saves).toEqual([{ pin: '123456', password: MOCK_PASSWORD }]);
  });

  it('"Back" on the confirmation returns to choosing the PIN, and takes the refusal with it', async () => {
    const saves = watchSaves();
    await toTheConfirmation();
    await typePassword();
    // Five digits: on the confirmation, with nothing sent.
    await pasteDigits('12345');

    await userEvent.click(screen.getByRole('button', { name: 'Back' }));
    expect(await screen.findByText('Create your PIN')).toBeInTheDocument();
    expect(screen.queryByLabelText('Account password')).not.toBeInTheDocument();

    // The confirmation starts empty the next time: the five digits did not wait there.
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
    await screen.findByText('Confirm your PIN');
    expect(screen.getByLabelText('Digit 1 of 6')).toHaveValue('');

    // A refusal on the confirmation does not follow the visitor back to the first step.
    await pasteDigits('654321');
    await screen.findByText('PINs do not match. Please try again.');
    await userEvent.click(screen.getByRole('button', { name: 'Back' }));
    expect(await screen.findByText('Create your PIN')).toBeInTheDocument();
    expect(screen.queryByText('PINs do not match. Please try again.')).not.toBeInTheDocument();
    expect(saves).toEqual([]);
  });

  it("shows the server's own sentence when it refuses the PIN itself", async () => {
    // The row the mock's handler quotes as measured: {"pin":"abc"} -> 400, errors
    // {"Pin":["PIN must be exactly 6 digits."]}, with no errorCode. These boxes cannot send that
    // PIN; the sentence stands for any rule of the server's about a PIN that they can send.
    const alert = await refusedWith(() =>
      problem({ status: 400, errors: { Pin: ['PIN must be exactly 6 digits.'] } }),
    );

    expect(alert).toHaveTextContent('PIN must be exactly 6 digits.');
    expect(screen.queryByText('PIN Setup Complete!')).not.toBeInTheDocument();
  });

  it('says the PIN is not allowed when the refusal of the PIN has no sentence', async () => {
    const alert = await refusedWith(() => problem({ status: 400, errors: {} }));

    expect(alert).toHaveTextContent('That PIN is not allowed. Please choose another.');
  });

  it('asks for the password when the server says it is missing', async () => {
    // Measured row, quoted in the mock: enrol, no password -> 422 PASSWORD_REQUIRED.
    const alert = await refusedWith(() =>
      problem({
        instance: '/api/auth/pin',
        status: 422,
        errorCode: 'PASSWORD_REQUIRED',
        detail: 'Your password is required to set a PIN.',
      }),
    );

    expect(alert).toHaveTextContent('Enter your account password to set a PIN.');
  });

  it('says the account is locked after too many wrong passwords', async () => {
    // A wrong password here counts toward the sign-in's lock, and while it stands this route
    // answers 429 ACCOUNT_LOCKED to every password, the right one included, without reading it.
    // Measured on SQL Server on 2026-10-16 (`LoginGuessBoundSqlServerTests`): the right password
    // after a burst of wrong ones was answered 429 and compared with nothing. The mock keeps the
    // lock (src/mocks/handlers.ts), and the password typed here is the right one.
    mockState.loginLockedUntil[MOCK_USER.email] = new Date(Date.now() + 15 * 60_000).toISOString();
    await toTheConfirmation();
    await typePassword();
    await pasteDigits('123456');
    const alert = await screen.findByRole('alert');

    expect(alert).toHaveTextContent(/^Your account is locked\.$/);
    expect(screen.getByRole('timer')).toHaveTextContent(/^Try again in (15:00|14:5\d)$/);
  });

  /**
   * The lock's refusal as the browser receives it: the API's body and its `Retry-After`, as
   * measured on the API's host (SetPinLockAnswerTests) and forwarded unchanged by the BFF's
   * (SetPinLockForwardingTests), with `seconds` for the wait left. With no seconds the answer
   * carries neither the field nor the header.
   */
  function lockAnswer(seconds?: number) {
    return problem({
      instance: '/api/auth/pin',
      status: 429,
      errorCode: 'ACCOUNT_LOCKED',
      detail:
        'Too many failed login attempts. Your account is temporarily locked; try again later.',
      ...(seconds === undefined
        ? {}
        : {
            extensions: {
              retryAfterSeconds: seconds,
              lockedUntil: new Date(Date.now() + seconds * 1000).toISOString(),
            },
            headers: { 'Retry-After': String(seconds) },
          }),
    });
  }

  /** Every save from now on, counted; the first `locks` are refused with a wait of `seconds`. */
  function lockedFor(seconds: number, locks: number) {
    const saves: unknown[] = [];
    const refusedAt: number[] = [];
    server.use(
      http.post('*/bff/auth/set-pin', async ({ request }) => {
        saves.push(await request.clone().json());
        if (saves.length <= locks) {
          refusedAt.push(Date.now());
          return lockAnswer(seconds);
        }
      }),
    );
    return { saves, refusedAt };
  }

  const setPinButton = () => screen.getByRole('button', { name: 'Set PIN' });

  it('a lock with a wait says the account is locked, and counts down outside the alert', async () => {
    lockedFor(900, 1);
    await toTheConfirmation();
    await typePassword();
    await pasteDigits('123456');
    const alert = await screen.findByRole('alert');
    const timer = screen.getByRole('timer');

    expect(alert).toHaveTextContent(/^Your account is locked\.$/);
    expect(timer).toHaveTextContent(/^Try again in (15:00|14:5\d)$/);
    expect(timer).toHaveAttribute('aria-live', 'polite');
    expect(alert).not.toContainElement(timer);
    await waitFor(() => expect(screen.getByLabelText('Account password')).toHaveFocus());
    expect(setPinButton()).toBeDisabled();
  });

  it('while the lock counts nothing sends, edits included; at zero it goes and Set PIN saves', async () => {
    const { saves, refusedAt } = lockedFor(900, 1);
    await toTheConfirmation();
    await typePassword();
    installFakeClock();
    const user = fakeClockUser();
    await user.click(screen.getByLabelText('Digit 1 of 6'));
    await user.paste('123456');
    await screen.findByRole('timer');

    // An edit clears a refusal, and the lock with it would give the save back at once.
    const password = screen.getByLabelText('Account password');
    fireEvent.change(password, { target: { value: `${MOCK_PASSWORD}x` } });
    fireEvent.change(password, { target: { value: MOCK_PASSWORD } });
    // Six digits again, with the password in hand: the boxes' own send is held too.
    await user.click(screen.getByLabelText('Digit 1 of 6'));
    await user.paste('123456');
    await advanceUntil(refusedAt[0], 855_000);

    expect(screen.getByRole('alert')).toHaveTextContent(/^Your account is locked\.$/);
    expect(screen.getByRole('timer')).toHaveTextContent(/^Try again in 4\ds$/);
    expect(setPinButton()).toBeDisabled();
    expect(saves).toHaveLength(1);

    await advanceUntil(refusedAt[0], 899_000);
    expect(screen.getByRole('timer')).toBeInTheDocument();
    expect(setPinButton()).toBeDisabled();

    // Three seconds past the wait, not one: the page takes its deadline a moment after the
    // handler's instant, and the countdown looks at the clock once a second.
    await advanceUntil(refusedAt[0], 903_000);
    expect(screen.queryByRole('timer')).not.toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(setPinButton()).toBeEnabled();

    // Sent, with the PIN and the password kept. What it is answered is not this test's: fifteen
    // minutes on the fake clock have also ended the mock's session.
    await user.click(setPinButton());
    await waitFor(() => expect(saves).toHaveLength(2));
    expect(saves[1]).toEqual({ pin: '123456', password: MOCK_PASSWORD });
  });

  it('a second lock with the same wait counts from its own answer', async () => {
    const { saves, refusedAt } = lockedFor(60, 2);
    await toTheConfirmation();
    await typePassword();
    installFakeClock();
    const user = fakeClockUser();
    await user.click(screen.getByLabelText('Digit 1 of 6'));
    await user.paste('123456');
    await screen.findByRole('timer');
    await advanceUntil(refusedAt[0], 63_000);
    expect(screen.queryByRole('timer')).not.toBeInTheDocument();

    await user.click(setPinButton());

    expect(await screen.findByRole('timer')).toHaveTextContent(/^Try again in (1:00|59s)$/);
    expect(screen.getByRole('alert')).toHaveTextContent(/^Your account is locked\.$/);
    expect(setPinButton()).toBeDisabled();
    expect(saves).toHaveLength(2);
  });

  it('a lock goes back with the visitor to the first step, and goes at zero there', async () => {
    // Unlike a refusal of what was typed, the lock is the account's, whichever step is on screen.
    const { saves, refusedAt } = lockedFor(60, 1);
    await toTheConfirmation();
    await typePassword();
    installFakeClock();
    const user = fakeClockUser();
    await user.click(screen.getByLabelText('Digit 1 of 6'));
    await user.paste('123456');
    await screen.findByRole('timer');

    await user.click(screen.getByRole('button', { name: 'Back' }));
    expect(await screen.findByText('Create your PIN')).toBeInTheDocument();
    expect(screen.getByRole('alert')).toHaveTextContent(/^Your account is locked\.$/);
    expect(screen.getByRole('timer')).toHaveTextContent(/^Try again in (1:00|59s|58s)$/);

    await advanceUntil(refusedAt[0], 63_000);
    expect(screen.queryByRole('timer')).not.toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();

    // The password was kept, so the six boxes of the confirmation send by themselves again.
    await user.click(screen.getByRole('button', { name: 'Continue' }));
    await screen.findByText('Confirm your PIN');
    await user.click(screen.getByLabelText('Digit 1 of 6'));
    await user.paste('123456');
    await waitFor(() => expect(saves).toHaveLength(2));
  });

  it.each([
    { what: 'names no wait', seconds: undefined },
    { what: 'has no wait left', seconds: 0 },
  ])(
    'a lock whose answer $what is said as before, with no countdown and no hold',
    async ({ seconds }) => {
      server.use(http.post('*/bff/auth/set-pin', () => lockAnswer(seconds)));
      await toTheConfirmation();
      await typePassword();
      await pasteDigits('123456');

      expect(await screen.findByRole('alert')).toHaveTextContent(
        'Too many incorrect passwords. Your account is locked; try again later.',
      );
      expect(screen.queryByRole('timer')).not.toBeInTheDocument();
      expect(setPinButton()).toBeEnabled();
    },
  );

  /** The six boxes of the confirm step, in order. */
  const boxes = () =>
    Array.from({ length: 6 }, (_, index) => screen.getByLabelText(`Digit ${index + 1} of 6`));

  /** What each of the confirm step's two fields says about the refusal on screen. */
  function fields() {
    const alertId = screen.getByRole('alert').id;
    const password = screen.getByLabelText('Account password');
    return {
      password: {
        invalid: password.getAttribute('aria-invalid'),
        describedByTheAlert: (password.getAttribute('aria-describedby') ?? '')
          .split(' ')
          .includes(alertId),
        focused: document.activeElement === password,
      },
      boxes: {
        invalid: boxes().map((box) => box.getAttribute('aria-invalid')),
        describedBy: screen
          .getByRole('group', { name: 'Confirm your PIN' })
          .getAttribute('aria-describedby'),
        digits: boxes()
          .map((box) => (box as HTMLInputElement).value)
          .join(''),
      },
    };
  }

  /*
    A refusal of the password is about the password field: it is marked invalid, described by the
    alert and given focus, and the PIN boxes, which were fine, keep their six digits and are not
    marked. Each refusal is answered as the API answers it: a wrong password by the mock itself,
    the missing password in the API's envelope, and the lock by the mock's own lock.
  */
  it.each([
    {
      code: 'INVALID_CREDENTIALS',
      says: 'Incorrect password. After 5 failed attempts, sign-in is paused for 15 minutes.',
      typed: 'WrongPassword1!',
      arrange: () => {},
    },
    {
      code: 'PASSWORD_REQUIRED',
      says: 'Enter your account password to set a PIN.',
      typed: MOCK_PASSWORD,
      arrange: () =>
        server.use(
          http.post('*/bff/auth/set-pin', () =>
            problem({
              instance: '/api/auth/pin',
              status: 422,
              errorCode: 'PASSWORD_REQUIRED',
              detail: 'Your password is required to set a PIN.',
            }),
          ),
        ),
    },
    {
      code: 'ACCOUNT_LOCKED',
      says: 'Your account is locked.',
      typed: MOCK_PASSWORD,
      arrange: () => {
        mockState.loginLockedUntil[MOCK_USER.email] = new Date(
          Date.now() + 15 * 60_000,
        ).toISOString();
      },
    },
  ])(
    '$code is shown on the password field, which takes focus, and the PIN stays typed',
    async ({ says, typed, arrange }) => {
      arrange();
      const saves = watchSaves();
      await toTheConfirmation();
      await typePassword(typed);
      await pasteDigits('123456');
      expect(await screen.findByRole('alert')).toHaveTextContent(says);

      await waitFor(() => expect(screen.getByLabelText('Account password')).toHaveFocus());
      expect(fields()).toEqual({
        password: { invalid: 'true', describedByTheAlert: true, focused: true },
        boxes: { invalid: Array(6).fill(null), describedBy: null, digits: '123456' },
      });
      // The six digits kept are not sent again by themselves: one save, the visitor's.
      expect(saves).toHaveLength(1);
    },
  );

  it("a mismatch is still the boxes' error: they are marked, described and emptied", async () => {
    await toTheConfirmation();
    await typePassword();
    await pasteDigits('654321');
    await screen.findByText('PINs do not match. Please try again.');

    const alertId = screen.getByRole('alert').id;
    expect(fields()).toEqual({
      password: { invalid: null, describedByTheAlert: false, focused: false },
      boxes: { invalid: Array(6).fill('true'), describedBy: alertId, digits: '' },
    });
  });

  it('after a wrong password, the kept PIN is sent again only from "Set PIN"', async () => {
    /*
      The PIN authorises money leaving the account, so keeping it typed must not send it by
      itself: correcting the password clears the refusal and sends nothing, and the second save is
      the visitor's press of "Set PIN", with the kept PIN and the corrected password.
    */
    const saves = watchSaves();
    await toTheConfirmation();
    await typePassword('WrongPassword1!');
    await pasteDigits('123456');
    await screen.findByText(
      'Incorrect password. After 5 failed attempts, sign-in is paused for 15 minutes.',
    );

    await userEvent.clear(screen.getByLabelText('Account password'));
    await typePassword();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(saves).toHaveLength(1);

    await userEvent.click(screen.getByRole('button', { name: 'Set PIN' }));

    expect(await screen.findByText('PIN Setup Complete!')).toBeInTheDocument();
    expect(saves).toEqual([
      { pin: '123456', password: 'WrongPassword1!' },
      { pin: '123456', password: MOCK_PASSWORD },
    ]);
  });

  it('an answer that cannot be read says the PIN may have been saved, not "check your connection"', async () => {
    const alert = await refusedWith(() => new HttpResponse('crash page', { status: 500 }));

    expect(alert).toHaveTextContent(COPY.saveUnknown);
    expect(alert).not.toHaveTextContent(CONNECTION_FAILED);
  });

  it("shows the server's sentence for any other refusal, and one of its own when there is none", async () => {
    const withASentence = await refusedWith(() =>
      problem({ status: 409, errorCode: 'CONFLICT', detail: 'A PIN change is already running.' }),
    );
    expect(withASentence).toHaveTextContent('A PIN change is already running.');
    cleanup();

    const withNone = await refusedWith(() => problem({ status: 409, errorCode: 'CONFLICT' }));
    expect(withNone).toHaveTextContent('Could not set your PIN. Please try again.');
  });

  it('after a refusal the boxes are empty again and the password is kept', async () => {
    const saves: unknown[] = [];
    server.use(
      http.post('*/bff/auth/set-pin', async ({ request }) => {
        saves.push(await request.clone().json());
        // The first save is refused by the BFF's limiter; the second goes on to the mock's own
        // answer.
        if (saves.length === 1) {
          return problem({
            status: 429,
            errorCode: 'RATE_LIMIT_EXCEEDED',
            detail: 'Too many requests. Please retry later.',
          });
        }
      }),
    );
    await toTheConfirmation();
    await typePassword();
    await pasteDigits('123456');
    await screen.findByText('Too many requests. Please retry later.');

    expect(screen.getByLabelText('Digit 1 of 6')).toHaveValue('');
    // The PIN typed again is enough: the password did not have to be.
    await pasteDigits('123456');

    expect(await screen.findByText('PIN Setup Complete!')).toBeInTheDocument();
    expect(saves).toEqual([
      { pin: '123456', password: MOCK_PASSWORD },
      { pin: '123456', password: MOCK_PASSWORD },
    ]);
  });

  it('a mismatch pressed from "Set PIN" puts focus on the first box', async () => {
    const stop = emulateFocusFixup();
    try {
      await toTheConfirmation();
      await pasteDigits('654321');
      await typePassword();
      await userEvent.click(screen.getByRole('button', { name: 'Set PIN' }));

      expect(await screen.findByText('PINs do not match. Please try again.')).toBeInTheDocument();
      await waitFor(() => expect(screen.getByLabelText('Digit 1 of 6')).toHaveFocus());
    } finally {
      stop();
    }
  });

  it('a PIN the session did not know about leaves for returnTo', async () => {
    await toTheConfirmation();
    mockState.session!.hasPin = true;
    await typePassword();
    await pasteDigits('123456');

    expect(await screen.findByText('ACCOUNTS PAGE')).toBeInTheDocument();
  });

  it('says the account already has a PIN when the session cannot be read', async () => {
    await toTheConfirmation();
    mockState.session!.hasPin = true;
    server.use(
      http.get('*/bff/auth/me', () => serviceUnavailable({ via: 'bff', instance: '/bff/auth/me' })),
    );
    await typePassword();
    await pasteDigits('123456');

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Your account already has a PIN. To change it, go to Settings.',
    );
  });
});
