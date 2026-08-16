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
      await confirmWithPin();

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
      await confirmWithPin();

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
      await confirmWithPin();
      await screen.findByText("We couldn't confirm your transfer");

      await userEvent.click(screen.getByRole('button', { name: /didn't go through/ }));

      // Back on the form with the confirmed recipient intact — only the INTENT was reset.
      expect(await screen.findByText('A. Friend')).toBeInTheDocument();
      await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
      await confirmWithPin();

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
      // The review's primary control is Continue since ADR-0041 — Send now lives on the PIN step.
      expect(screen.getByRole('button', { name: 'Continue' })).toBeInTheDocument();

      // The review step's own Back is the last one in the tree; the first belongs to the header
      // and leaves the page entirely.
      const backs = screen.getAllByRole('button', { name: 'Back' });
      await userEvent.click(backs[backs.length - 1]);

      expect(await screen.findByLabelText('Transfer amount')).toHaveValue('50');
      expect(screen.getByText('A. Friend')).toBeInTheDocument();
      expect(screen.queryByRole('button', { name: 'Continue' })).not.toBeInTheDocument();
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
      await userEvent.click(screen.getByRole('button', { name: 'Continue' }));

      /*
        RE-AIMED for ADR-0042, and at the same guard rather than a weaker one.

        There is no Send button to double-click any more — the sixth digit submits — so the second
        submit a user can physically attempt is a second COMPLETION of the PIN boxes. The guard that
        must stop it moved with the trigger: `disabled={isSubmitting || pinLockDeadline !== null}`
        on `PinInput`. A disabled input takes no input, so `onComplete` cannot fire twice.

        Same caveat as before, restated so nobody reads more into this than it proves: two
        overlapping submits would reuse the SAME key (`keyRef.current ??=`), so the server would
        deduplicate them regardless. This pins the CLIENT's half — the half a refactor could delete
        without any server test noticing.
      */
      const firstBox = screen.getByLabelText('Digit 1 of 6');
      try {
        await userEvent.click(firstBox);
        await userEvent.paste('123456');
        await waitFor(() => expect(firstBox).toBeDisabled());

        // A user hammering the boxes while the request is open: the disabled input swallows it.
        await userEvent.click(firstBox);
        await userEvent.paste('123456');
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

  describe('what a failure message is still about, one step later', () => {
    /*
      ONE RULE: every step transition clears the transient failure state, EXCEPT a failure the server
      attributed to a value the user can still edit — that survives, and is cleared by the edit that
      supersedes it or by the next send.

      Stated honestly, because the ADR says so too: NO source decides this. Four research lanes and
      ~40 primary sources — WCAG, GOV.UK, Fluent, Carbon, USWDS, Cloudscape, NN/g — say nothing
      about an error message across a wizard step change. AWS Cloudscape ships a Wizard, documents a
      review step with back-navigation AND models server errors, and is still silent. So this is a
      reasoned decision, and these tests are what make it a decision rather than an accident.

      Note also how NARROW the reachable set is: shouldKeepKey retains the key on IN_FLIGHT /
      NETWORK / PARSE / 5xx, which makes keyLive true, which makes toForm() a no-op. So only a
      key-DROPPING 4xx can ever follow the user back — which is exactly the two cases below.
    */
    const failWith = (errorCode: string, status: number) =>
      server.use(http.post('*/api/transfers', () => problem({ status, errorCode })));

    it('an attempt-scoped message does NOT follow you back to the form', async () => {
      /*
        Driven with IDEMPOTENCY_KEY_REUSE rather than the motivating case, STEP_UP_REQUIRED,
        because a 403 never reaches this branch in a test: problemBaseQuery recognises step-up from
        the X-Auth-Level-Required HEADER before any body parsing (decision D2), so it opens the PIN
        modal instead of setting a banner. Both codes are attempt-scoped and take the identical
        path through goToStep; this one is reachable without standing up the step-up machinery.

        The motivating case is worth naming anyway: "Please tap Send and try again" landing on the
        form step, which has no Send button at all — since ADR-0041 Send lives on the PIN step, one
        further from the form than it used to. Not merely stale: unactionable.
      */
      failWith('IDEMPOTENCY_KEY_REUSE', 422);
      await externalToReview();
      await confirmWithPin();
      expect(await screen.findByText(/Something went wrong/)).toBeInTheDocument();

      // TWO steps back now: a failed send leaves you on the PIN step, and the form is behind
      // review. Each click re-queries, because the button that carries you is a different node
      // after the first transition.
      const backToReview = screen.getAllByRole('button', { name: 'Back' });
      await userEvent.click(backToReview[backToReview.length - 1]);
      const backToForm = await screen.findAllByRole('button', { name: 'Back' });
      await userEvent.click(backToForm[backToForm.length - 1]);

      expect(await screen.findByLabelText('Transfer amount')).toBeInTheDocument();
      await waitFor(() =>
        expect(screen.queryByText(/Something went wrong/)).not.toBeInTheDocument(),
      );
    });

    it('an input-scoped message DOES survive, because the value it names is on that screen', async () => {
      // Insufficient funds names the amount, and the amount is what you go back to change. Clearing
      // it would bounce the user with no reason. WithdrawDialog already keeps this one deliberately;
      // the scope makes that a typed consequence rather than a commented special case.
      failWith('INSUFFICIENT_FUNDS', 422);
      await externalToReview();
      await confirmWithPin();
      expect(await screen.findByText(/Insufficient funds/)).toBeInTheDocument();

      // TWO steps back now: a failed send leaves you on the PIN step, and the form is behind
      // review. Each click re-queries, because the button that carries you is a different node
      // after the first transition.
      const backToReview = screen.getAllByRole('button', { name: 'Back' });
      await userEvent.click(backToReview[backToReview.length - 1]);
      const backToForm = await screen.findAllByRole('button', { name: 'Back' });
      await userEvent.click(backToForm[backToForm.length - 1]);

      expect(await screen.findByLabelText('Transfer amount')).toBeInTheDocument();
      expect(screen.getByText(/Insufficient funds/)).toBeInTheDocument();
    });

    it('and editing the amount clears it, so it never outlives the value it describes', async () => {
      failWith('INSUFFICIENT_FUNDS', 422);
      await externalToReview();
      await confirmWithPin();
      await screen.findByText(/Insufficient funds/);
      // PIN step -> review -> form: the amount chip this test edits lives on the form.
      const backToReview = screen.getAllByRole('button', { name: 'Back' });
      await userEvent.click(backToReview[backToReview.length - 1]);
      const backToForm = await screen.findAllByRole('button', { name: 'Back' });
      await userEvent.click(backToForm[backToForm.length - 1]);

      await userEvent.click(screen.getByRole('button', { name: '€25' }));

      await waitFor(() => expect(screen.queryByText(/Insufficient funds/)).not.toBeInTheDocument());
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
      await confirmWithPin();

      expect(await screen.findByText('Transfer Sent!')).toBeInTheDocument();
      expect(screen.getByText(/already processed/)).toBeInTheDocument();
    });

    it('does not cry replay on an ordinary transfer', async () => {
      // Without this the test above passes just as well against a banner that is ALWAYS shown.
      server.use(http.post('*/api/transfers', () => transferOk()));
      await externalToReview();
      await confirmWithPin();

      expect(await screen.findByText('Transfer Sent!')).toBeInTheDocument();
      expect(screen.queryByText(/already processed/)).not.toBeInTheDocument();
    });
  });
});
