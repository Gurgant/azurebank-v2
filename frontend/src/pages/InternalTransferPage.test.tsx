import { Route, Routes } from 'react-router-dom';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '../mocks/server';
import { problem } from '../mocks/problem';
import { renderWithProviders } from '../test/renderWithProviders';
import { StepUpModal } from '../features/auth';
import { InternalTransferPage } from './InternalTransferPage';

/**
 * PR-11b — internal transfer between the caller's OWN accounts, riding the same step-up
 * interceptor: pick source + destination (can't be the same), amount, review, Send → the
 * level-2 403 pops the root StepUpModal → PIN → the transfer replays onto the receipt.
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
    </Routes>,
    { routerEntries: ['/'] },
  );
}

async function enterPin(pin: string) {
  await userEvent.click(screen.getByLabelText('Digit 1 of 6'));
  await userEvent.paste(pin);
}

describe('internal transfer (PR-11b)', () => {
  it('moves money between own accounts through the step-up modal and shows the receipt', async () => {
    renderInternal();
    await screen.findByRole('button', { name: 'From Main Account' }); // accounts loaded

    // Main is the auto-selected source; pick Rainy Day as the destination.
    await userEvent.click(screen.getByRole('button', { name: 'To Rainy Day' }));
    await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
    await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));

    await userEvent.click(screen.getByRole('button', { name: 'Send €50.00' }));
    expect(await screen.findByText("Verify it's you")).toBeInTheDocument();
    await enterPin('123456');

    expect(await screen.findByText('Transfer Complete!')).toBeInTheDocument();
    expect(screen.getByText('€50.00')).toBeInTheDocument();
    expect(screen.getByText('€1,200.50')).toBeInTheDocument(); // Main: 1250.50 - 50
    // findBy: the step-up modal's EXIT is async — until its aria-hidden lifts off the
    // background, role queries can't see the receipt buttons (P1.9 sweep).
    await userEvent.click(await screen.findByRole('button', { name: 'Done' }));
    expect(await screen.findByText('DASHBOARD')).toBeInTheDocument();
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
    await userEvent.click(screen.getByRole('button', { name: 'Send €50.00' }));

    expect(await screen.findByText(/Still processing/)).toBeInTheDocument();
    screen.getAllByRole('button', { name: 'Back' }).forEach((b) => expect(b).toBeDisabled());

    await userEvent.click(screen.getByRole('button', { name: 'Send €50.00' }));
    expect(await screen.findByText('Transfer Complete!')).toBeInTheDocument();
    expect(keys).toHaveLength(2);
    expect(keys[0]).toBe(keys[1]); // SAME key across the retry
  });
});
