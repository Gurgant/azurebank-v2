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
import { InternalTransferPage } from './InternalTransferPage';

/**
 * U6.3a — the behaviour net the unification will be measured against.
 *
 * `TransferPage` and `InternalTransferPage` are about to become one component: an audit measured
 * **83% of InternalTransferPage already present verbatim, in order, inside TransferPage**, with 29
 * of 31 shared style keys byte-identical. U5 could lean on its pinned dialog tests running
 * UNMODIFIED as the proof that a refactor changed nothing. That oracle is not available here — the
 * two existing suites impose mutually exclusive DOM contracts on the same widget, so they must
 * change during the merge, and a suite that has to change cannot also be the thing that certifies
 * the change.
 *
 * So the net is built first, and deliberately from BEHAVIOUR: what the user can do, what leaves the
 * browser, what the server is told. Nothing here asserts markup, because the markup is precisely
 * what is expected to move.
 *
 * These live in their own file rather than appended to the two page suites, for the same reason:
 * those suites are the before-picture, and the fewer reasons they have to change, the more a
 * reviewer can trust that whatever DID change was structural.
 *
 * The gaps below were not chosen by taste. An audit enumerated what the two suites leave unpinned;
 * these are the ones where a silent regression would cost money rather than pixels — starting with
 * the RESULT_UNKNOWN screen, which is the single thing standing between a user and a double-spend
 * and which neither page tested at all.
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

async function verifyRecipient(tag: string) {
  await userEvent.type(screen.getByLabelText('Recipient handle'), tag);
  await userEvent.click(screen.getByRole('button', { name: 'Verify' }));
}

/** A committed external transfer, shaped like the mock's own 201. */
function transferOk(headers: Record<string, string> = {}) {
  return HttpResponse.json(
    {
      data: {
        transactionNumber: 'TXN-BEHAVIOUR',
        amount: 50,
        newBalance: 1200.5,
        recipientAzureTag: 'friend',
        recipientName: 'A. Friend',
        processedAt: '2026-07-22T12:00:00.0000000Z',
      },
      message: 'ok',
    },
    { status: 201, headers },
  );
}

/** Drive the external page to the review step with a confirmed recipient and €50. */
async function externalToReview() {
  renderTransfer();
  await screen.findByText('Main Account');
  await verifyRecipient('friend');
  await screen.findByText('A. Friend');
  await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
  await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
}

