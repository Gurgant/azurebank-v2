import { Route, Routes } from 'react-router-dom';
import { cleanup, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { PinInput } from '../components/PinInput';
import { enableDemoMode } from '../test/demoMode';
import { makeTestStore, renderWithProviders, type TestStore } from '../test/renderWithProviders';
import { PinSetupPage } from './PinSetupPage';
import { MOCK_PASSWORD, MOCK_USER, mockState, seedMockSession } from '../mocks/state';
import { server } from '../mocks/server';
import { problem, serviceUnavailable } from '../mocks/problem';
import {
  COPY,
  advanceUntil,
  fakeClockUser,
  hintRegion,
  hintShownAt,
  installFakeClock,
  never,
} from '../test/outage';
import { CONNECTION_FAILED } from '../api/problemMessages';

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

    expect(await screen.findByText('That password is not correct.')).toBeInTheDocument();
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
  async function toTheConfirmation() {
    renderPinSetup();
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

    expect(alert).toHaveTextContent(
      'Too many incorrect passwords. Your account is locked; try again later.',
    );
  });

  it('says to check the connection when the request never reaches the server', async () => {
    const alert = await refusedWith(() => HttpResponse.error());

    expect(alert).toHaveTextContent(CONNECTION_FAILED);
    expect(alert).not.toHaveTextContent(COPY.saveUnknown);
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
        // The first save is refused; the second goes on to the mock's own answer.
        if (saves.length === 1) return HttpResponse.error();
      }),
    );
    await toTheConfirmation();
    await typePassword();
    await pasteDigits('123456');
    await screen.findByText(CONNECTION_FAILED);

    expect(screen.getByLabelText('Digit 1 of 6')).toHaveValue('');
    // The PIN typed again is enough: the password did not have to be.
    await pasteDigits('123456');

    expect(await screen.findByText('PIN Setup Complete!')).toBeInTheDocument();
    expect(saves).toEqual([
      { pin: '123456', password: MOCK_PASSWORD },
      { pin: '123456', password: MOCK_PASSWORD },
    ]);
  });
});
