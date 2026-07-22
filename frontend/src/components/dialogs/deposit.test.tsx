import { Route, Routes } from 'react-router-dom';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '../../mocks/server';
import { problem } from '../../mocks/problem';
import { mockState } from '../../mocks/state';
import { renderWithProviders } from '../../test/renderWithProviders';
import { DepositDialog } from './DepositDialog';

/**
 * T3 (PR-9) — the first production idempotent mutation. Pins the useIdempotentMutation
 * contract end-to-end: replay note (D4), KEEP-on-IN_FLIGHT with the SAME key on retry,
 * key ROTATION on body edit (the D21 seam), RESULT_UNKNOWN verify-first flow, and the
 * generic (never-raw) message for a client-protocol KEY_REUSE bug (§2.3 / D17).
 */

const MAIN = mockState.accounts[0]; // seed: id a1, balance 1250.5

function seedAccount() {
  return { id: MAIN.id, name: 'Main Account', accountNumber: 'AB-****-****-90', balance: 1250.5 };
}

function renderDeposit() {
  return renderWithProviders(
    <Routes>
      <Route
        path="/"
        element={<DepositDialog isOpen onClose={() => {}} accounts={[seedAccount()]} />}
      />
      <Route path="/transactions/:id" element={<div>TX DETAIL PAGE</div>} />
      <Route path="/history" element={<div>HISTORY PAGE</div>} />
    </Routes>,
    { routerEntries: ['/'] },
  );
}

function depositSuccessBody(newBalance: number, replayed = false) {
  return HttpResponse.json(
    {
      data: {
        transaction: {
          id: '019f7b3f-0000-7000-8000-0000000000d99',
          transactionNumber: 'TXN-20260722-000999',
          type: 'Deposit',
          amount: 100,
          balanceAfter: newBalance,
          description: null,
          recipientAzureTag: null,
          senderAzureTag: null,
          status: 'Completed',
          createdAt: '2026-07-22T10:00:00.0000000Z',
        },
        newBalance,
      },
      message: 'Deposit completed successfully.',
    },
    { status: 201, headers: replayed ? { 'Idempotency-Replayed': 'true' } : {} },
  );
}

