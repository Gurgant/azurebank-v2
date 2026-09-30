import { Route, Routes } from 'react-router-dom';
import { act, cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { server } from '../mocks/server';
import { problem, serviceUnavailable } from '../mocks/problem';
import { renderWithProviders } from '../test/renderWithProviders';
import { TEST_PIN, enterPin } from '../test/pinFlow';
import {
  COPY,
  advance,
  advanceUntil,
  expectSilentHintTakesNoRoom,
  fakeClockUser,
  hintRegion,
  hintShownAt,
  never,
  pinBoxValues,
  sleep,
  installFakeClock,
} from '../test/outage';
import { CONNECTION_FAILED } from '../api/problemMessages';
import { apiSlice, type AccountResponse } from '../features/api/apiSlice';
import { StepUpModal } from '../features/auth';
import { TransferPage } from './TransferPage';
import { InternalTransferPage } from './InternalTransferPage';

/**
 * A transfer during an outage: what the visitor is told, and what the page lets them do next.
 *
 * The rules, each stated where it is asserted:
 *  - the confirm (mint, then send) is ONE wait with one hint, and the hint promises nothing: no
 *    "Retrying won't charge you twice" while nothing on the page can re-send the same key;
 *  - that sentence sits beside the controls that DO re-send the same key — the resend bar, in
 *    all three of its forms (in flight, outcome unknown, nothing applied);
 *  - "nothing was changed" only when the server said so (`applied: false`); a failed MINT moved
 *    no money whatever the answer, and asks for the PIN again with the boxes emptied — after a
 *    lost connection or a 500 as well as an outage;
 *  - a send with no answer in 65 s is the outage, not a connection problem;
 *  - the recipient check can be stopped, and a check with no answer ends as an outage.
 *
 * Sends are recorded by header rather than inferred from the screen: the same key and the same
 * authorisation on the retry are the whole safety property.
 */

afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

function renderTransfer() {
  return renderWithProviders(
    <Routes>
      <Route
        path="/"
        element={
          <>
            <TransferPage />
            <StepUpModal />
          </>
        }
      />
      <Route path="/dashboard" element={<div>DASHBOARD</div>} />
      <Route path="/history" element={<div>HISTORY</div>} />
    </Routes>,
    { routerEntries: ['/'] },
  );
}

function renderInternal() {
  return renderWithProviders(
    <Routes>
      <Route
        path="/"
        element={
          <>
            <InternalTransferPage />
            <StepUpModal />
          </>
        }
      />
      <Route path="/dashboard" element={<div>DASHBOARD</div>} />
      <Route path="/history" element={<div>HISTORY</div>} />
    </Routes>,
    { routerEntries: ['/'] },
  );
}

/** Form → the review step, stopping before Continue. */
async function externalToReview() {
  await screen.findByText('Main Account');
  await userEvent.type(screen.getByLabelText('Recipient handle'), 'friend');
  await userEvent.click(screen.getByRole('button', { name: 'Verify' }));
  await screen.findByText('A. Friend');
  await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
  await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
}

async function internalToReview() {
  await screen.findByRole('button', { name: 'From Main Account' });
  await userEvent.click(screen.getByRole('button', { name: 'To Rainy Day' }));
  await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
  await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
}

/** Form → review → the PIN step, stopping before the first digit. */
async function externalToPin() {
  await externalToReview();
  await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
  await screen.findByLabelText('Digit 1 of 6');
}

async function internalToPin() {
  await internalToReview();
  await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
  await screen.findByLabelText('Digit 1 of 6');
}

/**
 * Records every send's key and authorisation. The n-th send gets `answers[n]`; a send with no
 * answer of its own falls through to the mock's real handler, which commits it.
 */
function recordSends(path: string, answers: (() => Response)[]) {
  const sent: { key: string | null; auth: string | null }[] = [];
  server.use(
    http.post(path, ({ request }) => {
      sent.push({
        key: request.headers.get('Idempotency-Key'),
        auth: request.headers.get('Step-Up-Authorization'),
      });
      return answers[sent.length - 1]?.();
    }),
  );
  return sent;
}

const stillProcessing = () =>
  problem({
    status: 409,
    errorCode: 'IDEMPOTENCY_IN_FLIGHT',
    detail: 'A request with this key is still being processed.',
  });

const UNKNOWN_BAR =
  "We couldn't reach the bank. Your transfer may or may not have gone through — check again.";
const IN_FLIGHT_BAR = 'Still processing — check again to see whether it went through.';

/** The alert that carries `text`, which must be exactly the flow's error bar's words. */
async function alertSays(text: string) {
  const words = await screen.findByText(text);
  expect(words.closest('[role="alert"]')).not.toBeNull();
}

describe('the confirm wait', () => {
  it('is one wait, from the sixth digit through the send, without a promise or a stop', async () => {
    let mintAt = 0;
    let sends = 0;
    server.use(
      http.post('*/api/transfers/authorizations', async () => {
        mintAt = Date.now();
        await sleep(3_000);
        return undefined;
      }),
      http.post('*/api/transfers', () => {
        sends += 1;
        return never();
      }),
    );
    renderTransfer();
    await externalToPin();

    installFakeClock();
    const user = fakeClockUser();
    await user.click(screen.getByLabelText('Digit 1 of 6'));
    await user.paste(TEST_PIN);
    await waitFor(() => expect(mintAt).toBeGreaterThan(0));
    // The mint leaves before the page has drawn the wait, so the wait's clock starts a moment
    // later; under load, a count from the mint reached 5 s while the hint was still empty. Count
    // from the drawn wait instead, which is on screen before the hand-off.
    const shownAt = await hintShownAt();
    expect(sends).toBe(0);

    // The hand-off: the mint answers at 3 s and the send starts.
    await advanceUntil(mintAt, 3_100);
    await waitFor(() => expect(sends).toBe(1));

    // Counted from the sixth digit, not restarted by the hand-off.
    await advanceUntil(shownAt, 5_000);
    hintRegion(COPY.slow);
    await advanceUntil(shownAt, 20_000);
    const region = hintRegion(COPY.stillTrying);

    expect(screen.queryByText(COPY.noDoubleCharge, { exact: false })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: COPY.stopWaiting })).not.toBeInTheDocument();
    const boxes = screen.getByRole('group', { name: 'Enter your PIN' });
    expect(boxes.compareDocumentPosition(region) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  /** Counts the mints on `path`, letting each one through to the mock. */
  function countMints(path: string) {
    const mints = { count: 0 };
    server.use(
      http.post(path, () => {
        mints.count += 1;
        return undefined;
      }),
    );
    return mints;
  }

  /**
   * Patches the cached accounts while the PIN step is open, as a change elsewhere in the tab would,
   * then waits for the page to see it: RTK hands a cache patch to its subscribers on the next
   * animation frame, long before a person has typed six digits.
   */
  async function patchAccountsUnderThePinStep(
    store: ReturnType<typeof renderTransfer>['store'],
    patch: (accounts: AccountResponse[]) => AccountResponse[] | void,
  ) {
    act(() => {
      store.dispatch(apiSlice.util.updateQueryData('getAccounts', undefined, patch));
    });
    await act(() => new Promise<void>((resolve) => requestAnimationFrame(() => resolve())));
  }

  /** Nothing is pending, so nothing may say it is waiting — 25 s on, past both thresholds. */
  async function expectNoHintLeftBehind() {
    await advance(25_000);
    expect(screen.queryAllByRole('status')).toHaveLength(0);
    expect(screen.queryByText(COPY.slow)).not.toBeInTheDocument();
    expect(screen.queryByText(COPY.stillTrying)).not.toBeInTheDocument();
  }

  it.each([
    ['external', renderTransfer, externalToPin, '*/api/transfers/authorizations'],
    ['internal', renderInternal, internalToPin, '*/api/transfers/internal/authorizations'],
  ])(
    '%s: an over-balance that the form refuses at the sixth digit leaves no hint behind',
    async (_name, render, toPin, mintPath) => {
      const mints = countMints(mintPath);
      const { store } = render();
      await toPin();

      // The cached balance now covers less than the €50. The form's own bound follows it, so the
      // sixth digit is refused by the form's validation, before the confirm handler ever runs.
      await patchAccountsUnderThePinStep(store, (draft) => {
        for (const account of draft) account.balance = 10;
      });
      installFakeClock();
      const user = fakeClockUser();
      await user.click(screen.getByLabelText('Digit 1 of 6'));
      await user.paste(TEST_PIN);
      expect(
        (await screen.findAllByText('Exceeds available balance of €10.00.')).length,
      ).toBeGreaterThan(0);

      await expectNoHintLeftBehind();
      expect(mints.count).toBe(0);
    },
  );

  it('internal: a destination that disappears under the PIN step stops the confirm before any request, and leaves no hint behind', async () => {
    const mints = countMints('*/api/transfers/internal/authorizations');
    const { store } = renderInternal();
    await internalToPin();

    // The form holds only the destination's id, so it still validates and the sixth digit reaches
    // the confirm handler; there the destination is looked up in the accounts, found missing, and
    // the handler returns before it mints. A wait begun before that return would never end.
    await patchAccountsUnderThePinStep(store, (draft) =>
      draft.filter((account) => account.name !== 'Rainy Day'),
    );
    installFakeClock();
    const user = fakeClockUser();
    await user.click(screen.getByLabelText('Digit 1 of 6'));
    await user.paste(TEST_PIN);

    await expectNoHintLeftBehind();
    expect(mints.count).toBe(0);
    // And the form did not refuse it, which would have been the other path.
    expect(screen.queryByText('Please check the details and try again.')).not.toBeInTheDocument();
  });
});

describe.each([
  {
    page: 'external',
    render: renderTransfer,
    toPin: externalToPin,
    mintPath: '*/api/transfers/authorizations',
    sendPath: '*/api/transfers',
    safeRetry: COPY.noDoubleCharge,
    sent: 'Transfer Sent!',
  },
  {
    page: 'internal',
    render: renderInternal,
    toPin: internalToPin,
    mintPath: '*/api/transfers/internal/authorizations',
    sendPath: '*/api/transfers/internal',
    safeRetry: COPY.noDoubleMove,
    sent: 'Transfer Complete!',
  },
])('$page transfer: a send or a mint that meets the outage', (flow) => {
  it('a send refused with nothing applied says nothing changed, and "Try again" re-sends the same key and authorisation', async () => {
    const sent = recordSends(flow.sendPath, [
      () => serviceUnavailable({ via: 'api', applied: false, instance: flow.sendPath.slice(1) }),
    ]);
    flow.render();
    await flow.toPin();
    await enterPin();

    await alertSays(COPY.nothingChanged);
    const tryAgain = screen.getByRole('button', { name: COPY.tryAgain });
    expect(tryAgain.closest('[role="status"]')).toHaveTextContent(flow.safeRetry);
    expect(screen.queryByText(/may or may not have gone through/i)).not.toBeInTheDocument();

    await userEvent.click(tryAgain);
    expect(await screen.findByText(flow.sent)).toBeInTheDocument();
    expect(sent).toHaveLength(2);
    expect(sent[1]).toEqual(sent[0]);
  });

  it('a send answered 503 without `applied` is an unknown: the same-key check is offered, and it is safe', async () => {
    const sent = recordSends(flow.sendPath, [
      () => serviceUnavailable({ via: 'api', instance: flow.sendPath.slice(1) }),
    ]);
    flow.render();
    await flow.toPin();
    await enterPin();

    await alertSays(COPY.unavailable);
    const check = screen.getByRole('button', { name: 'Check again' });
    expect(check.closest('[role="status"]')).toHaveTextContent(`${UNKNOWN_BAR} ${flow.safeRetry}`);
    expect(pinBoxValues()).toEqual(TEST_PIN.split(''));

    await userEvent.click(check);
    expect(await screen.findByText(flow.sent)).toBeInTheDocument();
    expect(sent).toHaveLength(2);
    expect(sent[1].key).toBe(sent[0].key);
    expect(sent[1].auth).toBe(sent[0].auth);
  });

  it('a send with no answer ends at 65 s as an outage, and the same-key check is offered', async () => {
    let sentAt = 0;
    server.use(
      http.post(flow.sendPath, () => {
        sentAt = Date.now();
        return never();
      }),
    );
    flow.render();
    await flow.toPin();
    installFakeClock();
    const user = fakeClockUser();
    await user.click(screen.getByLabelText('Digit 1 of 6'));
    await user.paste(TEST_PIN);
    await waitFor(() => expect(sentAt).toBeGreaterThan(0));

    await advanceUntil(sentAt, 65_100);

    await alertSays(COPY.unavailable);
    expect(screen.queryByText(CONNECTION_FAILED)).not.toBeInTheDocument();
    const check = screen.getByRole('button', { name: 'Check again' });
    expect(check.closest('[role="status"]')).toHaveTextContent(`${UNKNOWN_BAR} ${flow.safeRetry}`);
  });

  it('a mint answered 503 moved no money: the PIN is asked for again, and no key is ever sent', async () => {
    let sends = 0;
    server.use(
      http.post(flow.mintPath, () =>
        serviceUnavailable({ via: 'api', instance: flow.mintPath.slice(1) }),
      ),
      http.post(flow.sendPath, () => {
        sends += 1;
        return undefined;
      }),
    );
    flow.render();
    await flow.toPin();
    await enterPin();

    await alertSays(COPY.noMoneyMoved);
    await waitFor(() => expect(pinBoxValues()).toEqual(['', '', '', '', '', '']));
    await waitFor(() => expect(document.activeElement).toBe(screen.getByLabelText('Digit 1 of 6')));
    expect(screen.queryByRole('button', { name: 'Check again' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: COPY.tryAgain })).not.toBeInTheDocument();
    expect(sends).toBe(0);
  });

  it.each([
    ['a lost connection', () => HttpResponse.error(), CONNECTION_FAILED],
    [
      'a 500',
      () =>
        problem({
          status: 500,
          errorCode: 'INTERNAL_ERROR',
          detail: 'An unexpected error occurred. Please try again later.',
        }),
      'An unexpected error occurred. Please try again later.',
    ],
  ])(
    'a mint lost to %s asks for the PIN again too, in its own words',
    async (_cause, answer, words) => {
      let sends = 0;
      server.use(
        http.post(flow.mintPath, answer),
        http.post(flow.sendPath, () => {
          sends += 1;
          return undefined;
        }),
      );
      flow.render();
      await flow.toPin();
      await enterPin();

      await alertSays(words);
      await waitFor(() => expect(pinBoxValues()).toEqual(['', '', '', '', '', '']));
      await waitFor(() =>
        expect(document.activeElement).toBe(screen.getByLabelText('Digit 1 of 6')),
      );
      expect(sends).toBe(0);
    },
  );

  it('a slow confirm says so under the PIN boxes, and nothing still says it is waiting once it has failed', async () => {
    let mintAt = 0;
    server.use(
      http.post(flow.mintPath, async () => {
        mintAt = Date.now();
        await sleep(8_000);
        return serviceUnavailable({ via: 'api', instance: flow.mintPath.slice(1) });
      }),
    );
    flow.render();
    await flow.toPin();
    installFakeClock();
    const user = fakeClockUser();
    await user.click(screen.getByLabelText('Digit 1 of 6'));
    await user.paste(TEST_PIN);
    const shownAt = await hintShownAt();

    await advanceUntil(shownAt, 5_000);
    const region = hintRegion(COPY.slow);
    const boxes = screen.getByRole('group', { name: 'Enter your PIN' });
    expect(boxes.compareDocumentPosition(region) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(screen.queryByText(COPY.noDoubleCharge, { exact: false })).not.toBeInTheDocument();
    expect(screen.queryByText(COPY.noDoubleMove, { exact: false })).not.toBeInTheDocument();

    await advanceUntil(mintAt, 8_100);
    await alertSays(COPY.noMoneyMoved);

    await advance(25_000);
    expect(document.querySelector('[data-wait-hint]')).toBeNull();
    expect(screen.queryByText(COPY.slow)).not.toBeInTheDocument();
    expect(screen.queryByText(COPY.stillTrying)).not.toBeInTheDocument();
  });

  it('the in-flight bar adds that checking again is safe', async () => {
    recordSends(flow.sendPath, [stillProcessing]);
    flow.render();
    await flow.toPin();
    await enterPin();

    const check = await screen.findByRole('button', { name: 'Check again' });
    expect(check.closest('[role="status"]')).toHaveTextContent(
      `${IN_FLIGHT_BAR} ${flow.safeRetry}`,
    );
  });
});

describe('after "Check again" on an in-flight send', () => {
  it('an expired confirmation empties the boxes and asks for the PIN again', async () => {
    recordSends('*/api/transfers', [
      stillProcessing,
      () =>
        problem({
          status: 401,
          errorCode: 'AUTHORIZATION_EXPIRED',
          detail: 'This authorisation has expired.',
        }),
    ]);
    renderTransfer();
    await externalToPin();
    await enterPin();

    await userEvent.click(await screen.findByRole('button', { name: 'Check again' }));
    expect(
      await screen.findByText(
        'For your security, your confirmation expired. Your transfer details are still here — enter your PIN again to confirm.',
      ),
    ).toBeInTheDocument();
    await waitFor(() => expect(pinBoxValues()).toEqual(['', '', '', '', '', '']));
  });

  it('a result the server cannot confirm shows the verify view', async () => {
    recordSends('*/api/transfers', [
      stillProcessing,
      () =>
        problem({
          status: 409,
          errorCode: 'IDEMPOTENCY_RESULT_UNKNOWN',
          detail: 'The result could not be confirmed.',
        }),
    ]);
    renderTransfer();
    await externalToPin();
    await enterPin();

    await userEvent.click(await screen.findByRole('button', { name: 'Check again' }));
    expect(await screen.findByText("We couldn't confirm your transfer")).toBeInTheDocument();
  });
});

describe.each([
  { page: 'external', render: renderTransfer, toReview: externalToReview },
  { page: 'internal', render: renderInternal, toReview: internalToReview },
])('$page transfer: the reads the form waits on', (flow) => {
  it('a slow load of the accounts says so, and "Stop waiting" lands on an announced bar with focus on Retry', async () => {
    const seen: { at: number; signal: AbortSignal }[] = [];
    server.use(
      http.get('*/api/accounts', ({ request }) => {
        seen.push({ at: Date.now(), signal: request.signal });
        return never();
      }),
    );
    installFakeClock();
    const user = fakeClockUser();
    const start = Date.now();
    flow.render();
    await waitFor(() => expect(seen).toHaveLength(1));

    await advanceUntil(start, 4_900);
    expect(screen.queryByText(COPY.slow)).not.toBeInTheDocument();
    await advanceUntil(seen[0].at, 5_000);
    hintRegion(COPY.slow);
    await advanceUntil(seen[0].at, 20_000);
    hintRegion(COPY.stillTrying);

    await user.click(screen.getByRole('button', { name: COPY.stopWaiting }));

    expect(seen[0].signal.aborted).toBe(true);
    await alertSays('Could not load your accounts.');
    await waitFor(() =>
      expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Retry' })),
    );
    await advance(30_000);
    expect(seen).toHaveLength(1);
  });

  it('Retry on the accounts waits again with the form still hidden, and a second failure is announced again', async () => {
    let holdNext = false;
    let release: () => void = () => {};
    let calls = 0;
    server.use(
      http.get('*/api/accounts', async () => {
        calls += 1;
        if (holdNext) {
          holdNext = false;
          await new Promise<void>((resolve) => (release = resolve));
        }
        return problem({
          status: 500,
          errorCode: 'INTERNAL_ERROR',
          detail: 'An unexpected error occurred. Please try again later.',
        });
      }),
    );
    installFakeClock();
    const user = fakeClockUser();
    try {
      flow.render();
      const first = await screen.findByRole('alert');

      holdNext = true;
      const retriedAt = Date.now();
      await user.click(within(first).getByRole('button', { name: 'Retry' }));
      await waitFor(() => expect(calls).toBe(2));

      // The bar goes while the accounts load again, and an empty form does not take its place.
      const shownAt = await hintShownAt();
      expect(screen.queryByRole('alert')).not.toBeInTheDocument();
      expect(screen.queryByRole('button', { name: 'Review Transfer' })).not.toBeInTheDocument();
      await advanceUntil(retriedAt, 4_900);
      expect(screen.queryByText(COPY.slow)).not.toBeInTheDocument();
      await advanceUntil(shownAt, 5_000);
      hintRegion(COPY.slow);
      await advanceUntil(shownAt, 20_000);
      hintRegion(COPY.stillTrying);
      expect(screen.getByRole('button', { name: COPY.stopWaiting })).toBeInTheDocument();

      release();
      const second = await screen.findByRole('alert');
      expect(second).not.toBe(first);
      expect(second).toHaveTextContent('An unexpected error occurred. Please try again later.');
      await waitFor(() =>
        expect(document.activeElement).toBe(within(second).getByRole('button', { name: 'Retry' })),
      );
    } finally {
      release();
    }
  });

  it('every new failure of the accounts is a new alert, with focus on its Retry', async () => {
    /*
      A 500 is not retried and the mock answers it at once, so a Retry's start and its failure can
      reach the page in the same frame, and the wait is never drawn. A bar that stayed the same
      element would then say nothing the second time. Pressed twice, as on the reading pages.
    */
    let calls = 0;
    server.use(
      http.get('*/api/accounts', () => {
        calls += 1;
        return problem({ status: 500, errorCode: 'INTERNAL_ERROR', detail: 'Something broke.' });
      }),
    );
    const user = userEvent.setup();
    flow.render();
    const alertOf = () =>
      screen.getByText(/Something broke\./).closest<HTMLElement>('[role="alert"]');
    await screen.findByText(/Something broke\./);

    for (let press = 1; press <= 2; press += 1) {
      const shown = alertOf();
      expect(shown).not.toBeNull();
      const before = calls;
      await user.click(within(shown as HTMLElement).getByRole('button', { name: 'Retry' }));
      await waitFor(() => expect(calls).toBe(before + 1));

      await waitFor(() => {
        expect(alertOf()).not.toBeNull();
        expect(alertOf()).not.toBe(shown);
      });
      const again = alertOf() as HTMLElement;
      await waitFor(() =>
        expect(document.activeElement).toBe(within(again).getByRole('button', { name: 'Retry' })),
      );
    }
  });

  it('the funds check on Continue says it is slow, under the button, and offers no way to stop it', async () => {
    flow.render();
    await flow.toReview();
    let checkedAt = 0;
    server.use(
      http.get('*/api/accounts', () => {
        checkedAt = Date.now();
        return never();
      }),
    );
    installFakeClock();
    const user = fakeClockUser();
    const clickedAt = Date.now();
    await user.click(screen.getByRole('button', { name: 'Continue' }));
    await waitFor(() => expect(checkedAt).toBeGreaterThan(0));
    const shownAt = await hintShownAt();

    await advanceUntil(clickedAt, 4_900);
    expect(screen.queryByText(COPY.slow)).not.toBeInTheDocument();
    await advanceUntil(shownAt, 5_000);
    hintRegion(COPY.slow);
    await advanceUntil(shownAt, 20_000);
    const region = hintRegion(COPY.stillTrying);

    expect(screen.queryByRole('button', { name: COPY.stopWaiting })).not.toBeInTheDocument();
    expect(screen.getAllByRole('status')).toEqual([region]);
    const note = screen.getByText("You'll confirm with your PIN on the next step.");
    expect(note.compareDocumentPosition(region) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('a funds check after one that failed is still one hint, and nothing can stop it', async () => {
    /*
      The check reloads the accounts. Once a check has failed — it fails open, on to the PIN step —
      the accounts hold their data and that error together, and the next check is a reload after
      an error: the accounts' own hint must not come back beside the check's, with a "Stop
      waiting" that would stop the check and skip it.
    */
    flow.render();
    await flow.toReview();
    server.use(
      http.get('*/api/accounts', () =>
        problem({ status: 500, errorCode: 'INTERNAL_ERROR', detail: 'Something broke.' }),
      ),
    );
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
    await screen.findByLabelText('Digit 1 of 6');
    const backs = screen.getAllByRole('button', { name: 'Back' });
    await userEvent.click(backs[backs.length - 1]);
    await screen.findByRole('button', { name: 'Continue' });

    const checks: AbortSignal[] = [];
    server.use(
      http.get('*/api/accounts', ({ request }) => {
        checks.push(request.signal);
        return never();
      }),
    );
    installFakeClock();
    const user = fakeClockUser();
    await user.click(screen.getByRole('button', { name: 'Continue' }));
    await waitFor(() => expect(checks).toHaveLength(1));
    const shownAt = await hintShownAt();
    await advanceUntil(shownAt, 20_500);

    const region = hintRegion(COPY.stillTrying);
    expect(document.querySelectorAll('[data-wait-hint]')).toHaveLength(1);
    expect(screen.getAllByRole('status')).toEqual([region]);
    expect(screen.queryByRole('button', { name: COPY.stopWaiting })).not.toBeInTheDocument();
    expect(checks[0].aborted).toBe(false);
    expect(screen.queryByLabelText('Digit 1 of 6')).not.toBeInTheDocument();
  });

  it('while the accounts first load, the form waits for them: nothing else can start beside it', async () => {
    let release: () => void = () => {};
    server.use(
      // Held, then handed on to the mock's own handler, which answers with the seeded accounts.
      http.get('*/api/accounts', async () => {
        await new Promise<void>((resolve) => (release = resolve));
      }),
    );
    try {
      flow.render();
      await hintShownAt();

      expect(screen.queryByLabelText('Transfer amount')).not.toBeInTheDocument();
      expect(screen.queryByLabelText('Recipient handle')).not.toBeInTheDocument();
      expect(screen.queryByRole('button', { name: 'Review Transfer' })).not.toBeInTheDocument();

      release();
      expect(await screen.findByLabelText('Transfer amount')).toBeInTheDocument();
      expect(document.querySelector('[data-wait-hint]')).toBeNull();
    } finally {
      release();
    }
  });
});

describe('the recipient check', () => {
  function holdLookups() {
    const seen: { at: number; signal: AbortSignal }[] = [];
    server.use(
      http.get('*/api/users/:azureTag', ({ request }) => {
        seen.push({ at: Date.now(), signal: request.signal });
        return never();
      }),
    );
    return seen;
  }

  async function verifyOnTheFakeClock() {
    renderTransfer();
    await screen.findByText('Main Account');
    await userEvent.type(screen.getByLabelText('Recipient handle'), 'friend');
    installFakeClock();
    const user = fakeClockUser();
    await user.click(screen.getByRole('button', { name: 'Verify' }));
    return user;
  }

  it('"Stop waiting" aborts it and lands on Verify, with the reason announced', async () => {
    const lookups = holdLookups();
    const user = await verifyOnTheFakeClock();
    await waitFor(() => expect(lookups).toHaveLength(1));

    await advanceUntil(lookups[0].at, 20_000);
    await user.click(screen.getByRole('button', { name: COPY.stopWaiting }));

    expect(lookups[0].signal.aborted).toBe(true);
    await alertSays("We couldn't check that handle. Please try again.");
    await waitFor(() =>
      expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Verify' })),
    );
  });

  it('moves nothing on the page until its hint has words', async () => {
    const lookups = holdLookups();
    await verifyOnTheFakeClock();
    await waitFor(() => expect(lookups).toHaveLength(1));

    const shownAt = await hintShownAt();
    expectSilentHintTakesNoRoom();
    await advanceUntil(shownAt, 5_000);
    hintRegion(COPY.slow);
  });

  it('a check with no answer ends at 65 s saying the service is unavailable', async () => {
    const lookups = holdLookups();
    await verifyOnTheFakeClock();
    await waitFor(() => expect(lookups).toHaveLength(1));

    await advanceUntil(lookups[0].at, 65_100);

    await alertSays(COPY.unavailable);
    expect(screen.queryByText(CONNECTION_FAILED)).not.toBeInTheDocument();
  });
});
