import { flushSync } from 'react-dom';
import { Route, Routes } from 'react-router-dom';
import { act, fireEvent, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it } from 'vitest';
import { server } from '../mocks/server';
import { seedMockSession } from '../mocks/state';
import { apiSlice } from '../features/api/apiSlice';
import { enterPin } from '../test/pinFlow';
import { makeTestStore, renderWithProviders, type TestStore } from '../test/renderWithProviders';
import { InternalTransferPage } from './InternalTransferPage';
import { TransferPage } from './TransferPage';

/**
 * The check `onValid` makes of the amount against the balance, on the two transfer pages.
 *
 * The form's own bound is a piece of state that follows the cached balance one render behind: an
 * effect copies the balance into it. So there is one render in which the page already holds the
 * new balance and the form still validates against the old one. A sixth digit that lands in that
 * render passes the form, and `onValid` is what refuses it. Everywhere else the form refuses
 * first (`balance-guard.test.tsx` and the pages' own suites hold that).
 *
 * To land there the test moves the cached balance and enters the sixth digit in one synchronous
 * stretch: `flushSync` commits the render that holds the new balance, and the event is dispatched
 * before the bound's own update, which React has only scheduled, can run.
 */

beforeEach(() => {
  seedMockSession();
});

function render(page: 'external' | 'internal', store: TestStore) {
  return renderWithProviders(
    <Routes>
      <Route path="/" element={page === 'external' ? <TransferPage /> : <InternalTransferPage />} />
    </Routes>,
    { store },
  );
}

/** Every POST under `/api/transfers` that leaves the page: the mint and the send. */
function countTransferPosts() {
  const posts: string[] = [];
  server.events.on('request:start', ({ request }) => {
    if (request.method === 'POST' && request.url.includes('/api/transfers')) {
      posts.push(new URL(request.url).pathname);
    }
  });
  return posts;
}

async function toThePinStep(page: 'external' | 'internal') {
  if (page === 'external') {
    await screen.findByText('Main Account');
    await userEvent.type(screen.getByLabelText('Recipient handle'), 'friend');
    await userEvent.click(screen.getByRole('button', { name: 'Verify' }));
    await screen.findByText('A. Friend');
  } else {
    await screen.findByRole('button', { name: 'From Main Account' });
    await userEvent.click(screen.getByRole('button', { name: 'To Rainy Day' }));
  }
  await userEvent.type(screen.getByLabelText('Transfer amount'), '400');
  await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
  await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
  // Five digits: on the PIN step, and nothing sent yet.
  await enterPin('12345');
}

describe('the amount is checked again when the PIN is complete', () => {
  it.each(['external', 'internal'] as const)(
    '%s: a balance that fell in the render before the sixth digit sends nothing',
    async (page) => {
      const store = makeTestStore();
      const posts = countTransferPosts();
      render(page, store);
      await toThePinStep(page);

      act(() => {
        flushSync(() => {
          store.dispatch(
            apiSlice.util.updateQueryData('getAccounts', undefined, (accounts) => {
              accounts[0].balance = 100;
            }),
          );
          // The cache's own action is batched and tells nobody until the next frame. An
          // action of any other kind tells every subscriber at once.
          store.dispatch({ type: 'test/tellTheSubscribers' });
        });
        fireEvent.change(screen.getByLabelText('Digit 6 of 6'), { target: { value: '6' } });
      });

      expect(
        await screen.findByText('Insufficient balance for this operation — €100.00 available.'),
      ).toBeInTheDocument();
      expect(await screen.findByLabelText('Transfer amount')).toBeInTheDocument();
      expect(posts).toEqual([]);
    },
  );
});
