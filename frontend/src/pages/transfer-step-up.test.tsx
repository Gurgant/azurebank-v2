import { cleanup, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { http } from 'msw';
import { Route, Routes } from 'react-router-dom';
import { server } from '../mocks/server';
import { problem } from '../mocks/problem';
import { mockState, seedMockSession } from '../mocks/state';
import { renderWithProviders } from '../test/renderWithProviders';
import { TEST_PIN, enterPin } from '../test/pinFlow';
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

async function completePin(pin = TEST_PIN) {
  await enterPin(pin);
}

/** One step back in the wizard. See the note at the call site for why the FIRST match. */
async function clickBack() {
  await userEvent.click(screen.getAllByRole('button', { name: 'Back' })[0]);
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
    await completePin();

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
        // A canned success — this override REPLACES the real handler, so it must not call through.
        return problem({ status: 401, errorCode: 'AUTHORIZATION_INVALID', detail: 'stop here' });
      }),
    );

    renderTransfer();
    await externalToPinStep();
    await completePin();

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

    await completePin();
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
    await completePin('999999');

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
    await completePin();

    expect(await screen.findByText(/your confirmation expired/i)).toBeInTheDocument();

    // The PIN is gone — it is the one thing the exception covers.
    await waitFor(() => {
      expect(screen.getByLabelText('Digit 1 of 6')).toHaveValue('');
    });

    // ...and the transfer details are still on screen, so the user re-enters six digits, not a form.
    // Two controls answer to "Back" on every step — the header's icon and the body's button — and
    // both run the same `wizard.toReview()` / `wizard.toForm()`. Taking the first is not a
    // preference; a `getByRole` would fail as ambiguous, which is what it did before this comment.
    await clickBack(); // pin -> review
    await clickBack(); // review -> form
    expect(screen.getByLabelText('Transfer amount')).toHaveValue('50');
    expect(screen.getByLabelText('Recipient handle')).toHaveValue('friend');
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
    await completePin();

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
    await completePin();

    expect(await screen.findByText('Transfer Complete!')).toBeInTheDocument();

    const minted = [...mockState.stepUpAuthorizations.values()];
    expect(minted).toHaveLength(1);
    expect(minted[0].consumed).toBe(true);
    // Bound to the OPERATION as well: an authorisation for an internal move must never be
    // spendable on an external one, and the mock refuses on exactly that field.
    expect(minted[0].operation).toBe('InternalTransfer');
  });
});