describe('deposit (T3 — idempotent mutation)', () => {
  it('deposits and shows the receipt with the real new balance, then navigates to the transaction', async () => {
    renderDeposit();

    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    expect(screen.getByText('New balance: €1,350.50')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Deposit €100.00' }));

    expect(await screen.findByText('Deposit Successful!')).toBeInTheDocument();
    expect(screen.getByText('+€100.00')).toBeInTheDocument();
    expect(screen.getByText('€1,350.50')).toBeInTheDocument(); // new balance in receipt

    await userEvent.click(screen.getByRole('button', { name: 'View Transaction' }));
    expect(await screen.findByText('TX DETAIL PAGE')).toBeInTheDocument();
  });

  it('surfaces the polite replay note on a replayed 2xx (D4)', async () => {
    server.use(http.post('*/api/transactions/deposit', () => depositSuccessBody(1350.5, true)));
    renderDeposit();

    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    await userEvent.click(screen.getByRole('button', { name: 'Deposit €100.00' }));

    expect(await screen.findByText('Deposit Successful!')).toBeInTheDocument();
    expect(screen.getByText(/already processed — showing the existing result/)).toBeInTheDocument();
  });

  it('KEEPS the key on IN_FLIGHT and retries with the SAME key (no client double-spend)', async () => {
    const keys: (string | null)[] = [];
    let calls = 0;
    server.use(
      http.post('*/api/transactions/deposit', ({ request }) => {
        keys.push(request.headers.get('Idempotency-Key'));
        calls += 1;
        return calls === 1
          ? problem({
              status: 409,
              errorCode: 'IDEMPOTENCY_IN_FLIGHT',
              detail: 'Still processing.',
            })
          : depositSuccessBody(1350.5);
      }),
    );
    renderDeposit();

    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    await userEvent.click(screen.getByRole('button', { name: 'Deposit €100.00' }));
    expect(await screen.findByText(/Still processing/)).toBeInTheDocument();

    // Retry WITHOUT editing the body — same key must be reused.
    await userEvent.click(screen.getByRole('button', { name: 'Deposit €100.00' }));
    expect(await screen.findByText('Deposit Successful!')).toBeInTheDocument();
    expect(keys).toHaveLength(2);
    expect(keys[0]).toBe(keys[1]);
  });

  it('ROTATES the key when the amount is edited between attempts (D21 seam)', async () => {
    const keys: (string | null)[] = [];
    server.use(
      http.post('*/api/transactions/deposit', ({ request }) => {
        keys.push(request.headers.get('Idempotency-Key'));
        // First attempt KEEPs the key; a body edit must still rotate it.
        return keys.length === 1
          ? problem({
              status: 409,
              errorCode: 'IDEMPOTENCY_IN_FLIGHT',
              detail: 'Still processing.',
            })
          : depositSuccessBody(1450.5);
      }),
    );
    renderDeposit();

    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    await userEvent.click(screen.getByRole('button', { name: 'Deposit €100.00' }));
    await screen.findByText(/Still processing/);

    // Edit the amount — the old key + new body would be a 422 KEY_REUSE, so it must rotate.
    await userEvent.click(screen.getByRole('button', { name: '€200' }));
    await userEvent.click(screen.getByRole('button', { name: 'Deposit €200.00' }));
    expect(await screen.findByText('Deposit Successful!')).toBeInTheDocument();
    expect(keys).toHaveLength(2);
    expect(keys[0]).not.toBe(keys[1]);
  });

  it('RESULT_UNKNOWN latches a verify-first flow, not a blind retry (§2.3)', async () => {
    server.use(
      http.post('*/api/transactions/deposit', () =>
        problem({
          status: 409,
          errorCode: 'IDEMPOTENCY_RESULT_UNKNOWN',
          detail: 'Could not confirm.',
        }),
      ),
    );
    renderDeposit();

    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    await userEvent.click(screen.getByRole('button', { name: 'Deposit €100.00' }));

    expect(await screen.findByText("We couldn't confirm your deposit")).toBeInTheDocument();
    // The blind Deposit button is gone; only verify actions remain.
    expect(screen.queryByRole('button', { name: /^Deposit/ })).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Check recent transactions' }));
    expect(await screen.findByText('HISTORY PAGE')).toBeInTheDocument();
  });

  it('shows a generic message for a KEY_REUSE client-protocol bug, never the raw code (D17)', async () => {
    server.use(
      http.post('*/api/transactions/deposit', () =>
        problem({
          status: 422,
          errorCode: 'IDEMPOTENCY_KEY_REUSE',
          detail: 'This idempotency key was already used with a different payload.',
        }),
      ),
    );
    renderDeposit();

    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    await userEvent.click(screen.getByRole('button', { name: 'Deposit €100.00' }));

    expect(await screen.findByText('Something went wrong. Please try again.')).toBeInTheDocument();
    expect(screen.queryByText(/IDEMPOTENCY_KEY_REUSE/)).not.toBeInTheDocument();
    expect(screen.queryByText(/idempotency key/i)).not.toBeInTheDocument();
  });

  it('disables the CTA until a valid amount is entered', async () => {
    renderDeposit();
    expect(screen.getByRole('button', { name: /^Deposit/ })).toBeDisabled();
    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    expect(screen.getByRole('button', { name: 'Deposit €100.00' })).toBeEnabled();
  });

  it('cannot be dismissed mid-flight — the close controls are disabled so the key survives', async () => {
    // Never-resolving deposit keeps the submit in flight. Unmounting the dialog now
    // would destroy the in-memory key → a reopened deposit would double-spend.
    server.use(http.post('*/api/transactions/deposit', () => new Promise<Response>(() => {})));
    renderDeposit();

    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    await userEvent.click(screen.getByRole('button', { name: 'Deposit €100.00' }));

    await waitFor(() => expect(screen.getByRole('button', { name: 'Close' })).toBeDisabled());
    expect(screen.getByText('Deposit Money')).toBeInTheDocument(); // still open
  });

  it('freezes body edits mid-flight so the pending intent cannot be rotated/nulled', async () => {
    // A body edit during submit would run onBodyEdit → resetIntent(), dropping the retained
    // key out from under the in-flight request; a later NETWORK/5xx could then close or
    // resubmit into a NEW intent. Every body control is disabled while submitting.
    server.use(http.post('*/api/transactions/deposit', () => new Promise<Response>(() => {})));
    renderDeposit();

    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    await userEvent.click(screen.getByRole('button', { name: 'Deposit €100.00' }));

    await waitFor(() => expect(screen.getByLabelText('Deposit amount')).toBeDisabled());
    expect(screen.getByRole('button', { name: '€200' })).toBeDisabled();
    expect(screen.getByLabelText('Description')).toBeDisabled();
  });

  it('shows an inline hint and disables the CTA for an over-limit amount', async () => {
    renderDeposit();
    await userEvent.type(screen.getByLabelText('Deposit amount'), '1000001');

    expect(screen.getByText('Maximum deposit is €1,000,000.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /^Deposit/ })).toBeDisabled();
  });

  it('collapses a multi-dot amount to a single decimal', async () => {
    renderDeposit();
    await userEvent.type(screen.getByLabelText('Deposit amount'), '1.2.3');
    expect(screen.getByLabelText('Deposit amount')).toHaveValue('1.23');
  });

  it('shows a friendly message on a transport failure, never the raw error (D17)', async () => {
    server.use(http.post('*/api/transactions/deposit', () => HttpResponse.error()));
    renderDeposit();

    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    await userEvent.click(screen.getByRole('button', { name: 'Deposit €100.00' }));

    expect(await screen.findByText(/Couldn't reach the server/)).toBeInTheDocument();
    expect(screen.queryByText(/Failed to fetch/i)).not.toBeInTheDocument();
  });
});
