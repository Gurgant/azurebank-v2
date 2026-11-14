import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { expect, it } from 'vitest';
import { server } from '../../mocks/server';
import { problem } from '../../mocks/problem';
import { mockState } from '../../mocks/state';
import { renderWithProviders } from '../../test/renderWithProviders';
import { settle } from '../../test/settle';
import { COPY, limiterRefusal, lookupsSeen, whenListedByKey } from '../../test/outage';
import { WithdrawDialog } from './WithdrawDialog';

it.each(['HTTP_429', 'RATE_LIMIT_EXCEEDED', 'DEMO_COPY_LIMIT'])(
  'checks the listing after NETWORK, Retry and a limiter refusal (%s), even when the read is refused',
  async (code) => {
    const account = mockState.accounts[0];
    const keys: (string | null)[] = [];
    server.use(
      http.post('*/api/transactions/withdraw', ({ request }) => {
        keys.push(request.headers.get('Idempotency-Key'));
        if (keys.length === 1) return HttpResponse.error();
        return limiterRefusal(code);
      }),
    );
    // The copy budget permits GETs; another limiter can still refuse the listing read.
    let refuseRead = true;
    whenListedByKey(() =>
      refuseRead ? problem({ status: 429, errorCode: 'RATE_LIMIT_EXCEEDED' }) : undefined,
    );
    const seen = lookupsSeen();
    renderWithProviders(
      <WithdrawDialog
        isOpen
        onClose={() => {}}
        accounts={[{ ...account, name: 'Main Account', accountNumber: 'AB-****-****-90' }]}
      />,
    );

    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    await userEvent.click(screen.getByRole('button', { name: /^Continue/ }));
    expect(await screen.findByText('Verify Withdrawal')).toBeInTheDocument();
    await userEvent.click(screen.getByLabelText('Digit 1 of 6'));
    await userEvent.paste('123456');
    await userEvent.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));
    expect(
      await screen.findByText("Couldn't reach the server — check your connection and try again."),
    ).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Close' })).toBeDisabled();

    // Retry re-presents the same authorisation and sends the same withdrawal key.
    await userEvent.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));
    expect(await screen.findByText("We couldn't confirm your withdrawal")).toBeInTheDocument();
    expect(
      await screen.findByText(
        "We couldn't check your transactions for it, and it may still go through. Keep this page open: we'll look again.",
      ),
    ).toBeInTheDocument();
    expect(keys).toHaveLength(2);
    expect(keys[0]).toBeTruthy();
    expect(keys[1]).toBe(keys[0]);
    expect(seen).toHaveLength(1);
    expect(seen[0]).toMatchObject({ key: keys[0], accountId: account.id });
    expect(screen.getByRole('button', { name: 'Close' })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Check recent transactions' })).toBeEnabled();
    expect(screen.queryByRole('button', { name: /^Withdraw/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /It didn't go through/ })).not.toBeInTheDocument();
    expect(screen.queryByText('Your withdrawal went through.')).not.toBeInTheDocument();
    expect(screen.queryByText('Withdrawal failed. Please try again.')).not.toBeInTheDocument();
    expect(screen.queryByText(/Too many requests/)).not.toBeInTheDocument();
    expect(screen.queryByText(/This demo copy has reached/)).not.toBeInTheDocument();

    refuseRead = false;
    await settle();
    await userEvent.click(screen.getByRole('button', { name: 'Check again' }));
    expect(
      await screen.findByText(
        "It isn't in your transactions yet, and it may still go through. Keep this page open: we'll look again.",
      ),
    ).toBeInTheDocument();
    await waitFor(() => expect(seen).toHaveLength(2));
    expect(seen[1]).toMatchObject({ key: keys[0], accountId: account.id });
    expect(keys).toHaveLength(2);
    expect(screen.queryByRole('button', { name: /It didn't go through/ })).not.toBeInTheDocument();
  },
);

/*
  The mint of a new authorisation under the key an expiry kept, refused by a limiter. The key
  kept the X disabled, and Withdraw could only ask the limiter again: a demo copy past its limit
  refuses every mint after that one. Every authorisation sent under the key was refused, so the
  check view needs no wait to say that nothing went through, and "try again" is the way on.
*/
it.each(['HTTP_429', 'RATE_LIMIT_EXCEEDED', 'DEMO_COPY_LIMIT'])(
  'shows the check view and opens the X when a limiter (%s) refuses the new authorisation after an expiry',
  async (code) => {
    const account = mockState.accounts[0];
    let mints = 0;
    const keys: (string | null)[] = [];
    server.use(
      http.post('*/api/transactions/withdraw/authorizations', () => {
        mints += 1;
        if (mints === 1) return undefined;
        return limiterRefusal(code);
      }),
      http.post('*/api/transactions/withdraw', ({ request }) => {
        keys.push(request.headers.get('Idempotency-Key'));
        return problem({
          status: 401,
          errorCode: 'AUTHORIZATION_EXPIRED',
          detail: 'This authorisation has expired. Enter your PIN again to confirm.',
        });
      }),
    );
    renderWithProviders(
      <WithdrawDialog
        isOpen
        onClose={() => {}}
        accounts={[{ ...account, name: 'Main Account', accountNumber: 'AB-****-****-90' }]}
      />,
    );

    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    await userEvent.click(screen.getByRole('button', { name: /^Continue/ }));
    expect(await screen.findByText('Verify Withdrawal')).toBeInTheDocument();
    await userEvent.click(screen.getByLabelText('Digit 1 of 6'));
    await userEvent.paste('123456');
    await userEvent.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));
    expect(await screen.findByText(/expired/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Close' })).toBeDisabled();

    // The PIN stays in its boxes on that answer, so Withdraw mints again under the kept key.
    await userEvent.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));
    expect(await screen.findByText(COPY.movementNone)).toBeInTheDocument();
    expect(screen.getByText("Your withdrawal didn't go through")).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Close' })).toBeEnabled();
    expect(screen.getByRole('button', { name: "It didn't go through — try again" })).toBeEnabled();
    expect(screen.queryByRole('button', { name: /^Withdraw/ })).not.toBeInTheDocument();
    expect(screen.queryByText(/Too many requests/)).not.toBeInTheDocument();
    expect(screen.queryByText(/This demo copy has reached/)).not.toBeInTheDocument();
    expect(screen.queryByText('Withdrawal failed. Please try again.')).not.toBeInTheDocument();
    expect(mints).toBe(2);
    expect(keys).toHaveLength(1);
  },
);

/*
  "Try again" after that latch: the form is back and the next withdrawal goes under a new key. And
  where the limit refuses the read the check view makes straight after, the view says it could not
  check, with "try again" still offered, since every authorisation under the key was refused.
*/
async function toAMintRefusedUnderTheKey(account: (typeof mockState.accounts)[number]) {
  renderWithProviders(
    <WithdrawDialog
      isOpen
      onClose={() => {}}
      accounts={[{ ...account, name: 'Main Account', accountNumber: 'AB-****-****-90' }]}
    />,
  );
  await userEvent.click(screen.getByRole('button', { name: '€100' }));
  await userEvent.click(screen.getByRole('button', { name: /^Continue/ }));
  expect(await screen.findByText('Verify Withdrawal')).toBeInTheDocument();
  await userEvent.click(screen.getByLabelText('Digit 1 of 6'));
  await userEvent.paste('123456');
  await userEvent.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));
  expect(await screen.findByText(/expired/)).toBeInTheDocument();
  await userEvent.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));
}