describe('money flows — the behaviour the unification must preserve', () => {
  describe('RESULT_UNKNOWN: the screen that stands between a user and a double-spend', () => {
    /*
      `IDEMPOTENCY_RESULT_UNKNOWN` is the server saying it cannot tell whether the money moved.
      `useIdempotentMutation` drops the key and latches `verifyRequired`, and `submit` then THROWS
      rather than send until the flow's explicit "it didn't go through" action calls `resetIntent`
      (ADR-0022). A fresh key on a request that may already have committed is the double-spend this
      whole protocol exists to prevent.

      Neither page had a single test for it, on either side of the wire. Both are covered here,
      because after the merge there is one implementation and it must satisfy both.
    */
    const unknown = () =>
      problem({
        status: 409,
        errorCode: 'IDEMPOTENCY_RESULT_UNKNOWN',
        detail: 'The result could not be confirmed.',
      });

    it('external: offers no way out except the two that force the user to check', async () => {
      server.use(http.post('*/api/transfers', unknown));
      await externalToReview();
      await userEvent.click(screen.getByRole('button', { name: 'Send €50.00' }));

      expect(await screen.findByText("We couldn't confirm your transfer")).toBeInTheDocument();
      expect(screen.getByText(/retrying blindly could send twice/)).toBeInTheDocument();

      // Send is GONE, not merely disabled — the one control that could spend again.
      expect(screen.queryByRole('button', { name: /^Send/ })).not.toBeInTheDocument();
      // And no generic escape: this view deliberately renders no Back and no Close, so nobody
      // wanders off without confronting the ambiguity.
      expect(screen.queryByRole('button', { name: 'Back' })).not.toBeInTheDocument();
      expect(screen.queryByRole('button', { name: 'Close' })).not.toBeInTheDocument();

      expect(screen.getByRole('button', { name: 'Check recent transactions' })).toBeEnabled();
      expect(screen.getByRole('button', { name: /didn't go through/ })).toBeEnabled();
    });

    it('internal: the same screen, the same absence of exits', async () => {
      server.use(http.post('*/api/transfers/internal', unknown));
      renderInternal();
      await screen.findByRole('button', { name: 'From Main Account' });
      await userEvent.click(screen.getByRole('button', { name: 'To Rainy Day' }));
      await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
      await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
      await userEvent.click(screen.getByRole('button', { name: 'Send €50.00' }));

      expect(await screen.findByText("We couldn't confirm your transfer")).toBeInTheDocument();
      expect(screen.queryByRole('button', { name: /^Send/ })).not.toBeInTheDocument();
      expect(screen.queryByRole('button', { name: 'Back' })).not.toBeInTheDocument();
      expect(screen.getByRole('button', { name: /didn't go through/ })).toBeEnabled();
    });

    it('"start over" re-arms the flow and mints a NEW key — the opposite of a retry', async () => {
      /*
        The distinction this pins is the whole point of ADR-0022, and it is invisible in the UI:

          - a KEPT key (IN_FLIGHT / network / 5xx) means "same intent, ask again" -> SAME key;
          - RESULT_UNKNOWN means "that intent is abandoned" -> a NEW key.

        Reuse the old key here and the server would replay a possibly-committed transfer; mint a new
        one on the IN_FLIGHT path and you would spend twice. The existing suite pins the first half;
        this pins the second, so the merge cannot quietly collapse them into one rule.

        MEASURED, and worth knowing before anyone tidies the hook: the key is nulled TWICE on this
        path — once in the RESULT_UNKNOWN branch, once again in `resetIntent`. Mutation-testing each
        line separately leaves this test green, because either one alone upholds the invariant. Only
        removing BOTH makes the second send reuse the first key, and then this fails. So it guards
        the INVARIANT rather than a line, and neither nulling is dead code to be deleted on the
        grounds that its own removal breaks nothing.
      */
      const keys: (string | null)[] = [];
      let calls = 0;
      server.use(
        http.post('*/api/transfers', ({ request }) => {
          keys.push(request.headers.get('Idempotency-Key'));
          calls += 1;
          return calls === 1 ? unknown() : transferOk();
        }),
      );
      await externalToReview();
      await userEvent.click(screen.getByRole('button', { name: 'Send €50.00' }));
      await screen.findByText("We couldn't confirm your transfer");

      await userEvent.click(screen.getByRole('button', { name: /didn't go through/ }));

      // Back on the form with the confirmed recipient intact — only the INTENT was reset.
      expect(await screen.findByText('A. Friend')).toBeInTheDocument();
      await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
      await userEvent.click(screen.getByRole('button', { name: 'Send €50.00' }));

      expect(await screen.findByText('Transfer Sent!')).toBeInTheDocument();
      expect(keys).toHaveLength(2);
      expect(keys[0]).not.toBe(keys[1]);
    });
  });

  describe('the step machine, whose return paths nothing pinned', () => {
    it('external: Back from review restores the form with amount and recipient intact', async () => {
      // No test in either suite has ever successfully gone Back, so half of a two-state machine was
      // unverified — and the merge has to reproduce it from two separate implementations.
      await externalToReview();
      expect(screen.getByRole('button', { name: 'Send €50.00' })).toBeInTheDocument();

      // The review step's own Back is the last one in the tree; the first belongs to the header
      // and leaves the page entirely.
      const backs = screen.getAllByRole('button', { name: 'Back' });
      await userEvent.click(backs[backs.length - 1]);

      expect(await screen.findByLabelText('Transfer amount')).toHaveValue('50');
      expect(screen.getByText('A. Friend')).toBeInTheDocument();
      expect(screen.queryByRole('button', { name: 'Send €50.00' })).not.toBeInTheDocument();
    });

    it('internal: Back from review restores the form with both accounts still chosen', async () => {
      renderInternal();
      await screen.findByRole('button', { name: 'From Main Account' });
      await userEvent.click(screen.getByRole('button', { name: 'To Rainy Day' }));
      await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
      await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));

      const backs = screen.getAllByRole('button', { name: 'Back' });
      await userEvent.click(backs[backs.length - 1]);

      expect(await screen.findByLabelText('Transfer amount')).toHaveValue('50');
      expect(screen.getByRole('button', { name: 'To Rainy Day' })).toHaveAttribute(
        'aria-pressed',
        'true',
      );
    });
  });

  describe('anti-double-spend, at the two points a user can actually cause one', () => {
    it('a second click while the first Send is in flight does not reach the server', async () => {
      /*
        `disabled={isSubmitting}` had never been exercised. Holding the response open is what makes
        the second click land while the first is still in flight — without the hold, userEvent's
        await lets the request settle first and the guard is never under test at all.

        Stated precisely, because the risk here is over-claiming: two overlapping submits would
        reuse the SAME key (`keyRef.current ??=`), so the server would deduplicate them anyway. This
        pins the client's half of that defence, which is the half the merge could delete.
      */
      let release!: () => void;
      const held = new Promise<void>((resolve) => {
        release = resolve;
      });
      let calls = 0;
      server.use(
        http.post('*/api/transfers', async () => {
          calls += 1;
          await held;
          return transferOk();
        }),
      );
      await externalToReview();

      const send = screen.getByRole('button', { name: 'Send €50.00' });
      try {
        await userEvent.click(send);
        await waitFor(() => expect(send).toBeDisabled());
        await userEvent.click(send);
        expect(calls).toBe(1);
      } finally {
        // In a `finally`: the handler is parked on this promise, so an assertion throwing before
        // the release would turn a clear failure into a hang in whatever test runs next.
        release();
      }

      expect(await screen.findByText('Transfer Sent!')).toBeInTheDocument();
      expect(calls).toBe(1);
    });

    it('editing the handle after Verify clears the confirmed recipient', async () => {
      // The one field that decides WHO gets the money. The confirmed recipient comes from the
      // server, not from the form, so if an edit did not clear it the review screen could name one
      // person while the request carried another — money misdirection, not a validation slip.
      renderTransfer();
      await screen.findByText('Main Account');
      await verifyRecipient('friend');
      await screen.findByText('A. Friend');
      await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
      expect(screen.getByRole('button', { name: 'Review Transfer' })).toBeEnabled();

      await userEvent.type(screen.getByLabelText('Recipient handle'), 'x');

      await waitFor(() => expect(screen.queryByText('A. Friend')).not.toBeInTheDocument());
      expect(screen.getByRole('button', { name: 'Review Transfer' })).toBeDisabled();
    });
  });

  describe('the receipt', () => {
    it('says so when the transfer was replayed rather than newly committed', async () => {
      // `replayed` is threaded from the base query through to SuccessData for exactly one banner,
      // and nothing tested it. It is the only signal a user gets that a retry did NOT move money a
      // second time — which is the reassurance the whole idempotency protocol is for.
      server.use(
        http.post('*/api/transfers', () => transferOk({ 'Idempotency-Replayed': 'true' })),
      );
      await externalToReview();
      await userEvent.click(screen.getByRole('button', { name: 'Send €50.00' }));

      expect(await screen.findByText('Transfer Sent!')).toBeInTheDocument();
      expect(screen.getByText(/already processed/)).toBeInTheDocument();
    });

    it('does not cry replay on an ordinary transfer', async () => {
      // Without this the test above passes just as well against a banner that is ALWAYS shown.
      server.use(http.post('*/api/transfers', () => transferOk()));
      await externalToReview();
      await userEvent.click(screen.getByRole('button', { name: 'Send €50.00' }));

      expect(await screen.findByText('Transfer Sent!')).toBeInTheDocument();
      expect(screen.queryByText(/already processed/)).not.toBeInTheDocument();
    });
  });
});
