import { Route, Routes } from 'react-router-dom';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '../mocks/server';
import { problem } from '../mocks/problem';
import { renderWithProviders } from '../test/renderWithProviders';
import { StepUpModal } from '../features/auth';
import { TransferPage } from './TransferPage';
import { seedMockSession } from '../mocks/state';

/**
 * Review -> PIN -> Send (ADR-0041). The flow gained a step: the review screen's primary button is
 * now Continue, and the PIN that authorises the transfer is entered here rather than supplied by
 * the root step-up modal off a 403.
 */
async function confirmWithPinAndSend(sendLabel: string) {
  await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
  await userEvent.click(screen.getByLabelText('Digit 1 of 6'));
  await userEvent.paste('123456');
  await userEvent.click(screen.getByRole('button', { name: sendLabel }));
}

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

describe('external transfer (PR-11)', () => {
  it('confirms a recipient, authorises with a PIN in-form, and shows the receipt', async () => {
    renderTransfer();
    await screen.findByText('Main Account'); // accounts loaded; first is auto-selected

    await verifyRecipient('friend');
    expect(await screen.findByText('A. Friend')).toBeInTheDocument();
    expect(screen.getByText('@friend')).toBeInTheDocument();

    await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
    await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));

    /*
      Review -> Continue -> PIN -> Send, all in the page. Until ADR-0041 this line waited for the
      root step-up modal ("Verify it's you") to appear off a 403 from the BFF; the PIN now travels
      in the request body and the API verifies it, so there is no modal and no 403 to react to.
    */
    await confirmWithPinAndSend('Send €50.00');
    expect(screen.queryByText("Verify it's you")).not.toBeInTheDocument();

    expect(await screen.findByText('Transfer Sent!')).toBeInTheDocument();
    expect(screen.getByText('-€50.00')).toBeInTheDocument();
    expect(screen.getByText('€1,200.50')).toBeInTheDocument(); // 1250.50 - 50
    await userEvent.click(await screen.findByRole('button', { name: 'Done' }));
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

  it('Back from the PIN step returns to review without sending', async () => {
    /*
      This used to be "cancelling the PIN modal returns to review without sending" — the modal was
      the root step-up dialog, driven by a 403. ADR-0041 removed both, so the property is now about
      the page's own step machine: leaving the PIN step must not send, and must land on review with
      the send control still reachable.
    */
    renderTransfer();
    await screen.findByText('Main Account');
    await verifyRecipient('friend');
    await screen.findByText('A. Friend');
    await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
    await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));

    await enterPin('123456');
    const backs = screen.getAllByRole('button', { name: 'Back' });
    await userEvent.click(backs[backs.length - 1]);

    expect(await screen.findByRole('button', { name: 'Continue' })).toBeInTheDocument();
    expect(screen.queryByText('Transfer Sent!')).not.toBeInTheDocument();
    // And the PIN did not survive the trip: coming back must re-ask rather than reuse it.
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
    expect(screen.getByRole('button', { name: 'Send €50.00' })).toBeDisabled();
  });

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
    await confirmWithPinAndSend('Send €50.00');

    expect(await screen.findByText(/Still processing/)).toBeInTheDocument();
    // Both the header and review Back are frozen while the key is retained (can't reach the
    // form to edit the body and null the key).
    screen.getAllByRole('button', { name: 'Back' }).forEach((b) => expect(b).toBeDisabled());

    /*
      The retry is a SECOND press of the same Send, not another walk through the flow: an
      IN_FLIGHT failure leaves the user on the PIN step with the PIN they already typed, because
      the page only clears it for PIN-specific refusals. That is the safe forward action — it
      reuses the retained key instead of minting a new one.
    */
    await userEvent.click(screen.getByRole('button', { name: 'Send €50.00' }));
    expect(await screen.findByText('Transfer Sent!')).toBeInTheDocument();
    expect(keys).toHaveLength(2);
    expect(keys[0]).toBe(keys[1]); // SAME key on the retry
  });

  it('the source-account cards announce their name and which one is chosen', async () => {
    // Added with the drift fix. The internal wizard's identical cards have carried both attributes
    // since it was written; without them a screen-reader user here heard an unnamed button and had
    // no way to tell which account was selected — the blue border says that to sighted users only.
    renderTransfer();
    await screen.findByText('Main Account');

    const main = screen.getByRole('button', { name: 'From Main Account' });
    const rainy = screen.getByRole('button', { name: 'From Rainy Day' });
    expect(main).toHaveAttribute('aria-pressed', 'true'); // primary is auto-selected
    expect(rainy).toHaveAttribute('aria-pressed', 'false');

    await userEvent.click(rainy);
    expect(rainy).toHaveAttribute('aria-pressed', 'true');
    expect(main).toHaveAttribute('aria-pressed', 'false');
  });

  it('says the accounts could not be loaded, and Retry actually refetches', async () => {
    // External had no error handling at all — a failed load simply rendered an empty account
    // picker. Added here as well as on the internal page, because fixing one of two identical
    // wizards is how the drift this PR removes got created in the first place.
    let calls = 0;
    server.use(
      http.get('*/api/accounts', () => {
        calls += 1;
        return calls === 1
          ? problem({ status: 500, errorCode: 'INTERNAL_ERROR' })
          : HttpResponse.json({ data: [], message: null });
      }),
    );
    renderTransfer();

    expect(await screen.findByText(/Could not load your accounts/)).toBeInTheDocument();

    // And the form is gone: an amount field over an empty picker would stack "Available: €0.00"
    // and "Exceeds available balance of €0.00" underneath the real error.
    expect(screen.queryByLabelText('Transfer amount')).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(calls).toBe(2));
  });

  it('a rate-limited lookup says so, instead of blaming the connection', async () => {
    /*
      The recipient lookup had a bare `catch` that reported every failure as connectivity. That
      endpoint has a DEDICATED limiter — /api/users/* runs a tight per-user sliding window
      (ADR-0014) and rejects with 429 + Retry-After — so the failure a user is most likely to
      provoke, by tapping Verify repeatedly, told them to check a connection that was fine.

      Asserted on STATUS, not on an errorCode: the BFF's rejection body is a bare ProblemDetails
      with no errorCode, so the client synthesises HTTP_429 and there is nothing to match on.
    */
    server.use(
      http.get('*/api/users/:azureTag', () =>
        HttpResponse.json(
          { type: 'https://httpstatuses.com/429', title: 'Too Many Requests', status: 429 },
          { status: 429, headers: { 'Retry-After': '90' } },
        ),
      ),
    );
    renderTransfer();
    await screen.findByText('Main Account');

    await verifyRecipient('friend');

    expect(await screen.findByText(/Too many lookups/)).toBeInTheDocument();
    expect(screen.getByText(/2 minutes/)).toBeInTheDocument(); // 90s, rounded up by formatLockHorizon
    expect(screen.queryByText(/check your connection/)).not.toBeInTheDocument();
  });

  it(
    'still blames the connection when the connection really is the problem',
    { timeout: 15_000 },
    async () => {
      // Without this the test above passes just as well against a catch that says "too many lookups"
      // for everything — the exact defect being fixed, with the wording swapped.
      //
      // The generous timeout is not padding: problemBaseQuery RETRIES a NETWORK failure up to three
      // times before it ever reaches the catch, so the message cannot appear on the first tick.
      server.use(http.get('*/api/users/:azureTag', () => HttpResponse.error()));
      renderTransfer();
      await screen.findByText('Main Account');

      await verifyRecipient('friend');

      expect(
        await screen.findByText(/check your connection/, {}, { timeout: 8000 }),
      ).toBeInTheDocument();
      expect(screen.queryByText(/Too many lookups/)).not.toBeInTheDocument();
    },
  );

  it('a failed Send is ANNOUNCED, not just displayed', async () => {
    /*
      Measured before fixing: the money banners rendered a bare <MessageBar intent="error"> with no
      role and no aria-live. Fluent's MessageBar root is role="group" — NOT a live region — and it
      delegates announcing to useAnnounce(), whose default context is a no-op; this app mounts no
      AnnounceProvider anywhere (grep: zero hits). So a screen-reader user was told nothing at all
      when their transfer failed.

      LoginPage already solved this and says why in a comment. The money flows never got the same
      treatment — which also made the keep-vs-clear question below literally unobservable to those
      users until now.
    */
    server.use(
      http.post('*/api/transfers', () =>
        problem({ status: 422, errorCode: 'INSUFFICIENT_FUNDS', detail: 'Not enough.' }),
      ),
    );
    renderTransfer();
    await screen.findByText('Main Account');
    await verifyRecipient('friend');
    await screen.findByText('A. Friend');
    await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
    await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
    await confirmWithPinAndSend('Send €50.00');

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Insufficient funds for this transfer.');
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

  it('revalidates the amount against the NEW balance when the source account changes', async () => {
    // CodeRabbit-caught regression guard: mode 'onChange' only revalidates the field that
    // changed, so without the explicit trigger-on-bound-change an account switch would
    // leave canReview reflecting the PREVIOUS account's balance (silent no-op on Send).
    renderTransfer();
    await screen.findByText('Main Account');
    await verifyRecipient('friend');
    await screen.findByText('A. Friend');
    // 900 fits Main Account (1250.50) but exceeds Rainy Day (830).
    await userEvent.type(screen.getByLabelText('Transfer amount'), '900');
    expect(screen.getByRole('button', { name: 'Review Transfer' })).toBeEnabled();

    await userEvent.click(screen.getByRole('button', { name: /Rainy Day/ }));
    expect(await screen.findByText('Exceeds available balance of €830.00.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Review Transfer' })).toBeDisabled();
  });
});
