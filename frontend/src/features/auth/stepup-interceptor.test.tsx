import { useState } from 'react';
import { act, cleanup, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '../../mocks/server';
import { problem } from '../../mocks/problem';
import { mockState, seedMockSession } from '../../mocks/state';
import { renderWithProviders } from '../../test/renderWithProviders';
import type { ApiProblem } from '../../api/problemBaseQuery';
import { useTransferMutation } from '../api/apiSlice';
import { useIdempotentMutation } from '../../hooks/useIdempotentMutation';
import { StepUpModal } from './StepUpModal';

/**
 * The step-up interceptor end to end (ADR-0022): a 403 pops the root StepUpModal, verify-pin
 * elevates the session, and the ORIGINAL request is replayed ONCE with the identical
 * Idempotency-Key — the money-critical no-double-spend property. Also pins cancel
 * (STEP_UP_CANCELLED, no replay) and wrong-PIN-stays.
 *
 * ⚠️ READ THIS BEFORE TRUSTING THE ENDPOINT IN THESE TESTS.
 *
 * `/api/transfers` here is a VEHICLE for the interceptor, not a claim about transfers. Since
 * ADR-0041 the BFF does NOT answer 403 for a transfer — the PIN travels in the body and the API
 * verifies it — and every 403 below is fabricated by this file's own `spyTransfer`, which
 * overrides the aligned mock handler. The interceptor itself is still live code, because
 * `/full-number` keeps the session model (decision D3a), so the mechanism is worth pinning; it is
 * the pairing with THIS endpoint that is now synthetic.
 *
 * The consequence is worth stating plainly rather than leaving for someone to discover: after
 * ADR-0041, no monetary endpoint sits behind the step-up gate, so the replay-with-the-same-key
 * path has no live caller. `/full-number` is a GET and carries no Idempotency-Key. Whether to
 * retire that half of the interceptor is a separate decision, deliberately not taken here.
 * `TransferDoesNotTriggerStepUp` at the bottom is the guard that keeps the new truth true.
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
              pin: '123456',
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

/**
 * Records every Idempotency-Key AND the raw body text; 403 at level 1, 201 at level 2
 * (keyed off mockState). The body is captured as TEXT, not parsed — "byte-identical" is a
 * claim about the serialized bytes, and comparing two parsed objects would pass even if the
 * property order changed, which is exactly what the server's HMAC fingerprint would reject.
 */
function spyTransfer(keys: (string | null)[], bodies: string[] = []) {
  return http.post('*/api/transfers', async ({ request }) => {
    keys.push(request.headers.get('Idempotency-Key'));
    bodies.push(await request.clone().text());
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

/*
  UNMOUNT FIRST, then restore the clock — the order is load-bearing and cost a debugging round.

  Vitest runs afterEach hooks in reverse registration order, so this file's hook runs BEFORE the
  `cleanup()` in `test/setup.ts`. Restoring real timers here first left the countdown still mounted
  with a live FAKE interval; its unmount then called `clearInterval` with a fake id against the real
  implementation, so the interval was never cancelled and kept firing setState into the NEXT test —
  which the console.error gate caught as act(...) violations, and only when the whole file ran.
  `cleanup()` is idempotent, so calling it here and again in setup.ts is free.
*/
afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

describe('step-up interceptor (PR-11)', () => {
  it('403 → modal → verify → replays the request with the SAME key AND the SAME body', async () => {
    const keys: (string | null)[] = [];
    const bodies: string[] = [];
    server.use(spyTransfer(keys, bodies));
    renderWithProviders(<Harness />);

    await userEvent.click(screen.getByRole('button', { name: 'Send' }));
    expect(await screen.findByText("Verify it's you")).toBeInTheDocument();

    await enterPin('123456'); // onComplete auto-verifies → elevates → replays
    expect(await screen.findByText('ok:TXN-STEPUP-1')).toBeInTheDocument();

    // Two attempts (initial 403 + the post-elevation replay), IDENTICAL key = no double-spend.
    expect(keys).toHaveLength(2);
    expect(keys[0]).not.toBeNull();
    expect(keys[0]).toBe(keys[1]);

    /*
      ADR-0022's Verification §4 claims "step-up replay carries a byte-identical body and the
      same key". Until this assertion existed, only the KEY half was pinned — and the key alone
      cannot tell "the same request, replayed" from "the same key with an edited body", which is
      the one case the server answers 422 IDEMPOTENCY_KEY_REUSE for. Byte-identity is a
      CONSEQUENCE of baseQueryWithStepUp replaying the identical `args` reference: fetchBaseQuery
      re-serializes a per-invocation copy (JSON.stringify on the same object, same key order),
      so the bytes match because nothing rebuilt them — not because anything holds a buffer.
    */
    expect(bodies).toHaveLength(2);

    /*
      Guard against a VACUOUS pass first. `bodies[0] === bodies[1]` is satisfied by two EMPTY
      strings, and that state is reachable: dropping `body` from the transfer query fn was measured
      to leave this whole file 6/6 green with `bodies === ['', '']`, because spyTransfer branches
      only on mockState.authLevel and never inspects the payload.

      `toEqual`, not `toMatchObject`: the query fn destructures `{ idempotencyKey, body }`, so the
      field most likely to leak into the payload by accident is the key itself — and a subset
      matcher would wave that through. `description` is absent rather than undefined because
      JSON.stringify drops undefined values.
    */
    expect(JSON.parse(bodies[0])).toEqual({
      fromAccountId: 'a',
      recipientAzureTag: 'friend',
      amount: 25,
      pin: '123456',
    });
    // Content checked once; `===` carries it to the replay transitively. Keep BOTH assertions —
    // JSON.parse discards key order and whitespace, which is the byte-level property claimed above.
    expect(bodies[0]).toBe(bodies[1]);
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

    /*
      The PIN group's `aria-describedby` must RESOLVE to the banner. The id sits on the MessageBar
      itself rather than on a wrapper around it (the app-wide pattern), so this assertion is also
      what proves Fluent forwards `id` to the rendered root: the day it stops, the reference dangles
      and a screen-reader user is told a PIN was rejected by an element that announces nothing.
      Asserting the text through getElementById is deliberate — matching only the attribute would
      pass against a dangling id.
    */
    const describedBy = screen
      .getByRole('group', { name: 'Enter your PIN' })
      .getAttribute('aria-describedby');
    expect(describedBy).toBeTruthy();
    expect(document.getElementById(describedBy!)).toHaveTextContent(
      'Incorrect PIN. Please try again.',
    );

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

  it('the lock EXPIRES — the PIN boxes come back once the window closes', async () => {
    /*
      Until this, nothing asserted that a step-up lock ever ends. The modal stored the number of
      seconds and never touched it: the countdown froze and the PIN boxes stayed disabled for as
      long as the modal was open. Cancel remained enabled, so the escape was to abandon whatever
      the modal was gating — which is why this read as cosmetic rather than as a dead end.

      Fake timers BEFORE the render: `RetryCountdown` creates its interval on mount, and an interval
      scheduled against the real clock cannot be advanced. `shouldAdvanceTime` keeps userEvent's own
      zero-delay waits resolving.
    */
    server.use(spyTransfer([]));
    server.use(
      http.post('*/bff/auth/verify-pin', () =>
        problem({
          status: 429,
          errorCode: 'PIN_LOCKED',
          detail: 'Too many attempts.',
          extensions: { retryAfterSeconds: 5 },
        }),
      ),
    );
    renderWithProviders(<Harness />);

    await userEvent.click(screen.getByRole('button', { name: 'Send' }));
    await screen.findByText("Verify it's you");

    // Installed here, not at the top: before this PIN no countdown exists, so nothing can tick.
    vi.useFakeTimers({ shouldAdvanceTime: true });
    await enterPin('000000');

    expect(await screen.findByText(/Too many incorrect PIN attempts/)).toBeInTheDocument();

    await act(async () => {
      vi.advanceTimersByTime(6000);
    });

    await waitFor(() =>
      expect(screen.queryByText(/Too many incorrect PIN attempts/)).not.toBeInTheDocument(),
    );
    // The boxes are the control the lock disabled, so they are what has to come back.
    expect(screen.getByLabelText('Digit 1 of 6')).toBeEnabled();
  });

  it('a transfer against the ALIGNED mock never triggers step-up (ADR-0041)', async () => {
    /*
      Deliberately NO `server.use` — this runs against the real handler set, which is aligned to
      the backend. Every other test in this file overrides it with a 403; this one must not, or it
      would pin the behaviour it exists to forbid.

      If someone puts `/api/transfers` back into `AuthLevelMiddleware.PinRequiredPaths` and aligns
      the mock to match, this fails: the modal appears and no receipt arrives. That is the whole
      point — the in-band check would then be double-gated, and the weaker of the two would be
      deciding again.
    */
    mockState.authLevel = 1;
    renderWithProviders(<Harness />);

    await userEvent.click(screen.getByRole('button', { name: 'Send' }));

    await screen.findByText(/^(ok|err):/);
    expect(screen.queryByText("Verify it's you")).not.toBeInTheDocument();
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