it('returns to the form on "try again" after a limiter refuses the new authorisation, and the next withdrawal has a new key', async () => {
  const account = mockState.accounts[0];
  let mints = 0;
  const keys: (string | null)[] = [];
  server.use(
    http.post('*/api/transactions/withdraw/authorizations', () => {
      mints += 1;
      return mints === 2 ? limiterRefusal('RATE_LIMIT_EXCEEDED') : undefined;
    }),
    http.post('*/api/transactions/withdraw', ({ request }) => {
      keys.push(request.headers.get('Idempotency-Key'));
      if (keys.length > 1) return undefined;
      return problem({
        status: 401,
        errorCode: 'AUTHORIZATION_EXPIRED',
        detail: 'This authorisation has expired. Enter your PIN again to confirm.',
      });
    }),
  );
  await toAMintRefusedUnderTheKey(account);
  expect(await screen.findByText(COPY.movementNone)).toBeInTheDocument();
  await settle();

  await userEvent.click(screen.getByRole('button', { name: "It didn't go through — try again" }));

  // The form again, and no check view.
  expect(await screen.findByRole('button', { name: '€100' })).toBeInTheDocument();
  expect(screen.queryByText(COPY.movementNone)).not.toBeInTheDocument();
  await settle();
  await userEvent.click(screen.getByRole('button', { name: '€100' }));
  await userEvent.click(screen.getByRole('button', { name: /^Continue/ }));
  expect(await screen.findByText('Verify Withdrawal')).toBeInTheDocument();
  await userEvent.click(screen.getByLabelText('Digit 1 of 6'));
  await userEvent.paste('123456');
  await userEvent.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));
  expect(await screen.findByText('Withdrawal Successful!')).toBeInTheDocument();
  expect(keys).toHaveLength(2);
  expect(keys[1]).not.toBe(keys[0]);
  expect(mints).toBe(3);
});

