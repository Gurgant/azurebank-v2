import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { expect, it } from 'vitest';
import { server } from '../../mocks/server';
import { problem } from '../../mocks/problem';
import { mockState } from '../../mocks/state';
import { renderWithProviders } from '../../test/renderWithProviders';
import { settle } from '../../test/settle';
import { lookupsSeen, whenListedByKey } from '../../test/outage';
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
        return code === 'HTTP_429'
          ? HttpResponse.json({ title: 'Too Many Requests', status: 429 }, { status: 429 })
          : problem({
              status: 429,
              errorCode: code,
              detail:
                code === 'DEMO_COPY_LIMIT'
                  ? 'This demo copy has reached its limit of changes. Start over to get a fresh copy.'
                  : 'Too many requests. Please retry later.',
            });
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
