import { createRef } from 'react';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { expect, it, vi } from 'vitest';
import type { ApiProblem } from '../../api/problemBaseQuery';
import { renderWithProviders } from '../../test/renderWithProviders';
import { ReadErrorBar } from './ReadErrorBar';

/**
 * The bar every page with a read of its own shows when that read fails, inside the page's alert.
 * What the pages do with it — the slot, the key, where focus lands — is held by their own tests
 * (`slow-reads.test.tsx`); this holds the bar's words and its Retry.
 */

const FALLBACK = 'Could not load your accounts.';
const TRACE_ID = '4bf92f3577b34da6a3ce929d0e0e4736';

const failure = (fields: Partial<ApiProblem> = {}): ApiProblem => ({
  status: 500,
  errorCode: 'INTERNAL_ERROR',
  ...fields,
});

const renderBar = (problem: ApiProblem, onRetry = vi.fn()) => {
  const retryRef = createRef<HTMLButtonElement>();
  renderWithProviders(
    <ReadErrorBar problem={problem} fallback={FALLBACK} onRetry={onRetry} retryRef={retryRef} />,
  );
  return { onRetry, retryRef };
};

it("says the server's detail, and not the page's sentence, when the failure carries one", () => {
  renderBar(failure({ detail: 'Something broke.' }));

  expect(screen.getByText('Something broke.')).toBeInTheDocument();
  expect(screen.queryByText(FALLBACK)).not.toBeInTheDocument();
});

it("says the page's own sentence when the server sent no detail", () => {
  renderBar(failure());
  expect(screen.getByText(FALLBACK)).toBeInTheDocument();
});

it("says the page's own sentence for an empty detail too", () => {
  renderBar(failure({ detail: '' }));
  expect(screen.getByText(FALLBACK)).toBeInTheDocument();
});

it('adds the support code after the words when the failure has a trace id', () => {
  renderBar(failure({ detail: 'Something broke.', traceId: TRACE_ID }));

  expect(screen.getByText(`Something broke. Support code: ${TRACE_ID}`)).toBeInTheDocument();
});

it('adds the support code after the fallback too', () => {
  renderBar(failure({ traceId: TRACE_ID }));

  expect(screen.getByText(`${FALLBACK} Support code: ${TRACE_ID}`)).toBeInTheDocument();
});

it('says no support code when the failure has no trace id', () => {
  renderBar(failure({ detail: 'Something broke.' }));

  expect(screen.queryByText(/Support code/)).not.toBeInTheDocument();
});

it("has no role of its own: the alert it is read in is the page's slot", () => {
  renderBar(failure({ detail: 'Something broke.' }));

  expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  expect(screen.queryByRole('status')).not.toBeInTheDocument();
  expect(screen.getByText('Something broke.').closest('[role="group"]')).not.toBeNull();
});

it('calls onRetry from its Retry, which holds the landing ref', async () => {
  const user = userEvent.setup();
  const { onRetry, retryRef } = renderBar(failure({ detail: 'Something broke.' }));
  const retry = screen.getByRole('button', { name: 'Retry' });

  expect(retryRef.current).toBe(retry);
  await user.click(retry);
  expect(onRetry).toHaveBeenCalledTimes(1);
});
