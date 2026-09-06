import { act, cleanup, fireEvent, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { Route, Routes, useLocation } from 'react-router-dom';
import { server } from '../../mocks/server';
import { problem } from '../../mocks/problem';
import { mockState } from '../../mocks/state';
import { apiSlice } from '../../features/api/apiSlice';
import { makeTestStore, renderWithProviders, type TestStore } from '../../test/renderWithProviders';
import { enterPin, TEST_PIN } from '../../test/pinFlow';
import { DeleteAccountDialog } from './DeleteAccountDialog';

/*
  UNMOUNT FIRST, then restore the clock — the order is load-bearing (see withdraw.test.tsx: a
  countdown still mounted with a live FAKE interval leaks setState into the next test). The
  `server.events` listeners some tests register are module-level and must not leak either.
*/
afterEach(() => {
  cleanup();
  vi.useRealTimers();
  server.events.removeAllListeners();
});

/**
 * ADR-0049 from the client's side: the sixth digit mints a deletion authorisation and the DELETE
 * presents it in the header. Where a case pins the MOCK (the guards, M0, the happy path) the real
 * handlers answer; where it pins the DIALOG's own branch (INVALID_PIN, PIN_LOCKED, EXPIRED,
 * NETWORK, ACCOUNT_NOT_FOUND) a `server.use` override does, and the test's comment quotes the
 * measured row of `measure-after-main-19742ff-2026-09-06.txt` (2026-09-06T19:16Z, main 19742ff)
 * the branch exists for.
 */

const TEMP_FUND = { id: '019f7b3f-0000-7000-8000-0000000000a3', name: 'Temp Fund' };
const MINT_URL = '*/api/accounts/:id/deletion-authorizations';
const ACCOUNT_URL = '*/api/accounts/:id';

function seedTempFund() {
  mockState.accounts.push({
    id: TEMP_FUND.id,
    accountNumber: 'AB-****-****-33',
    name: TEMP_FUND.name,
    type: 'Savings',
    balance: 0,
    isPrimary: false,
    createdAt: '2026-07-10T09:00:00.0000000Z',
  });
}

/**
 * The PIN-setup destination, probed (register-handoff.test.tsx's idiom) so a test can assert the
 * WHOLE query string the handoff carries — a path route matches any search, so the route element
 * rendering alone would pass with returnTo absent, wrong, or with the PIN riding along.
 */
function PinSetupProbe() {
  return (
    <>
      <div>PIN SETUP PAGE</div>
      <div data-testid="pin-setup-search">{useLocation().search}</div>
    </>
  );
}

function renderDialog(account = TEMP_FUND, store: TestStore = makeTestStore()) {
  const onClose = vi.fn();
  const { router } = renderWithProviders(
    <Routes>
      <Route path="/" element={<DeleteAccountDialog account={account} onClose={onClose} />} />
      <Route path="/pin-setup" element={<PinSetupProbe />} />
    </Routes>,
    { routerEntries: ['/'], store },
  );
  return { onClose, store, router };
}

/** Count every GET /api/accounts the store makes from now on — a list-tag invalidation refetches. */
function countListGets() {
  const counter = { lists: 0 };
  server.events.on('request:start', ({ request }) => {
    if (request.method === 'GET' && new URL(request.url).pathname === '/api/accounts') {
      counter.lists += 1;
    }
  });
  return counter;
}

/** Every PIN box, for the "cleared and enabled" checks the retry-capable branches share. */
function pinBoxes() {
  return Array.from({ length: 6 }, (_, i) => screen.getByLabelText(`Digit ${i + 1} of 6`));
}

async function reachPinStep() {
  const dialog = await screen.findByRole('dialog', { name: 'Delete account?' });
  await userEvent.click(screen.getByRole('button', { name: 'Delete' }));
  await screen.findByLabelText('Digit 1 of 6');
  return dialog;
}

/** Every request the dialog makes, observed WITHOUT replacing the handler that answers it. */
function observeRequests() {
  const seen: { method: string; url: string; header: string | null; body: string }[] = [];
  server.events.on('request:start', ({ request }) => {
    if (!request.url.includes('/api/accounts/')) return;
    void request
      .clone()
      .text()
      .then((body) =>
        seen.push({
          method: request.method,
          url: request.url,
          header: request.headers.get('Step-Up-Authorization'),
          body,
        }),
      );
  });
  return seen;
}

describe('DeleteAccountDialog — the closure costs a PIN (ADR-0049)', () => {
  it('the sixth digit mints then deletes — one POST then one DELETE carrying the minted id in the header', async () => {
    /*
      The wire shape, pinned on the client side where a refactor could quietly move it: the PIN
      travels in the mint's JSON body and nowhere else (no query string, no header), and the
      authorisation travels in the DELETE's header with NO body. Canned answers, so only the
      dialog is under test here; the real mock answers the next test.
    */
    seedTempFund();
    const authorizationId = crypto.randomUUID();
    const seen: { kind: 'mint' | 'delete'; url: string; header: string | null; body: string }[] =
      [];
    server.use(
      http.post(MINT_URL, async ({ request }) => {
        seen.push({
          kind: 'mint',
          url: request.url,
          header: request.headers.get('Step-Up-Authorization'),
          body: await request.text(),
        });
        return HttpResponse.json(
          {
            data: { authorizationId, expiresAt: new Date(Date.now() + 120_000).toISOString() },
            message: 'Account closure authorised',
          },
          { status: 201 },
        );
      }),
      http.delete(ACCOUNT_URL, async ({ request }) => {
        seen.push({
          kind: 'delete',
          url: request.url,
          header: request.headers.get('Step-Up-Authorization'),
          body: await request.text(),
        });
        return HttpResponse.json({ message: 'Account deleted successfully' }, { status: 200 });
      }),
    );
    const { onClose } = renderDialog();
    await reachPinStep();
    await enterPin();

    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
    expect(seen.map((r) => r.kind)).toEqual(['mint', 'delete']);
    const [mint, del] = seen;
    // The PIN: in the mint's body, exactly, and nowhere else.
    expect(JSON.parse(mint.body)).toEqual({ pin: TEST_PIN });
    expect(new URL(mint.url).search).toBe('');
    expect(mint.header).toBeNull();
    // The authorisation: in the DELETE's header, a 36-char GUID equal to the one minted; no body.
    expect(del.header).toBe(authorizationId);
    expect(del.header).toMatch(/^[0-9a-f-]{36}$/i);
    expect(del.body).toBe('');
    expect(new URL(del.url).search).toBe('');
  });

  it('closes the account against the real mock: one authorisation, spent once, list tag invalidated', async () => {
    /*
      M5 -> D9 on the real handlers: 201 then 200 "Account deleted successfully", authorisation
      Consumed. "List tag invalidated" is OBSERVED, not assumed: an accounts query is subscribed
      before the flow and a second GET /api/accounts must follow the 200 — remove the mutation's
      invalidatesTags and `lists` stays 0. (`mockState.accounts` no longer holding the row is the
      mock modelling the API's soft-delete filter — D9 records the 200, the IsDeleted flag and the
      Consumed row, not a GET of the listing.)
    */
    seedTempFund();
    const store = makeTestStore();
    await store.dispatch(apiSlice.endpoints.getAccounts.initiate()).unwrap();
    const gets = countListGets();
    const { onClose } = renderDialog(TEMP_FUND, store);
    await reachPinStep();
    await enterPin();

    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(gets.lists).toBe(1)); // the success-path invalidatesTags refetch
    expect(mockState.accounts.some((a) => a.id === TEMP_FUND.id)).toBe(false);
    const minted = [...mockState.stepUpAuthorizations.values()];
    expect(minted).toHaveLength(1);
    expect(minted[0]).toMatchObject({
      operation: 'AccountDeletion',
      fromAccountId: TEMP_FUND.id,
      amount: 0,
      consumed: true,
    });
  });

  it('a five-digit paste does not send', async () => {
    // Five digits leave onComplete unfired — the only honest "on the PIN step, not yet sent".
    seedTempFund();
    const seen = observeRequests();
    const { onClose } = renderDialog();
    await reachPinStep();
    await enterPin('12345');

    expect(screen.getByLabelText('Digit 5 of 6')).toHaveValue('5');
    expect(seen).toHaveLength(0);
    expect(onClose).not.toHaveBeenCalled();
  });

  it('wrong PIN reddens and clears the boxes, and keeps the session', async () => {
    /*
      Measured M1: 401 INVALID_PIN "Invalid PIN." on the mint — the mint's ONLY 401. Reading the
      boxes rather than a banner is deliberate: emptying them is something ONLY the INVALID_PIN
      branch does. The store is BOOTED to 'authenticated' first, or the session half cannot fail:
      sessionMiddleware only reacts at that status, and this is the assertion that turns red when
      INVALID_PIN leaves IN_FLOW_401_CODES.
    */
    seedTempFund();
    const store = makeTestStore();
    await store.dispatch(apiSlice.endpoints.getMe.initiate()).unwrap();
    expect(store.getState().auth.status).toBe('authenticated');
    server.use(
      http.post(MINT_URL, () =>
        problem({ status: 401, errorCode: 'INVALID_PIN', detail: 'Invalid PIN.' }),
      ),
    );
    const { onClose } = renderDialog(TEMP_FUND, store);
    await reachPinStep();
    await enterPin();

    await waitFor(() => expect(screen.getByLabelText('Digit 1 of 6')).toHaveValue(''));
    expect(screen.getByLabelText('Digit 1 of 6')).toHaveAttribute('aria-invalid', 'true');
    expect(await screen.findByText('Invalid PIN.')).toBeInTheDocument();
    expect(screen.getByRole('dialog', { name: 'Delete account?' })).toBeInTheDocument();
    expect(onClose).not.toHaveBeenCalled();
    expect(store.getState().auth.status).toBe('authenticated');
  });

  it('PIN_LOCKED disables the boxes and shows the server horizon', async () => {
    // 429 with retryAfterSeconds 120 — NOT provoked on the deletion mint in the after-run (one
    // wrong PIN, M1, counter back to 0); the shape is checkPinInBand's, measured on the transfer
    // mints 2026-08-16. 120 s from the server, not the 15-minute client default.
    seedTempFund();
    server.use(
      http.post(MINT_URL, () =>
        problem({
          status: 429,
          errorCode: 'PIN_LOCKED',
          detail: 'Too many attempts.',
          extensions: { retryAfterSeconds: 120 },
        }),
      ),
    );
    renderDialog();
    await reachPinStep();
    await enterPin();

    expect(await screen.findByText(/Too many incorrect PIN attempts/)).toBeInTheDocument();
    expect(screen.getByRole('timer')).toHaveTextContent('Try again in 2:00');
    expect(screen.getByLabelText('Digit 1 of 6')).toBeDisabled();
  });

  it('the lock expires — the PIN boxes come back once the window closes', async () => {
    seedTempFund();
    server.use(
      http.post(MINT_URL, () =>
        problem({
          status: 429,
          errorCode: 'PIN_LOCKED',
          detail: 'Too many attempts.',
          extensions: { retryAfterSeconds: 5 },
        }),
      ),
    );
    renderDialog();
    await reachPinStep();
    await enterPin();

    // Installed AFTER the lock appears, as transfer-pin-recovery.test.tsx does: before this no
    // countdown exists, so nothing can tick outside act(...).
    expect(await screen.findByText(/Too many incorrect PIN attempts/)).toBeInTheDocument();
    vi.useFakeTimers({ shouldAdvanceTime: true });

    await act(async () => {
      vi.advanceTimersByTime(6000);
    });

    await waitFor(() =>
      expect(screen.queryByText(/Too many incorrect PIN attempts/)).not.toBeInTheDocument(),
    );
    expect(screen.getByLabelText('Digit 1 of 6')).toBeEnabled();
  });

  it('AUTHORIZATION_EXPIRED on the DELETE re-prompts without red and keeps the session, and the next completion mints again', async () => {
    /*
      Measured E2: 401 AUTHORIZATION_EXPIRED "This authorisation has expired. Enter your PIN again
      to confirm.", PinAccessFailedCount 0/0, /bff/auth/me 200 after. The detail is rendered
      verbatim (no transfer-worded sentence), the boxes are cleared WITHOUT aria-invalid, and —
      because the minted id was a local — the next six digits go down the mint branch again: two
      mints, two DELETEs, a different header on the second. `once: true` retires the override so
      the second DELETE reaches the real handler and actually closes the account. The store is
      booted to 'authenticated' so the session assertion can fail: this is the shape that pins
      AUTHORIZATION_EXPIRED's place in IN_FLOW_401_CODES from this surface (it is the DELETE's
      code — the mint never emits it, M1 is the mint's only 401).
    */
    seedTempFund();
    const store = makeTestStore();
    await store.dispatch(apiSlice.endpoints.getMe.initiate()).unwrap();
    expect(store.getState().auth.status).toBe('authenticated');
    const seen = observeRequests();
    server.use(
      http.delete(
        ACCOUNT_URL,
        () =>
          problem({
            status: 401,
            errorCode: 'AUTHORIZATION_EXPIRED',
            detail: 'This authorisation has expired. Enter your PIN again to confirm.',
          }),
        { once: true },
      ),
    );
    const { onClose } = renderDialog(TEMP_FUND, store);
    await reachPinStep();
    await enterPin();

    expect(
      await screen.findByText('This authorisation has expired. Enter your PIN again to confirm.'),
    ).toBeInTheDocument();
    await waitFor(() => expect(screen.getByLabelText('Digit 1 of 6')).toHaveValue(''));
    expect(screen.getByLabelText('Digit 1 of 6')).not.toHaveAttribute('aria-invalid');
    expect(onClose).not.toHaveBeenCalled();
    expect(store.getState().auth.status).toBe('authenticated');

    await enterPin();
    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));

    expect(mockState.stepUpAuthorizations.size).toBe(2);
    const deletes = seen.filter((r) => r.method === 'DELETE');
    const mints = seen.filter((r) => r.method === 'POST');
    expect(mints).toHaveLength(2);
    expect(deletes).toHaveLength(2);
    expect(deletes[1].header, 'a FRESH authorisation, not the refused one').not.toBe(
      deletes[0].header,
    );
    expect(deletes[1].header).toMatch(/^[0-9a-f-]{36}$/i);
    expect(mockState.stepUpAuthorizations.get(deletes[1].header as string)?.consumed).toBe(true);
    expect(mockState.accounts.some((a) => a.id === TEMP_FUND.id)).toBe(false);
  });

  it('PIN_REQUIRED navigates to /pin-setup?returnTo=/accounts', async () => {
    /*
      Measured M0: 422 PIN_REQUIRED before anything is spent. The returnTo carries a PATH and the
      query string carries NOTHING else — asserted on the router's location and on the probe's raw
      `search` with an exact match (`toBe`, not jest-dom's substring `toHaveTextContent`), so a
      dropped returnTo, a wrong one, or PIN digits riding along all fail.
    */
    seedTempFund();
    server.use(
      http.post(MINT_URL, () =>
        problem({
          status: 422,
          errorCode: 'PIN_REQUIRED',
          detail: 'PIN must be set before authorising this operation.',
        }),
      ),
    );
    const { router } = renderDialog();
    await reachPinStep();
    await enterPin();

    expect(await screen.findByText('PIN SETUP PAGE')).toBeInTheDocument();
    expect(router.state.location.pathname + router.state.location.search).toBe(
      '/pin-setup?returnTo=/accounts',
    );
    expect(screen.getByTestId('pin-setup-search').textContent).toBe('?returnTo=/accounts');
    expect(router.state.location.search).not.toContain(TEST_PIN);
  });

  it('PIN_REQUIRED from the real mock (no PIN enrolled) navigates the same way — M0', async () => {
    // The mock's checkPinInBand reads mockState.session.hasPin (seeded true by test/setup.ts);
    // flipping it is what the real M0 row looks like from here.
    seedTempFund();
    mockState.session!.hasPin = false;
    const { router } = renderDialog();
    await reachPinStep();
    await enterPin();

    expect(await screen.findByText('PIN SETUP PAGE')).toBeInTheDocument();
    expect(router.state.location.pathname + router.state.location.search).toBe(
      '/pin-setup?returnTo=/accounts',
    );
    expect(screen.getByTestId('pin-setup-search').textContent).toBe('?returnTo=/accounts');
    expect(mockState.stepUpAuthorizations.size).toBe(0);
  });

  it('NON_ZERO_BALANCE from the mint keeps the dialog open with the mapped sentence and spends no attempt', async () => {
    /*
      Measured M2, sent with a WRONG pin on purpose: 422 NON_ZERO_BALANCE, PinAccessFailedCount
      unchanged — the guard answers before the PIN is consulted. The PIN here is deliberately
      WRONG for the same reason: with the correct one, checkPinInBand resets the counter to 0 on
      a match and `pinAttempts === 0` holds in EITHER order. With '000000' a mock that consulted
      the PIN first answers 401 INVALID_PIN — sentence absent, boxes red, counter 1 — so every
      assertion below fails for the reason this comment gives. Real mock, seeded funded account.
    */
    const rainyDay = mockState.accounts[1];
    expect(rainyDay.balance).toBe(830);
    const { onClose } = renderDialog({ id: rainyDay.id, name: rainyDay.name });
    await reachPinStep();
    await enterPin('000000');

    expect(
      await screen.findByText('Only accounts with a zero balance can be deleted.'),
    ).toBeInTheDocument();
    expect(screen.queryByText('Invalid PIN.')).toBeNull();
    expect(screen.getByRole('dialog', { name: 'Delete account?' })).toBeInTheDocument();
    expect(screen.getByLabelText('Digit 1 of 6')).not.toHaveAttribute('aria-invalid');
    expect(mockState.pinAttempts).toBe(0);
    expect(mockState.stepUpAuthorizations.size).toBe(0);
    expect(onClose).not.toHaveBeenCalled();
  });

  it('PRIMARY_ACCOUNT_DELETE from the mint keeps the dialog open with its own sentence — M3', async () => {
    /*
      Measured M3 with the CORRECT pin: 422 PRIMARY_ACCOUNT_DELETE. The pin sent here is WRONG so
      the `pinAttempts === 0` line can fail: it pins the mock's shared refuseIfNotClosable-before-
      checkPinInBand order (handlers.ts authoriseAccountDeletion, the order M2 measured on the
      balance guard) — a mock that consulted the PIN first would answer 401 INVALID_PIN and the
      counter would read 1. The wrong-pin-on-primary combination itself is NOT a measured row.
    */
    const primary = mockState.accounts.find((a) => a.isPrimary)!;
    primary.balance = 0;
    const { onClose } = renderDialog({ id: primary.id, name: primary.name });
    await reachPinStep();
    await enterPin('000000');

    expect(
      await screen.findByText(
        'This is your primary account — set another account as primary first.',
      ),
    ).toBeInTheDocument();
    expect(screen.queryByText('Invalid PIN.')).toBeNull();
    expect(screen.getByRole('dialog', { name: 'Delete account?' })).toBeInTheDocument();
    expect(screen.getByLabelText('Digit 1 of 6')).not.toHaveAttribute('aria-invalid');
    expect(mockState.pinAttempts).toBe(0);
    expect(mockState.stepUpAuthorizations.size).toBe(0);
    expect(onClose).not.toHaveBeenCalled();
  });

  it('ACCOUNT_NOT_FOUND on the DELETE closes and refetches the list', async () => {
    /*
      Measured D10/D11: 404 ACCOUNT_NOT_FOUND once the account is gone. The dialog treats it as
      already closed — and because the mutation invalidates nothing on error, it invalidates the
      LIST tag by hand. Observed through a subscribed accounts query: a second GET goes out.
    */
    seedTempFund();
    const store = makeTestStore();
    // Subscribe (and keep the subscription) so an invalidation has something to refetch.
    await store.dispatch(apiSlice.endpoints.getAccounts.initiate()).unwrap();
    const gets = countListGets();
    server.use(
      http.delete(ACCOUNT_URL, ({ request }) =>
        problem({
          status: 404,
          errorCode: 'ACCOUNT_NOT_FOUND',
          detail: `Account with identifier '${TEMP_FUND.id}' was not found.`,
          instance: new URL(request.url).pathname,
        }),
      ),
    );
    const { onClose } = renderDialog(TEMP_FUND, store);
    await reachPinStep();
    await enterPin();

    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(gets.lists).toBe(1)); // the by-hand invalidateTags refetch
  });

  it('a transport failure on the mint shows the connection sentence and clears the boxes', async () => {
    seedTempFund();
    server.use(http.post(MINT_URL, () => HttpResponse.error()));
    const { onClose } = renderDialog();
    await reachPinStep();
    await enterPin();

    expect(await screen.findByText(/Couldn't reach the server/)).toBeInTheDocument();
    await waitFor(() => expect(screen.getByLabelText('Digit 1 of 6')).toHaveValue(''));
    expect(screen.getByLabelText('Digit 1 of 6')).not.toHaveAttribute('aria-invalid');
    expect(onClose).not.toHaveBeenCalled();
  });

  it('an errorCode-less 401 on the mint IS a dead session — auth flips to expired', async () => {
    /*
      The BFF's own 401 shape (auth.test.tsx: ProblemDetails WITHOUT errorCode) -> HTTP_401 in
      problemBaseQuery -> sessionMiddleware signs out; the dialog's fallback branch just shows the
      detail on a tree ProtectedRoute is about to unmount. BOOT THE STORE TO 'authenticated' FIRST,
      or this cannot fail: the middleware only reacts at that status, and a fresh test store sits
      at 'unknown' (transfer-step-up.test.tsx records the version of this test that pinned
      nothing). Falsified by adding 'HTTP_401' to IN_FLOW_401_CODES or by the fallback swallowing
      the rejection. The in-flow codes the rail does emit are pinned above, on the rows they
      belong to: INVALID_PIN on the mint (M1), AUTHORIZATION_EXPIRED on the DELETE (E2).
    */
    seedTempFund();
    const store = makeTestStore();
    await store.dispatch(apiSlice.endpoints.getMe.initiate()).unwrap();
    expect(store.getState().auth.status).toBe('authenticated');
    server.use(
      http.post(MINT_URL, () =>
        problem({ status: 401, title: 'Unauthorized', detail: 'Session expired' }),
      ),
    );
    renderDialog(TEMP_FUND, store);
    await reachPinStep();
    await enterPin();

    await waitFor(() => expect(store.getState().auth.status).toBe('expired'));
  });

  it('a refusal the ladder has no branch for clears the boxes, so the next keystroke is not a submit', async () => {
    /*
      The fallback branch (5xx, 403 ACCESS_DENIED per D14/D15, the BFF limiter's 429). Canned
      500 'boom': after the banner every box must be EMPTY and ENABLED, and a change on box 1 must
      be an append into an empty PinInput — NOT a second mint. Before the fix the six boxes kept
      the PIN with the dialog idle, and PinInput.handleChange's overwrite path (index <
      value.length) emitted a 6-char value on the first keystroke, i.e. re-submitted a hybrid PIN.
    */
    seedTempFund();
    const seen = observeRequests();
    server.use(
      http.post(MINT_URL, () => problem({ status: 500, errorCode: 'HTTP_500', detail: 'boom' })),
    );
    const { onClose } = renderDialog();
    await reachPinStep();
    await enterPin();

    expect(await screen.findByText('boom')).toBeInTheDocument();
    await waitFor(() => expect(screen.getByLabelText('Digit 1 of 6')).toHaveValue(''));
    for (const box of pinBoxes()) {
      expect(box).toHaveValue('');
      expect(box).toBeEnabled();
    }
    expect(screen.getByLabelText('Digit 1 of 6')).not.toHaveAttribute('aria-invalid');
    await waitFor(() => expect(seen.filter((r) => r.method === 'POST')).toHaveLength(1));

    fireEvent.change(screen.getByLabelText('Digit 1 of 6'), { target: { value: '9' } });

    expect(screen.getByLabelText('Digit 1 of 6')).toHaveValue('9');
    // Give a would-be second mint every chance to start before asserting it did not.
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 50));
    });
    expect(seen.filter((r) => r.method === 'POST')).toHaveLength(1);
    expect(onClose).not.toHaveBeenCalled();
  });

  it('Escape and the close button are refused while the mint is held open', async () => {
    /*
      Hold the mint response open, then aim Escape AT the surface: `userEvent.keyboard` targets
      `document.activeElement`, which is a disabled PIN box that swallows it — and PinInput refuses
      a paste while disabled, so without the held promise this would pass for the wrong reason.
    */
    seedTempFund();
    let release!: () => void;
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(
      http.post(MINT_URL, async () => {
        await held;
        return problem({ status: 500, errorCode: 'HTTP_500', detail: 'released' });
      }),
    );
    const { onClose } = renderDialog();
    const dialog = await reachPinStep();
    await enterPin();

    try {
      await screen.findByLabelText('Deleting account');
      expect(screen.getByRole('button', { name: 'Close' })).toBeDisabled();
      expect(screen.getByRole('button', { name: 'Cancel' })).toBeDisabled();
      await userEvent.type(dialog, '{Escape}');
      expect(screen.getByRole('dialog', { name: 'Delete account?' })).toBeInTheDocument();
      expect(onClose).not.toHaveBeenCalled();
    } finally {
      // The handler is parked on this promise; an assertion failure must not leave it hanging.
      release();
    }
    await screen.findByText('released');
  });
});
