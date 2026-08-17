import { cleanup, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { Route, Routes } from 'react-router-dom';
import { server } from '../mocks/server';
import { problem } from '../mocks/problem';
import { mockState, seedMockSession } from '../mocks/state';
import { makeTestStore, renderWithProviders } from '../test/renderWithProviders';
import { apiSlice } from '../features/api/apiSlice';
import { enterPin } from '../test/pinFlow';
import { TransferPage } from './TransferPage';
import { InternalTransferPage } from './InternalTransferPage';

afterEach(cleanup);

/**
 * ADR-0042 from the client's side: the PIN mints an authorisation bound to this amount and this
 * payee, and the transfer presents it in a header.
 *
 * Everything here asserts behaviour that did not exist before this PR. The pre-existing suites were
 * updated to keep passing, which proves nothing was BROKEN; this file is what proves something was
 * BUILT. Each test is written so that deleting the line it defends turns it red — the header
 * assertion reads the header rather than the 201, and the expiry assertion reads the FORM FIELDS
 * rather than the absence of a navigation.
 */

function renderTransfer() {
  return renderWithProviders(
    <Routes>
      <Route path="/" element={<TransferPage />} />
      <Route path="/pin-setup" element={<div>PIN SETUP</div>} />
    </Routes>,
    { routerEntries: ['/'] },
  );
}

function renderInternal() {
  return renderWithProviders(
    <Routes>
      <Route path="/" element={<InternalTransferPage />} />
      <Route path="/pin-setup" element={<div>PIN SETUP</div>} />
    </Routes>,
    { routerEntries: ['/'] },
  );
}

/** Fill step 1 and step 2 for an external transfer, stopping BEFORE the PIN. */
async function externalToPinStep(amount = '50') {
  await screen.findByText('Main Account');
  await userEvent.type(screen.getByLabelText('Recipient handle'), 'friend');
  await userEvent.click(screen.getByRole('button', { name: 'Verify' }));
  await screen.findByText('A. Friend');
  await userEvent.type(screen.getByLabelText('Transfer amount'), amount);
  await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
  await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
}

beforeEach(() => {
  seedMockSession();
});

describe('the client mints an authorisation and presents it', () => {
  it('mints exactly one authorisation and spends it — end to end, through the real mock', async () => {
    /*
      Asserts the STATE the protocol leaves behind rather than a status code, and that is the point:
      a transfer that succeeded without ever minting would return the same 201, so a status
      assertion would pass on a client that never wired any of this.

      `consumed === true` is the strong half. Only the authorisation whose id the client actually
      presented in the header can be marked spent, so this proves mint → present → consume as one
      chain without needing to intercept anything.

      No interception here at all, deliberately: calling `fetch` inside an MSW override re-enters
      the same override, which kills the worker rather than failing the test. Reading the mock's own
      store is both safer and a stronger claim.
    */
    renderTransfer();
    await externalToPinStep();
    await enterPin();

    expect(await screen.findByText('Transfer Sent!')).toBeInTheDocument();

    const minted = [...mockState.stepUpAuthorizations.values()];
    expect(minted).toHaveLength(1);
    expect(minted[0].consumed).toBe(true);
    expect(minted[0].operation).toBe('Transfer');
    expect(minted[0].amount).toBe(50);
  });

  it('puts the authorisation in the Step-Up-Authorization HEADER, never in the body', async () => {
    /*
      The header placement is a decision the server measured into existence: it fingerprints the
      request BODY alone, so an authorisation carried in the body would make every retry a 422.
      This pins the wire shape on the client side, where a refactor could quietly move it.
    */
    let header: string | null = null;
    let bodyText = '';
    server.use(
      http.post('*/api/transfers', async ({ request }) => {
        header = request.headers.get('Step-Up-Authorization');
        bodyText = await request.text();
        // A canned REFUSAL, and deliberately so: this override REPLACES the real handler, so it
        // must not call through, and the test needs nothing after the header is captured. The
        // status is irrelevant to what is asserted — only that the flow stops here.
        return problem({ status: 401, errorCode: 'AUTHORIZATION_INVALID', detail: 'stop here' });
      }),
    );

    renderTransfer();
    await externalToPinStep();
    await enterPin();

    await waitFor(() => expect(header).toBeTruthy());
    expect(header).toMatch(/^[0-9a-f-]{36}$/i);
    expect(bodyText).not.toContain(header as unknown as string);
    expect(bodyText).not.toMatch(/stepUpAuthorization/i);
  });

  it('completes on the sixth digit, with no Send control anywhere on the step', async () => {
    renderTransfer();
    await externalToPinStep();

    // Present BEFORE the PIN: the step renders no Send affordance at all, so the only thing that
    // can submit is the input itself.
    expect(screen.queryByRole('button', { name: /^Send/ })).not.toBeInTheDocument();

    await enterPin();
    expect(await screen.findByText('Transfer Sent!')).toBeInTheDocument();
  });

  it('treats a wrong PIN at MINT time exactly as one at send time', async () => {
    /*
      The mint is a second place a PIN can be refused, and the page routes both through one
      `handleRefusal`. Without that, a wrong PIN would clear the boxes when the SEND refused it and
      leave them full when the MINT did — the same mistake with two different recoveries.
    */
    server.use(
      http.post('*/api/transfers/authorizations', () =>
        problem({ status: 401, errorCode: 'INVALID_PIN', detail: 'Invalid PIN.' }),
      ),
    );

    renderTransfer();
    await externalToPinStep();
    await enterPin('999999');

    // Emptied boxes are something ONLY the INVALID_PIN branch does — a banner could come from the
    // generic failure path and would pass even if that branch never ran.
    await waitFor(() => {
      expect(screen.getByLabelText('Digit 1 of 6')).toHaveValue('');
    });
    expect(screen.queryByText('Transfer Sent!')).not.toBeInTheDocument();
  });
});

describe('what an expired authorisation does to the screen', () => {
  it('keeps the amount and the payee, clears only the PIN, and says the confirmation expired', async () => {
    /*
      WCAG 2.2 SC 3.3.7 Redundant Entry is LEVEL A, and its exception covers security information
      only. So this asserts the FIELD VALUES, not the absence of a navigation: a page that dropped
      the user back to an empty form would also "not navigate away", and would still be a Level A
      failure.
    */
    server.use(
      http.post('*/api/transfers', () =>
        problem({
          status: 401,
          errorCode: 'AUTHORIZATION_EXPIRED',
          detail: 'This authorisation has expired. Enter your PIN again to confirm.',
        }),
      ),
    );

    renderTransfer();
    await externalToPinStep();
    await enterPin();

    expect(await screen.findByText(/your confirmation expired/i)).toBeInTheDocument();

    // The PIN is gone — it is the one thing the exception covers.
    await waitFor(() => {
      expect(screen.getByLabelText('Digit 1 of 6')).toHaveValue('');
    });

    /*
      And the details survived — asserted by USING them rather than by reading them back.

      This test used to press Back twice and read the two form fields. It cannot any more, and the
      reason is the point of this PR: a refused authorisation now KEEPS the idempotency key, so
      `exitLocked` holds every backward exit. That is deliberate — the retry must stay the same
      intent — but it means "the values are still there" can no longer be shown by navigating to
      them.

      Using them is the stronger claim anyway. A page that merely re-rendered the old numbers would
      satisfy a field read; only a page that still HOLDS them can put them back on the wire.
    */
    let retried: { amount?: number; recipientAzureTag?: string } = {};
    server.use(
      http.post('*/api/transfers', async ({ request }) => {
        retried = (await request.json()) as typeof retried;
        return problem({ status: 401, errorCode: 'AUTHORIZATION_EXPIRED', detail: 'again' });
      }),
    );
    await enterPin();

    await waitFor(() => expect(retried.amount).toBe(50));
    expect(retried.recipientAzureTag).toBe('friend');
  });

  it('says nothing about WHICH cause when the server answers the uniform refusal', async () => {
    // The server deliberately answers AUTHORIZATION_INVALID for unknown / not-yours /
    // already-spent / wrong-binding so as not to be an oracle. The client must not guess either.
    server.use(
      http.post('*/api/transfers', () =>
        problem({
          status: 401,
          errorCode: 'AUTHORIZATION_INVALID',
          detail: 'This authorisation cannot be used.',
        }),
      ),
    );

    renderTransfer();
    await externalToPinStep();
    await enterPin();

    const banner = await screen.findByText(/that confirmation can no longer be used/i);
    expect(banner).toBeInTheDocument();
    expect(banner.textContent).not.toMatch(/expired|already|spent|amount|payee/i);
  });
});

describe('the internal transfer speaks the same protocol', () => {
  it('mints against its own endpoint and spends it too', async () => {
    renderInternal();
    // The account NAME appears on both pickers, so it is ambiguous; the picker BUTTON is not.
    await screen.findByRole('button', { name: 'From Main Account' });
    await userEvent.click(screen.getByRole('button', { name: 'To Rainy Day' }));
    await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
    await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
    await enterPin();

    expect(await screen.findByText('Transfer Complete!')).toBeInTheDocument();

    const minted = [...mockState.stepUpAuthorizations.values()];
    expect(minted).toHaveLength(1);
    expect(minted[0].consumed).toBe(true);
    // Bound to the OPERATION as well: an authorisation for an internal move must never be
    // spendable on an external one, and the mock refuses on exactly that field.
    expect(minted[0].operation).toBe('InternalTransfer');
  });
});

describe('a lost response, and the way back to the PIN', () => {
  /*
    #211. The four rows the server can answer to the SAME key and the SAME body — measured on the
    running API and written down in `A2-PR3-MEASURED-CONTRACT.md`:

      Completed                -> 201 + Idempotency-Replayed: true    done, no PIN
      Executed, fresh          -> 409 IDEMPOTENCY_IN_FLIGHT           wait, no PIN
      Executed, stale > 10 min -> 409 IDEMPOTENCY_RESULT_UNKNOWN      never re-execute
      record absent / released -> 401 AUTHORIZATION_EXPIRED           nothing moved; NOW ask a PIN

    Only the last needs a PIN, and by then the server has said nothing happened.
  */

  /** A transport failure: the request left, the answer never came. `status: 'NETWORK'`. */
  function loseTheResponse() {
    server.use(http.post('*/api/transfers', () => HttpResponse.error(), { once: true }));
  }

  it('offers a way to re-send when the response is LOST, not only on a 409', async () => {
    /*
      The control existed and did not render for the case it was built for.

      It was gated on `inFlight`, which is set only for 409 IDEMPOTENCY_IN_FLIGHT. A lost response
      is `status: 'NETWORK'`, which `classifyMoneyProblem` routes to a plain message — so the one
      re-send affordance stayed hidden while every exit was held by `exitLocked`. Not a loop: a
      dead end, with the key retained and nothing on screen able to use it.

      Falsified by putting the gate back to `{inFlight && (`.
    */
    renderTransfer();
    await externalToPinStep();
    loseTheResponse();
    await enterPin();

    expect(await screen.findByRole('button', { name: 'Check again' })).toBeInTheDocument();
    // And it says the honest thing: nobody told us it is processing, so we do not claim it is.
    expect(screen.getByText(/may or may not have gone through/i)).toBeInTheDocument();
  });

  it('re-sends the SAME key and the SAME body, and lands the transfer', async () => {
    /*
      The whole point of retaining the key. Asserts the KEY, not the outcome: a client that quietly
      minted a fresh key would also reach a 201 here, and would have made a second payment.
    */
    const keys: string[] = [];
    server.use(
      http.post('*/api/transfers', async ({ request }) => {
        keys.push(request.headers.get('Idempotency-Key') ?? '');
        return HttpResponse.error();
      }),
    );

    renderTransfer();
    await externalToPinStep();
    await enterPin();
    await screen.findByRole('button', { name: 'Check again' });

    server.resetHandlers();
    server.use(
      http.post('*/api/transfers', async ({ request }) => {
        keys.push(request.headers.get('Idempotency-Key') ?? '');
        return problem({ status: 401, errorCode: 'AUTHORIZATION_INVALID', detail: 'stop' });
      }),
    );
    await userEvent.click(screen.getByRole('button', { name: 'Check again' }));

    await waitFor(() => expect(keys).toHaveLength(2));
    expect(keys[0]).toMatch(/^[0-9a-f-]{36}$/i);
    expect(keys[1], 'the retry must reuse the key, or it is a second payment').toBe(keys[0]);
  });

  it('after AUTHORIZATION_EXPIRED the next PIN mints a FRESH authorisation, same key', async () => {
    /*
      Row 4, and the property #211 exists for.

      Before this PR the page re-presented `lastAuthorization.current` whenever a key was live, so
      the six digits the user kept typing would have re-sent the dead authorisation forever. The
      refusal now drops it, which sends the next completion down the mint branch while `submit`'s
      `keyRef.current ??=` keeps the key.

      Both halves are asserted because either alone is satisfiable by a wrong implementation: a new
      authorisation with a new key is a second payment, and the same key with the same authorisation
      is the loop.

      Falsified by deleting `lastAuthorization.current = null` from handleRefusal's expired branch.
    */
    const seen: { key: string; auth: string }[] = [];
    server.use(
      http.post('*/api/transfers', async ({ request }) => {
        seen.push({
          key: request.headers.get('Idempotency-Key') ?? '',
          auth: request.headers.get('Step-Up-Authorization') ?? '',
        });
        return problem({
          status: 401,
          errorCode: 'AUTHORIZATION_EXPIRED',
          detail: 'This authorisation has expired. Enter your PIN again to confirm.',
        });
      }),
    );

    renderTransfer();
    await externalToPinStep();
    await enterPin();
    await screen.findByText(/your confirmation expired/i);

    // The boxes are empty, so six more digits are a NEW completion rather than a no-op.
    await waitFor(() => expect(screen.getByLabelText('Digit 1 of 6')).toHaveValue(''));
    await enterPin();

    await waitFor(() => expect(seen).toHaveLength(2));
    expect(seen[1].key, 'same intent, same key').toBe(seen[0].key);
    expect(seen[1].auth, 'a FRESH authorisation, not the refused one').not.toBe(seen[0].auth);
    expect(seen[1].auth).toMatch(/^[0-9a-f-]{36}$/i);
  });

  it('does not sign the user out — an authorisation 401 is not a dead session', async () => {
    /*
      The sharpest defect this PR fixes, and the one a user would have met first.

      Both authorisation refusals are 401s, and `sessionMiddleware` treated every 401 that was not
      INVALID_PIN or INVALID_CREDENTIALS as a dead session: `sessionExpired()` + `resetApiState()`,
      then `ProtectedRoute` to /login — and the wizard's exit blocker exempts /login, so the user
      was not even asked about a payment whose outcome they did not know.

      The session was never in question. `IdempotencyMiddleware` sits after UseAuthentication, so
      reaching the step-up check at all proves the cookie was accepted.

      Falsified by removing either code from IN_FLOW_401_CODES.
    */
    server.use(
      http.post('*/api/transfers', () =>
        problem({ status: 401, errorCode: 'AUTHORIZATION_EXPIRED', detail: 'expired' }),
      ),
    );

    /*
      BOOT THE STORE TO 'authenticated' FIRST, or this test cannot fail.

      `sessionMiddleware` only reacts when `auth.status === 'authenticated'`, and a fresh test store
      sits at 'unknown'. The first version of this test skipped the boot and stayed green with the
      fix reverted — a test that pins nothing. Caught by running the falsification probe and finding
      it did NOT bite, which is the only reason to run them.

      The boot is the same one `policies.test.tsx` uses for the same reason: dispatch `getMe`
      against the seeded mock session and assert the status flipped.
    */
    const store = makeTestStore();
    await store.dispatch(apiSlice.endpoints.getMe.initiate()).unwrap();
    expect(store.getState().auth.status).toBe('authenticated');
    renderWithProviders(
      <Routes>
        <Route path="/" element={<TransferPage />} />
        <Route path="/login" element={<div>LOGIN</div>} />
      </Routes>,
      { routerEntries: ['/'], store },
    );
    await externalToPinStep();
    await enterPin();

    await screen.findByText(/your confirmation expired/i);
    // The user is still on the PIN step of the transfer, still authenticated.
    expect(screen.getByLabelText('Digit 1 of 6')).toBeInTheDocument();
    expect(screen.queryByText('LOGIN')).not.toBeInTheDocument();
    // The assertion that bites: without the exemption this reads 'expired'.
    expect(store.getState().auth.status).toBe('authenticated');
  });

  it('says something DIFFERENT when the authorisation was never granted', async () => {
    // Expired and unusable are distinct server answers and must stay distinct sentences: one asks
    // for six digits again, the other says the confirmation cannot be used at all.
    server.use(
      http.post('*/api/transfers', () =>
        problem({ status: 401, errorCode: 'AUTHORIZATION_INVALID', detail: 'nope' }),
      ),
    );

    renderTransfer();
    await externalToPinStep();
    await enterPin();

    expect(await screen.findByText(/can no longer be used/i)).toBeInTheDocument();
    expect(screen.queryByText(/your confirmation expired/i)).not.toBeInTheDocument();
  });
});
