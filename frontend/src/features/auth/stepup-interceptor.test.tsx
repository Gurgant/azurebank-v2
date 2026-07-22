import { useState } from 'react';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '../../mocks/server';
import { problem } from '../../mocks/problem';
import { mockState } from '../../mocks/state';
import { renderWithProviders } from '../../test/renderWithProviders';
import type { ApiProblem } from '../../api/problemBaseQuery';
import { useTransferMutation } from '../api/apiSlice';
import { useIdempotentMutation } from '../../hooks/useIdempotentMutation';
import { StepUpModal } from './StepUpModal';

/**
 * PR-11 — the step-up interceptor end to end (DECISIONS §2.2). A level-2-gated transfer 403s,
 * the root StepUpModal appears, verify-pin elevates the session, and the ORIGINAL request is
 * replayed ONCE with the identical Idempotency-Key — the money-critical no-double-spend
 * property. Also pins cancel (STEP_UP_CANCELLED, no replay) and wrong-PIN-stays.
 */

/** A minimal transfer trigger + the root modal, so the interceptor runs for real. */
function Harness() {
  const [trigger] = useTransferMutation();
  const { submit } = useIdempotentMutation(trigger);
  const [out, setOut] = useState('idle');
  return (
    <>
      <button
        onClick={async () => {
          setOut('pending');
          try {
            const result = await submit({
              fromAccountId: 'a',
              recipientAzureTag: 'friend',
              amount: 25,
            });
            setOut(`ok:${result.transactionNumber}`);
          } catch (caught) {
            setOut(`err:${(caught as ApiProblem).errorCode}`);
          }
        }}
      >
        Send
      </button>
      <div data-testid="out">{out}</div>
      <StepUpModal />
    </>
  );
}

/** Records every Idempotency-Key; 403 at level 1, 201 at level 2 (keyed off mockState). */
function spyTransfer(keys: (string | null)[]) {
  return http.post('*/api/transfers', ({ request }) => {
    keys.push(request.headers.get('Idempotency-Key'));
    if (mockState.authLevel < 2) {
      return HttpResponse.json(
        { type: 'STEP_UP_REQUIRED', requiredLevel: 2, currentLevel: 1, status: 403 },
        { status: 403, headers: { 'X-Auth-Level-Required': '2', 'X-Auth-Level-Current': '1' } },
      );
    }
    return HttpResponse.json(
      {
        data: {
          transactionNumber: 'TXN-STEPUP-1',
          amount: 25,
          newBalance: 975,
          recipientAzureTag: 'friend',
          recipientName: 'A. Friend',
          processedAt: '2026-07-22T12:00:00.0000000Z',
        },
        message: 'ok',
      },
      { status: 201 },
    );
  });
}

async function enterPin(pin: string) {
  await userEvent.click(screen.getByLabelText('Digit 1 of 6'));
  await userEvent.paste(pin);
}

describe('step-up interceptor (PR-11)', () => {
  it('403 → modal → verify → replays the request with the SAME idempotency key', async () => {
    const keys: (string | null)[] = [];
    server.use(spyTransfer(keys));
    renderWithProviders(<Harness />);

    await userEvent.click(screen.getByRole('button', { name: 'Send' }));
    expect(await screen.findByText("Verify it's you")).toBeInTheDocument();

    await enterPin('123456'); // onComplete auto-verifies → elevates → replays
    expect(await screen.findByText('ok:TXN-STEPUP-1')).toBeInTheDocument();

    // Two attempts (initial 403 + the post-elevation replay), IDENTICAL key = no double-spend.
    expect(keys).toHaveLength(2);
    expect(keys[0]).not.toBeNull();
    expect(keys[0]).toBe(keys[1]);
  });

  it('cancelling the modal yields STEP_UP_CANCELLED and never replays', async () => {
    const keys: (string | null)[] = [];
    server.use(spyTransfer(keys));
    renderWithProviders(<Harness />);

    await userEvent.click(screen.getByRole('button', { name: 'Send' }));
    await screen.findByText("Verify it's you");
    // findByRole (not getByRole): wait for the Cancel button, not just the modal title — the
    // title can paint a tick earlier, which raced under CI load.
    await userEvent.click(await screen.findByRole('button', { name: 'Cancel' }));

    expect(await screen.findByText('err:STEP_UP_CANCELLED')).toBeInTheDocument();
    expect(keys).toHaveLength(1); // only the initial 403 — no replay after cancel
  });

  it('a wrong PIN stays in the modal (verified:false); a correct one then elevates', async () => {
    const keys: (string | null)[] = [];
    server.use(spyTransfer(keys));
    renderWithProviders(<Harness />);

    await userEvent.click(screen.getByRole('button', { name: 'Send' }));
    await screen.findByText("Verify it's you");

    await enterPin('000000');
    expect(await screen.findByText('Incorrect PIN. Please try again.')).toBeInTheDocument();
    expect(screen.getByText("Verify it's you")).toBeInTheDocument(); // still open

    await enterPin('123456');
    expect(await screen.findByText('ok:TXN-STEPUP-1')).toBeInTheDocument();
  });

  it('replays only ONCE — a still-403 replay surfaces STEP_UP_REQUIRED, never an infinite loop', async () => {
    let calls = 0;
    server.use(
      http.post('*/api/transfers', () => {
        calls += 1;
        return HttpResponse.json(
          { type: 'STEP_UP_REQUIRED', requiredLevel: 2, currentLevel: 1, status: 403 },
          { status: 403, headers: { 'X-Auth-Level-Required': '2', 'X-Auth-Level-Current': '1' } },
        );
      }),
    );
    renderWithProviders(<Harness />);

    await userEvent.click(screen.getByRole('button', { name: 'Send' }));
    await screen.findByText("Verify it's you");
    await enterPin('123456'); // verify-pin elevates, but the transfer keeps 403ing

    expect(await screen.findByText('err:STEP_UP_REQUIRED')).toBeInTheDocument();
    expect(calls).toBe(2); // original + exactly one replay, not a loop
  });

  it('locks the modal after 3 wrong PINs (429 PIN_LOCKED)', async () => {
    server.use(spyTransfer([]));
    renderWithProviders(<Harness />);

    await userEvent.click(screen.getByRole('button', { name: 'Send' }));
    await screen.findByText("Verify it's you");

    await enterPin('000000');
    expect(await screen.findByText('Incorrect PIN. Please try again.')).toBeInTheDocument();
    await enterPin('000000');
    await enterPin('000000'); // 3rd miss trips the shared lockout

    expect(await screen.findByText(/Too many incorrect PIN attempts/)).toBeInTheDocument();
  });

  it('an unexpected verify-pin error (500) is SURFACED, not masked as a cancellation', async () => {
    server.use(spyTransfer([]));
    server.use(
      http.post('*/bff/auth/verify-pin', () =>
        problem({ status: 500, errorCode: 'INTERNAL_ERROR', detail: 'boom' }),
      ),
    );
    renderWithProviders(<Harness />);

    await userEvent.click(screen.getByRole('button', { name: 'Send' }));
    await screen.findByText("Verify it's you");
    await enterPin('123456');

    expect(await screen.findByText(/Couldn't verify right now/)).toBeInTheDocument();
    // Modal stays open (NOT settled 'cancelled') so the failure isn't silent.
    expect(screen.getByText("Verify it's you")).toBeInTheDocument();
    expect(screen.queryByText(/^err:/)).not.toBeInTheDocument();
  });
});
