import { Route, Routes } from 'react-router-dom';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { confirmWithPin } from '../test/pinFlow';
import { describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '../mocks/server';
import { problem } from '../mocks/problem';
import { renderWithProviders } from '../test/renderWithProviders';
import { StepUpModal } from '../features/auth';
import { InternalTransferPage } from './InternalTransferPage';
import { mockState, seedMockSession } from '../mocks/state';

/**
 * PR-11b — internal transfer between the caller's OWN accounts, PIN collected in the page
 * (ADR-0041/0042): pick source + destination (can't be the same), amount, review, PIN → the
 * authorisation is minted and spent on Send → receipt. Until ADR-0041 Send met a level-2 403 and
 * the root StepUpModal collected the PIN; it stays mounted below to prove it no longer appears.
 */

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

/*
  A session, seeded, because the step-up routes now demand one.

  The mock used to answer /bff/auth/verify-pin for a caller with NO session at all, so these
  tests never had to establish one and quietly exercised a state the product cannot reach: the
  real BFF reads the session first and answers 401. Aligning the mock (contract gate) made that
  visible. Seeding is the faithful fix — a user reaching a PIN prompt is, by construction,
  already signed in.
*/
beforeEach(() => {
  seedMockSession();
});

/**
 * T11, on a wizard page. `balance-guard.test.tsx` proves the shared pieces against the withdraw
 * dialog; this proves the WIRING here, which is per-page and is the part that can silently be
 * missing: the gate has to run on the review screen's Continue, and its refusal has to reach the
 * banner through `wizard.fail` rather than vanishing.
 */
describe('the balance guard on an internal transfer', () => {
  it('does not reach the PIN step when the server says the source balance has moved', async () => {
    let reads = 0;
    server.use(
      http.get('*/api/accounts', () => {
        reads += 1;
        // Read 1 renders the form with the seeded €1,250.50; from the gate's read onward the
        // source account holds €10, so the €50 the user is reviewing can no longer be sent.
        const balance = reads === 1 ? 1250.5 : 10;
        const [main, ...rest] = mockState.accounts;
        return HttpResponse.json({ data: [{ ...main, balance }, ...rest], message: null });
      }),
    );
    renderInternal();
    await screen.findByRole('button', { name: 'From Main Account' });

    await userEvent.click(screen.getByRole('button', { name: 'To Rainy Day' }));
    await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
    await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));

    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));

    expect(
      await screen.findByText('Insufficient balance for this operation — €10.00 available.'),
    ).toBeInTheDocument();
    // The ceremony is never rendered — which is the whole requirement.
    expect(screen.queryByLabelText('Digit 1 of 6')).not.toBeInTheDocument();
  });
});

