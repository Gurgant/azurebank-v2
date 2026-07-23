import { Route, Routes } from 'react-router-dom';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '../mocks/server';
import { problem } from '../mocks/problem';
import { renderWithProviders } from '../test/renderWithProviders';
import { StepUpModal } from '../features/auth';
import { TransferPage } from './TransferPage';

/**
 * PR-11 — the external transfer end to end, INCLUDING the step-up interceptor: pick an
 * account, confirm a recipient by exact AzureTag, enter an amount, review, Send → the level-2
 * 403 pops the root StepUpModal → PIN → the transfer replays and lands on the receipt.
 */

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

async function enterPin(pin: string) {
  await userEvent.click(screen.getByLabelText('Digit 1 of 6'));
  await userEvent.paste(pin);
}

async function verifyRecipient(tag: string) {
  await userEvent.type(screen.getByLabelText('Recipient handle'), tag);
  await userEvent.click(screen.getByRole('button', { name: 'Verify' }));
}

describe('external transfer (PR-11)', () => {
  it('confirms a recipient, transfers, steps up with a PIN, and shows the receipt', async () => {
    renderTransfer();
    await screen.findByText('Main Account'); // accounts loaded; first is auto-selected

    await verifyRecipient('friend');
    expect(await screen.findByText('A. Friend')).toBeInTheDocument();
    expect(screen.getByText('@friend')).toBeInTheDocument();

    await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
    await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));

    // Review → Send triggers the level-2 403 → the step-up modal.
    await userEvent.click(screen.getByRole('button', { name: 'Send €50.00' }));
    expect(await screen.findByText("Verify it's you")).toBeInTheDocument();

    await enterPin('123456');

    expect(await screen.findByText('Transfer Sent!')).toBeInTheDocument();
    expect(screen.getByText('-€50.00')).toBeInTheDocument();
    expect(screen.getByText('€1,200.50')).toBeInTheDocument(); // 1250.50 - 50
    await userEvent.click(screen.getByRole('button', { name: 'Done' }));
    expect(await screen.findByText('DASHBOARD')).toBeInTheDocument();
  });

  it('surfaces "not found" for an unknown handle and blocks review', async () => {
    renderTransfer();
    await screen.findByText('Main Account');

    await verifyRecipient('nobody');
    expect(await screen.findByText(/We couldn't find @nobody/)).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
    // No confirmed recipient → Review stays disabled.
    expect(screen.getByRole('button', { name: 'Review Transfer' })).toBeDisabled();
  });

  // 15s budget: the PR-12 fix gave the Cancel lookup a 5s findByRole timeout (the Fluent
  // alert-dialog's open transition keeps it aria-hidden briefly under load), but vitest's
  // DEFAULT test budget is also 5s — so on a loaded runner the inner wait could never
  // finish before the test itself timed out. The budget must exceed the inner waits.
  it(
    'cancelling the PIN modal returns to review without sending',
    { timeout: 15_000 },
    async () => {
      renderTransfer();
      await screen.findByText('Main Account');
      await verifyRecipient('friend');
      await screen.findByText('A. Friend');
      await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
      await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
      await userEvent.click(screen.getByRole('button', { name: 'Send €50.00' }));

      await screen.findByText("Verify it's you");
      // The dialog is aria-hidden during its Fluent open transition, and on a starved CI
      // runner that transition can stall for 5s+ (two consecutive CI runs proved the strict
      // role query NEVER resolves there, while the title — found by TEXT, which ignores
      // aria-hidden — shows the button is really in the DOM). So query hidden-inclusive and
      // click via fireEvent (no interactivity checks mid-transition). What this test pins is
      // cancel-returns-to-review, not the dialog's aria lifecycle — the step-up suite and
      // the live browser cover that.
      fireEvent.click(
        await screen.findByRole('button', { name: 'Cancel', hidden: true }, { timeout: 5000 }),
      );

      // Still on review, no receipt — the user can Send again to retry step-up.
      // (Same starved-runner headroom for the exit transition.)
      await waitFor(() => expect(screen.queryByText("Verify it's you")).not.toBeInTheDocument(), {
        timeout: 5000,
      });
      expect(screen.getByRole('button', { name: 'Send €50.00' })).toBeInTheDocument();
      expect(screen.queryByText('Transfer Sent!')).not.toBeInTheDocument();
    },
  );

  it('after IN_FLIGHT the retained key freezes Back; Send reuses the SAME key (no double-spend)', async () => {
    // The transfer returns IN_FLIGHT (a keep-key outcome) — the review Back must be disabled
    // (keyLive), so the body can't be edited to null the retained key, and Send reuses it.
    const keys: (string | null)[] = [];
    let calls = 0;
    server.use(
      http.post('*/api/transfers', ({ request }) => {
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
                  transactionNumber: 'TXN-X',
                  amount: 50,
                  newBalance: 1200.5,
                  recipientAzureTag: 'friend',
                  recipientName: 'A. Friend',
                  processedAt: '2026-07-22T12:00:00.0000000Z',
                },
                message: 'ok',
              },
              { status: 201 },
            );
      }),
    );
    renderTransfer();
    await screen.findByText('Main Account');
    await verifyRecipient('friend');
    await screen.findByText('A. Friend');
    await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
    await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
    await userEvent.click(screen.getByRole('button', { name: 'Send €50.00' }));

    expect(await screen.findByText(/Still processing/)).toBeInTheDocument();
    // Both the header and review Back are frozen while the key is retained (can't reach the
    // form to edit the body and null the key).
    screen.getAllByRole('button', { name: 'Back' }).forEach((b) => expect(b).toBeDisabled());

    await userEvent.click(screen.getByRole('button', { name: 'Send €50.00' }));
    expect(await screen.findByText('Transfer Sent!')).toBeInTheDocument();
    expect(keys).toHaveLength(2);
    expect(keys[0]).toBe(keys[1]); // SAME key on the retry
  });

  it('disables Review when the amount exceeds the balance', async () => {
    renderTransfer();
    await screen.findByText('Main Account');
    await verifyRecipient('friend');
    await screen.findByText('A. Friend');
    await userEvent.type(screen.getByLabelText('Transfer amount'), '99999');

    expect(screen.getByText(/Exceeds available balance/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Review Transfer' })).toBeDisabled();
  });
});