it('says it could not check, and still offers "try again", when the limit refuses the read too', async () => {
  const account = mockState.accounts[0];
  let mints = 0;
  server.use(
    http.post('*/api/transactions/withdraw/authorizations', () => {
      mints += 1;
      return mints === 2 ? limiterRefusal('RATE_LIMIT_EXCEEDED') : undefined;
    }),
    http.post('*/api/transactions/withdraw', () =>
      problem({
        status: 401,
        errorCode: 'AUTHORIZATION_EXPIRED',
        detail: 'This authorisation has expired. Enter your PIN again to confirm.',
      }),
    ),
  );
  whenListedByKey(() => problem({ status: 429, errorCode: 'RATE_LIMIT_EXCEEDED' }));
  await toAMintRefusedUnderTheKey(account);

  expect(await screen.findByText(COPY.movementUnchecked)).toBeInTheDocument();
  expect(screen.getByText("We couldn't confirm your withdrawal")).toBeInTheDocument();
  expect(screen.queryByText(COPY.movementNone)).not.toBeInTheDocument();
  expect(screen.getByRole('button', { name: 'Close' })).toBeEnabled();
  expect(screen.getByRole('button', { name: "It didn't go through — try again" })).toBeEnabled();
  expect(mints).toBe(2);
});

/*
  With no key held, the same refusal of the first mint is said on the PIN step, as before: nothing
  was sent, so there is nothing to check and the X stays open.
*/
it.each([
  { code: 'HTTP_429', said: 'Withdrawal failed. Please try again.' },
  { code: 'RATE_LIMIT_EXCEEDED', said: 'Too many requests. Please retry later.' },
  {
    code: 'DEMO_COPY_LIMIT',
    said: 'This demo copy has reached its limit of changes. Start over to get a fresh copy.',
  },
])(
  'says it on the PIN step and keeps the X open when a limiter ($code) refuses the first authorisation',
  async ({ code, said }) => {
    const account = mockState.accounts[0];
    let withdrawals = 0;
    server.use(
      http.post('*/api/transactions/withdraw/authorizations', () => limiterRefusal(code)),
      http.post('*/api/transactions/withdraw', () => {
        withdrawals += 1;
        return undefined;
      }),
    );
    renderWithProviders(
      <WithdrawDialog
        isOpen
        onClose={() => {}}
        accounts={[{ ...account, name: 'Main Account', accountNumber: 'AB-****-****-90' }]}
      />,
    );

    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    await userEvent.click(screen.getByRole('button', { name: /^Continue/ }));
    expect(await screen.findByText('Verify Withdrawal')).toBeInTheDocument();
    await userEvent.click(screen.getByLabelText('Digit 1 of 6'));
    await userEvent.paste('123456');
    await userEvent.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));

    expect(await screen.findByText(said)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Close' })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Back' })).toBeEnabled();
    expect(screen.getByRole('button', { name: /^Withdraw/ })).toBeInTheDocument();
    expect(screen.queryByText(COPY.movementNone)).not.toBeInTheDocument();
    expect(screen.queryByText("Your withdrawal didn't go through")).not.toBeInTheDocument();
    expect(withdrawals).toBe(0);
  },
);