describe('internal transfer (PR-11b)', () => {
  it('moves money between own accounts, authorised by an in-form PIN, and shows the receipt', async () => {
    renderInternal();
    await screen.findByRole('button', { name: 'From Main Account' }); // accounts loaded

    // Main is the auto-selected source; pick Rainy Day as the destination.
    await userEvent.click(screen.getByRole('button', { name: 'To Rainy Day' }));
    await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
    await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));

    await confirmWithPin();
    // The PIN is entered in the page now (`confirmWithPin`), not by the root step-up modal
    // reacting to a 403 — see ADR-0041.
    expect(screen.queryByText("Verify it's you")).not.toBeInTheDocument();

    expect(await screen.findByText('Transfer Complete!')).toBeInTheDocument();
    expect(screen.getByText('€50.00')).toBeInTheDocument();
    expect(screen.getByText('€1,200.50')).toBeInTheDocument(); // Main: 1250.50 - 50
    // findBy: the step-up modal's EXIT is async — until its aria-hidden lifts off the
    // background, role queries can't see the receipt buttons (P1.9 sweep).
    await userEvent.click(await screen.findByRole('button', { name: 'Done' }));
    expect(await screen.findByText('DASHBOARD')).toBeInTheDocument();
  });

  it('the receipt offers History as well as Done', async () => {
    // Added with the drift fix: an internal transfer writes a transaction and invalidates the same
    // list an external one does, but only external's receipt offered to go and look at it.
    renderInternal();
    await screen.findByRole('button', { name: 'From Main Account' });
    await userEvent.click(screen.getByRole('button', { name: 'To Rainy Day' }));
    await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
    await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
    await confirmWithPin();
    expect(screen.queryByText("Verify it's you")).not.toBeInTheDocument();
    await screen.findByText('Transfer Complete!');

    await userEvent.click(await screen.findByRole('button', { name: 'View History' }));
    expect(await screen.findByText('HISTORY')).toBeInTheDocument();
  });

  it('cannot pick the source account as the destination', async () => {
    renderInternal();
    await screen.findByRole('button', { name: 'From Main Account' });
    // Main is the source → it is disabled in the To list.
    expect(screen.getByRole('button', { name: 'To Main Account' })).toBeDisabled();
    // With no destination chosen, Review stays disabled.
    await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
    expect(screen.getByRole('button', { name: 'Review Transfer' })).toBeDisabled();
  });

  it('explains that a second account is needed when the user has only one', async () => {
    server.use(
      http.get('*/api/accounts', () =>
        HttpResponse.json({
          data: [
            {
              id: '019f7b3f-0000-7000-8000-0000000000a1',
              accountNumber: 'AB-****-****-90',
              name: 'Main Account',
              type: 'Checking',
              balance: 1000,
              isPrimary: true,
              createdAt: '2026-07-01T09:00:00.0000000Z',
            },
          ],
          message: null,
        }),
      ),
    );
    renderInternal();
    expect(await screen.findByText(/You need a second account to transfer/)).toBeInTheDocument();
  });

  it('says the accounts could not be loaded, and Retry actually refetches', async () => {
    /*
      Two defects, one test.

      The message was gated on `!accountsLoading`, and isLoading also goes false once a request has
      FAILED — so `accounts` fell back to [] and the page told a user with five accounts they needed
      a second one, sending them to create an account to solve a network problem.

      Fixing that gate alone left SILENCE: the shell stood there with empty pickers and no
      explanation. Both pages now follow the D22 convention AccountsPage already used — surface the
      problem and offer a way out of it.
    */
    let calls = 0;
    server.use(
      http.get('*/api/accounts', () => {
        calls += 1;
        return calls === 1
          ? problem({ status: 500, errorCode: 'INTERNAL_ERROR' })
          : HttpResponse.json({ data: [], message: null });
      }),
    );
    renderInternal();

    expect(await screen.findByText(/Could not load your accounts/)).toBeInTheDocument();
    // The wrong message must NOT be there: an empty list is not evidence of a missing account.
    expect(
      screen.queryByText(/You need a second account to transfer between your own accounts/),
    ).not.toBeInTheDocument();

    // And the form is gone: an amount field over an empty picker would stack "Available: €0.00"
    // and "Exceeds available balance of €0.00" underneath the real error.
    expect(screen.queryByLabelText('Transfer amount')).not.toBeInTheDocument();
    // Retry is not decoration — it must actually re-issue the request.
    await userEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(calls).toBe(2));
  });

  it('disables Review when the amount exceeds the source balance', async () => {
    renderInternal();
    await screen.findByRole('button', { name: 'From Main Account' });
    await userEvent.click(screen.getByRole('button', { name: 'To Rainy Day' }));
    await userEvent.type(screen.getByLabelText('Transfer amount'), '99999');

    expect(screen.getByText(/Exceeds available balance/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Review Transfer' })).toBeDisabled();
  });

  it('after IN_FLIGHT the retained key freezes Back; Send reuses the SAME key', async () => {
    const keys: (string | null)[] = [];
    let calls = 0;
    server.use(
      http.post('*/api/transfers/internal', ({ request }) => {
        keys.push(request.headers.get('Idempotency-Key'));
        calls += 1;
        return calls === 1
          ? problem({
              status: 409,
              errorCode: 'IDEMPOTENCY_IN_FLIGHT',
              detail: 'Still processing.',
            })
          : HttpResponse.json(
              {
                data: {
                  transferId: '019f7b3f-0000-7000-8000-000000000d01',
                  transactionNumber: 'TXN-INT-1',
                  fromAccountId: '019f7b3f-0000-7000-8000-0000000000a1',
                  toAccountId: '019f7b3f-0000-7000-8000-0000000000a2',
                  amount: 50,
                  description: null,
                  fromAccountNewBalance: 1200.5,
                  toAccountNewBalance: 880,
                  processedAt: '2026-07-22T13:00:00.0000000Z',
                },
                message: 'ok',
              },
              { status: 201 },
            );
      }),
    );
    renderInternal();
    await screen.findByRole('button', { name: 'From Main Account' });
    await userEvent.click(screen.getByRole('button', { name: 'To Rainy Day' }));
    await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
    await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
    // The spy answers IN_FLIGHT then 201 (no 403), so no modal — this isolates the PAGE's
    // retained-key guard: Back must freeze and Send must reuse the key.
    await confirmWithPin();

    expect(await screen.findByText(/Still processing/)).toBeInTheDocument();
    screen.getAllByRole('button', { name: 'Back' }).forEach((b) => expect(b).toBeDisabled());

    /*
      The retry is the banner's own `Check again`, not another walk through the flow: an
      IN_FLIGHT failure leaves the user on the PIN step with the PIN they already typed, because
      the page only clears it for PIN-specific refusals. That is the safe forward action — it
      reuses the retained key instead of minting a new one.
    */
    await userEvent.click(screen.getByRole('button', { name: 'Check again' }));
    expect(await screen.findByText('Transfer Complete!')).toBeInTheDocument();
    expect(keys).toHaveLength(2);
    expect(keys[0]).toBe(keys[1]); // SAME key across the retry
  });
});
