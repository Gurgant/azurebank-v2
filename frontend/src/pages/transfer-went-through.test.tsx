import { Route, Routes } from 'react-router-dom';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { server } from '../mocks/server';
import { renderWithProviders } from '../test/renderWithProviders';
import { enterPin } from '../test/pinFlow';
import { COPY, committedAnswerLost, emulateFocusFixup } from '../test/outage';
import { StepUpModal } from '../features/auth';
import { TransferPage } from './TransferPage';
import { InternalTransferPage } from './InternalTransferPage';

/**
 * A transfer the server says went through, when its receipt cannot be shown.
 *
 * The answer is the API's 409 `IDEMPOTENCY_RESULT_UNKNOWN` carrying `applied: true` (ADR-0009): the
 * key's record was read from the database as committed. Until that member existed the page drew
 * the check view for it, "We couldn't confirm your transfer", with a button that said it had not
 * gone through; pressed, the next send was a second payment under a new key.
 *
 * Two routes reach the answer, and both are driven here for both pages:
 *  - the send itself is answered with it (a re-run inside the API found its commit already there);
 *  - the send's answer is lost, and "Check again" re-sends the same key (the route a visitor
 *    meets: a commit whose answer never arrived, checked again once the claim is stale).
 *
 * What is asserted is what the visitor can do next: read that it went through, go to the history
 * or home, and nothing else. Sends are recorded by header: one key throughout.
 */

let stopFocusFixup: () => void;
beforeEach(() => {
  stopFocusFixup = emulateFocusFixup();
});
afterEach(() => {
  stopFocusFixup();
});

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

/** Form → review → the PIN step, stopping before the first digit. */
async function externalToPin() {
  await screen.findByText('Main Account');
  await userEvent.type(screen.getByLabelText('Recipient handle'), 'friend');
  await userEvent.click(screen.getByRole('button', { name: 'Verify' }));
  await screen.findByText('A. Friend');
  await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
  await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
  await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
  await screen.findByLabelText('Digit 1 of 6');
}

async function internalToPin() {
  await screen.findByRole('button', { name: 'From Main Account' });
  await userEvent.click(screen.getByRole('button', { name: 'To Rainy Day' }));
  await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
  await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
  await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
  await screen.findByLabelText('Digit 1 of 6');
}

/** Records every send's key and authorisation; the n-th send gets `answers[n]`. */
function recordSends(path: string, answers: (() => Response)[]) {
  const sent: { key: string | null; auth: string | null }[] = [];
  server.use(
    http.post(`*${path}`, ({ request }) => {
      sent.push({
        key: request.headers.get('Idempotency-Key'),
        auth: request.headers.get('Step-Up-Authorization'),
      });
      return answers[sent.length - 1]?.();
    }),
  );
  return sent;
}

/** The went-through view, whole: what it says, what it offers, and what it must not. */
async function expectWentThroughView() {
  // The answer has been drawn once the PIN step is gone, whatever the page made of it. The check
  // view is what the page drew for this answer before it read `applied`, so it is named first.
  await waitFor(() => expect(screen.queryByLabelText('Digit 1 of 6')).not.toBeInTheDocument());
  expect(screen.queryByText("We couldn't confirm your transfer")).not.toBeInTheDocument();
  const sentence = screen.getByText(COPY.transferWentThrough);

  // The receipt's own title, as the page's one heading.
  expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Transfer Complete');

  // The receipt's two ways on, and no control that could send again or claim it did not happen.
  expect(screen.getAllByRole('button').map((button) => button.textContent)).toEqual([
    'View History',
    'Done',
  ]);
  expect(screen.queryByRole('button', { name: 'Back' })).not.toBeInTheDocument();
  expect(screen.queryByRole('button', { name: 'Close' })).not.toBeInTheDocument();
  expect(screen.queryByText(/didn't go through/)).not.toBeInTheDocument();
  expect(screen.queryByText('Check recent transactions')).not.toBeInTheDocument();
  expect(screen.queryByText(/may or may not/)).not.toBeInTheDocument();

  // Nothing of the form is left on the page either.
  expect(screen.queryByLabelText('Transfer amount')).not.toBeInTheDocument();

  // Said once, by focus: no alert, no status bar, and focus on the sentence itself.
  expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  expect(screen.queryByRole('status')).not.toBeInTheDocument();
  await waitFor(() => expect(sentence).toHaveFocus());

  return sentence;
}

describe.each([
  {
    page: 'external',
    render: renderTransfer,
    toPin: externalToPin,
    sendPath: '/api/transfers',
  },
  {
    page: 'internal',
    render: renderInternal,
    toPin: internalToPin,
    sendPath: '/api/transfers/internal',
  },
])('$page transfer: the server says it went through', (flow) => {
  it('the send answered 409 applied: true shows "Transfer Complete" and the sentence, and View History goes to the history', async () => {
    const sent = recordSends(flow.sendPath, [() => committedAnswerLost(flow.sendPath)]);
    flow.render();
    await flow.toPin();
    await enterPin();

    await expectWentThroughView();
    expect(sent).toHaveLength(1);

    await userEvent.click(screen.getByRole('button', { name: 'View History' }));
    expect(await screen.findByText('HISTORY')).toBeInTheDocument();
    expect(sent).toHaveLength(1);
  });

  it('a lost answer, then "Check again" answered 409 applied: true, shows the same view on the same key, and Done goes home', async () => {
    const sent = recordSends(flow.sendPath, [
      () => HttpResponse.error(),
      () => committedAnswerLost(flow.sendPath),
    ]);
    flow.render();
    await flow.toPin();
    await enterPin();

    // The answer was lost: the page offers to check, with the same key.
    await userEvent.click(await screen.findByRole('button', { name: 'Check again' }));

    await expectWentThroughView();
    expect(sent).toHaveLength(2);
    expect(sent[1].key).toBe(sent[0].key);
    expect(sent[1].auth).toBe(sent[0].auth);

    await userEvent.click(screen.getByRole('button', { name: 'Done' }));
    expect(await screen.findByText('DASHBOARD')).toBeInTheDocument();
    expect(sent).toHaveLength(2);
  });
});
