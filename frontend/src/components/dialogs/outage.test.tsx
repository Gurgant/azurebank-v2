import { Route, Routes } from 'react-router-dom';
import { act, cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { server } from '../../mocks/server';
import { problem, serviceUnavailable } from '../../mocks/problem';
import { mockState } from '../../mocks/state';
import { renderWithProviders } from '../../test/renderWithProviders';
import { expectNoNestedLiveRegions } from '../../test/liveRegions';
import {
  COPY,
  advanceUntil,
  fakeClockUser,
  hintRegion,
  hintShownAt,
  never,
  installFakeClock,
  reloadAsksFirst,
} from '../../test/outage';
import { CONNECTION_FAILED } from '../../api/problemMessages';
import { AccountsPage } from '../../pages/AccountsPage';
import { DashboardPage } from '../../pages/DashboardPage';
import { ChangePinDialog } from './ChangePinDialog';
import { CreateAccountDialog } from './CreateAccountDialog';
import { DepositDialog } from './DepositDialog';
import { RenameAccountDialog } from './RenameAccountDialog';
import { RenameAzureTagDialog } from './RenameAzureTagDialog';
import { WithdrawDialog } from './WithdrawDialog';

/**
 * The dialogs during an outage: whether the visitor's change or money went anywhere.
 *
 * A money dialog says "nothing was changed" only when the server said so (`applied: false`);
 * otherwise it says it cannot tell yet and how to find out — pressing the same button again, which
 * re-sends the same key. "Retrying won't charge you twice" rides along only where that press is
 * the retry (withdraw), never on deposit, where "charge" is the wrong word for money arriving.
 * A change with no key (a new account, a new name, a new PIN) cannot be asked again safely, so it
 * says the change may already be saved. And an answer that arrives but cannot be read is no
 * answer at all: the money may have moved, so the dialog asks for a check, never "failed".
 */

afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

const MAIN = mockState.accounts[0];

function moneyAccount() {
  return { id: MAIN.id, name: 'Main Account', accountNumber: 'AB-••••-••••-90', balance: 1250.5 };
}

function renderWithdraw() {
  return renderWithProviders(
    <Routes>
      <Route
        path="/"
        element={<WithdrawDialog isOpen onClose={() => {}} accounts={[moneyAccount()]} />}
      />
      <Route path="/history" element={<div>HISTORY PAGE</div>} />
      <Route path="/pin-setup" element={<div>PIN SETUP PAGE</div>} />
    </Routes>,
    { routerEntries: ['/'] },
  );
}

function renderDeposit(accounts = [moneyAccount()]) {
  return renderWithProviders(
    <Routes>
      <Route path="/" element={<DepositDialog isOpen onClose={() => {}} accounts={accounts} />} />
      <Route path="/history" element={<div>HISTORY PAGE</div>} />
    </Routes>,
    { routerEntries: ['/'] },
  );
}

async function withdrawToPin() {
  await userEvent.click(screen.getByRole('button', { name: '€100' }));
  await userEvent.click(screen.getByRole('button', { name: /^Continue/ }));
  await screen.findByText('Verify Withdrawal');
  await userEvent.click(screen.getByLabelText('Digit 1 of 6'));
  await userEvent.paste('123456');
}

/** Wait until the one alert on screen says `text`. */
async function alertSays(text: string) {
  await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent(text));
}

const inFlight = () =>
  problem({
    status: 409,
    errorCode: 'IDEMPOTENCY_IN_FLIGHT',
    detail: 'A request with this key is still being processed.',
  });

describe('withdraw during an outage', () => {
  it('a send refused with nothing applied says nothing changed, and that retrying is safe', async () => {
    server.use(
      http.post('*/api/transactions/withdraw', () =>
        serviceUnavailable({ via: 'api', applied: false, instance: '/api/transactions/withdraw' }),
      ),
    );
    renderWithdraw();
    await withdrawToPin();
    await userEvent.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));

    await alertSays(`${COPY.nothingChanged} ${COPY.noDoubleCharge}`);
  });

  it('a send answered 503 without `applied` says it cannot tell yet, and how to check safely', async () => {
    server.use(
      http.post('*/api/transactions/withdraw', () =>
        serviceUnavailable({ via: 'api', instance: '/api/transactions/withdraw' }),
      ),
    );
    renderWithdraw();
    await withdrawToPin();
    await userEvent.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));

    await alertSays(`${COPY.withdrawalUnknown} ${COPY.noDoubleCharge}`);
  });

  it('a send with no answer ends at 65 s with the same words', async () => {
    let sentAt = 0;
    server.use(
      http.post('*/api/transactions/withdraw', () => {
        sentAt = Date.now();
        return never();
      }),
    );
    renderWithdraw();
    await withdrawToPin();
    installFakeClock();
    const user = fakeClockUser();
    await user.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));
    await waitFor(() => expect(sentAt).toBeGreaterThan(0));

    await advanceUntil(sentAt, 65_100);

    await alertSays(`${COPY.withdrawalUnknown} ${COPY.noDoubleCharge}`);
  });

  it('a mint answered 503 moved no money', async () => {
    server.use(
      http.post('*/api/transactions/withdraw/authorizations', () =>
        serviceUnavailable({ via: 'api', instance: '/api/transactions/withdraw/authorizations' }),
      ),
    );
    renderWithdraw();
    await withdrawToPin();
    await userEvent.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));

    await alertSays(COPY.noMoneyMoved);
  });

  it('the in-flight note adds that pressing Withdraw again is safe', async () => {
    server.use(http.post('*/api/transactions/withdraw', inFlight));
    renderWithdraw();
    await withdrawToPin();
    await userEvent.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));

    await waitFor(() =>
      expect(screen.getByRole('status')).toHaveTextContent(
        `Still processing — tap Withdraw again to check. ${COPY.noDoubleCharge}`,
      ),
    );
  });
});

describe('deposit during an outage', () => {
  async function depositHundred() {
    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    await userEvent.click(screen.getByRole('button', { name: 'Deposit €100.00' }));
  }

  it('a send refused with nothing applied says nothing changed — and says nothing about charges', async () => {
    server.use(
      http.post('*/api/transactions/deposit', () =>
        serviceUnavailable({ via: 'api', applied: false, instance: '/api/transactions/deposit' }),
      ),
    );
    renderDeposit();
    await depositHundred();

    await alertSays(COPY.nothingChanged);
    expect(screen.queryByText(/charge/)).not.toBeInTheDocument();
  });

  it('a send answered 503 without `applied` says it cannot tell yet, and to tap Deposit again', async () => {
    server.use(
      http.post('*/api/transactions/deposit', () =>
        serviceUnavailable({ via: 'api', instance: '/api/transactions/deposit' }),
      ),
    );
    renderDeposit();
    await depositHundred();

    await alertSays(COPY.depositUnknown);
    expect(screen.queryByText(/charge/)).not.toBeInTheDocument();
  });

  it('a send with no answer ends at 65 s with the same words', async () => {
    let sentAt = 0;
    server.use(
      http.post('*/api/transactions/deposit', () => {
        sentAt = Date.now();
        return never();
      }),
    );
    renderDeposit();
    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    installFakeClock();
    const user = fakeClockUser();
    await user.click(screen.getByRole('button', { name: 'Deposit €100.00' }));
    await waitFor(() => expect(sentAt).toBeGreaterThan(0));

    await advanceUntil(sentAt, 65_100);

    await alertSays(COPY.depositUnknown);
  });

  it('after an unknown outcome, editing the amount asks for a check and never sends a second key', async () => {
    let sends = 0;
    server.use(
      http.post('*/api/transactions/deposit', () => {
        sends += 1;
        return serviceUnavailable({ via: 'api', instance: '/api/transactions/deposit' });
      }),
    );
    renderDeposit();
    await depositHundred();
    // Whatever the words, the first attempt's outcome is unknown and its key is held.
    await screen.findByRole('alert');

    await userEvent.click(screen.getByRole('button', { name: '€200' }));

    expect(await screen.findByText("We couldn't confirm your deposit")).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Deposit/ })).not.toBeInTheDocument();
    expect(sends).toBe(1);
  });

  it.each([
    ['typing in the description', () => userEvent.type(screen.getByLabelText('Description'), 'R')],
    [
      'choosing the other account',
      () => userEvent.click(screen.getByRole('button', { name: /Rainy Day/ })),
    ],
  ])(
    'after an unknown outcome, %s is an edit: it asks for a check and never sends a second key',
    async (_edit, edit) => {
      let sends = 0;
      server.use(
        http.post('*/api/transactions/deposit', () => {
          sends += 1;
          return serviceUnavailable({ via: 'api', instance: '/api/transactions/deposit' });
        }),
      );
      const other = mockState.accounts[1];
      renderDeposit([
        moneyAccount(),
        { id: other.id, name: 'Rainy Day', accountNumber: 'AB-••••-••••-91', balance: 500 },
      ]);
      await depositHundred();
      await alertSays(COPY.depositUnknown);

      await edit();

      expect(await screen.findByText("We couldn't confirm your deposit")).toBeInTheDocument();
      expect(screen.queryByRole('button', { name: /^Deposit/ })).not.toBeInTheDocument();
      expect(sends).toBe(1);
    },
  );

  it.each([
    [
      'tapping the amount already chosen',
      () => userEvent.click(screen.getByRole('button', { name: '€100' })),
    ],
    [
      'choosing the account already selected',
      () => userEvent.click(screen.getByRole('button', { name: /Main Account/ })),
    ],
    [
      'typing a character the amount field drops',
      () => userEvent.type(screen.getByLabelText('Deposit amount'), ','),
    ],
  ])(
    'after an unknown outcome, %s is no edit: Deposit again re-sends the same key',
    async (_gesture, gesture) => {
      const keys: (string | null)[] = [];
      server.use(
        http.post('*/api/transactions/deposit', ({ request }) => {
          keys.push(request.headers.get('Idempotency-Key'));
          return serviceUnavailable({ via: 'api', instance: '/api/transactions/deposit' });
        }),
      );
      renderDeposit();
      await depositHundred();
      await alertSays(COPY.depositUnknown);

      await gesture();

      // The deposit is the one already sent, so the words and the button they point at stay.
      expect(screen.queryByText("We couldn't confirm your deposit")).not.toBeInTheDocument();
      await alertSays(COPY.depositUnknown);
      await userEvent.click(screen.getByRole('button', { name: 'Deposit €100.00' }));
      await waitFor(() => expect(keys).toHaveLength(2));
      expect(keys[1]).toBe(keys[0]);
    },
  );
});

describe('a money send whose answer arrives but cannot be read', () => {
  /*
    A 2xx whose body fails its schema rejects with no HTTP status. The server acted, so the key's
    hook latches verify-first, and the dialog must say only that: its "…failed. Please try again."
    beneath the verify view would tell the visitor the money did not move.
  */
  const unreadable = () => HttpResponse.json({ data: null, message: 'Done.' }, { status: 201 });

  /**
   * RTK logs a response transform that throws, once, on console.error — which this suite treats as
   * a failure. Here that error is the scenario, so it is stubbed, and asserted to be that one.
   */
  function expectTheUnreadableAnswerLogged() {
    const logged = vi.spyOn(console, 'error').mockImplementation(() => {});
    return () => {
      expect(logged).toHaveBeenCalledTimes(1);
      expect(String(logged.mock.calls[0][0])).toMatch(/An unhandled error occurred processing/);
    };
  }

  it('deposit: asks for a check, and says nothing failed', async () => {
    const expectLogged = expectTheUnreadableAnswerLogged();
    server.use(http.post('*/api/transactions/deposit', unreadable));
    renderDeposit();
    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    await userEvent.click(screen.getByRole('button', { name: 'Deposit €100.00' }));

    expect(await screen.findByText("We couldn't confirm your deposit")).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expectLogged();
  });

  it('withdraw: asks for a check, and says nothing failed', async () => {
    const expectLogged = expectTheUnreadableAnswerLogged();
    server.use(http.post('*/api/transactions/withdraw', unreadable));
    renderWithdraw();
    await withdrawToPin();
    await userEvent.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));

    expect(await screen.findByText("We couldn't confirm your withdrawal")).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expectLogged();
  });
});

describe('a reload while a money dialog holds a key', () => {
  /*
    The key lives only in the page, so a reload after a send whose outcome is unknown would send
    the money again with a new one. The browser asks first, as on the transfer pages; before any
    send there is nothing to lose, and it does not.
  */
  it.each([
    {
      dialog: 'withdraw',
      path: '*/api/transactions/withdraw',
      open: async () => {
        renderWithdraw();
        await withdrawToPin();
      },
      send: 'Withdraw €100.00',
      words: COPY.withdrawalUnknown,
    },
    {
      dialog: 'deposit',
      path: '*/api/transactions/deposit',
      open: async () => {
        renderDeposit();
        await userEvent.click(screen.getByRole('button', { name: '€100' }));
      },
      send: 'Deposit €100.00',
      words: COPY.depositUnknown,
    },
  ])(
    '$dialog: the browser asks before a reload once a send is unknown, and not before',
    async (flow) => {
      server.use(
        http.post(flow.path, () =>
          serviceUnavailable({ via: 'api', instance: flow.path.slice(1) }),
        ),
      );
      await flow.open();
      expect(reloadAsksFirst()).toBe(false);

      await userEvent.click(screen.getByRole('button', { name: flow.send }));
      await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent(flow.words));

      expect(reloadAsksFirst()).toBe(true);
    },
  );
});

describe('a money dialog on the Dashboard, when the accounts behind it fail', () => {
  /*
    The Dashboard swaps its sections for the accounts bar when its accounts read fails, and the
    withdraw dialog's funds check reads those same accounts. A dialog mounted inside the sections
    went with them, and with it the key of a send whose outcome is unknown: the next Withdraw
    would have been a new intent beside one that may have landed.
  */
  it('stays open, and pressing Withdraw again re-sends the same key', async () => {
    const keys: (string | null)[] = [];
    let accountsDown = false;
    server.use(
      http.post('*/api/transactions/withdraw', ({ request }) => {
        keys.push(request.headers.get('Idempotency-Key'));
        return serviceUnavailable({ via: 'api', instance: '/api/transactions/withdraw' });
      }),
      http.get('*/api/accounts', () =>
        accountsDown
          ? serviceUnavailable({ via: 'api', instance: '/api/accounts', retryAfterSeconds: 1 })
          : undefined,
      ),
    );
    renderWithProviders(
      <Routes>
        <Route path="/" element={<DashboardPage />} />
      </Routes>,
      { routerEntries: ['/'] },
    );
    await userEvent.click(await screen.findByRole('button', { name: 'Withdraw' }));
    await withdrawToPin();
    await userEvent.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));
    const dialog = screen.getByRole('dialog');
    await waitFor(() =>
      expect(within(dialog).getByRole('alert')).toHaveTextContent(COPY.withdrawalUnknown),
    );

    // Back to the amount, and Continue again while the accounts cannot be read: the funds check
    // and its one retry fail, and the page behind the dialog gives way to the accounts bar.
    await userEvent.click(within(dialog).getByRole('button', { name: 'Back' }));
    accountsDown = true;
    await userEvent.click(within(dialog).getByRole('button', { name: /^Continue/ }));
    const pageAlert = () =>
      Array.from(document.querySelectorAll<HTMLElement>('[role="alert"]')).find(
        (alert) => !dialog.contains(alert),
      );
    await waitFor(() => expect(pageAlert()?.textContent ?? '').toContain(COPY.unavailable), {
      timeout: 5_000,
    });

    expect(screen.getByRole('dialog')).toBe(dialog);
    await within(dialog).findByText('Verify Withdrawal');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Withdraw €100.00' }));
    await waitFor(() => expect(keys).toHaveLength(2));
    expect(keys[1]).toBe(keys[0]);
  });
});

describe("a money dialog's wait hint", () => {
  /**
   * The hint's checks shared by both dialogs: inside the dialog, no promise, no stop, no nesting;
   * and from 20 s the one thing that keeps a slow send safe, staying on the page, since a reload
   * would send it again with a new key.
   */
  async function expectWriteHint(sentAt: number) {
    const dialog = screen.getByRole('dialog');
    await advanceUntil(sentAt, 5_000);
    hintRegion(COPY.slow, dialog);
    await advanceUntil(sentAt, 20_000);
    const region = hintRegion(COPY.keepPageOpen, dialog);

    expect(within(dialog).queryByText(COPY.noDoubleCharge, { exact: false })).toBeNull();
    expect(within(dialog).queryByRole('button', { name: COPY.stopWaiting })).toBeNull();
    expect(region.closest('[data-wait-hint]')).not.toBeNull();
    expectNoNestedLiveRegions();
  }

  it('deposit: lives in the dialog, and promises nothing while the send is pending', async () => {
    let sentAt = 0;
    server.use(
      http.post('*/api/transactions/deposit', () => {
        sentAt = Date.now();
        return never();
      }),
    );
    renderDeposit();
    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    installFakeClock();
    const user = fakeClockUser();
    await user.click(screen.getByRole('button', { name: 'Deposit €100.00' }));
    await waitFor(() => expect(sentAt).toBeGreaterThan(0));

    await expectWriteHint(sentAt);
  });

  it('withdraw: lives in the dialog, and promises nothing while the send is pending', async () => {
    let sentAt = 0;
    server.use(
      http.post('*/api/transactions/withdraw', () => {
        sentAt = Date.now();
        return never();
      }),
    );
    renderWithdraw();
    await withdrawToPin();
    installFakeClock();
    const user = fakeClockUser();
    await user.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));
    await waitFor(() => expect(sentAt).toBeGreaterThan(0));

    await expectWriteHint(sentAt);
  });

  it('withdraw: while only the PIN is being checked, no key exists yet, and 20 s says only "Still trying…"', async () => {
    let mintAt = 0;
    server.use(
      http.post('*/api/transactions/withdraw/authorizations', () => {
        mintAt = Date.now();
        return never();
      }),
    );
    renderWithdraw();
    await withdrawToPin();
    installFakeClock();
    const user = fakeClockUser();
    await user.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));
    await waitFor(() => expect(mintAt).toBeGreaterThan(0));
    const dialog = screen.getByRole('dialog');
    const shownAt = await hintShownAt(dialog);

    await advanceUntil(shownAt, 25_000);

    hintRegion(COPY.stillTrying, dialog);
    expect(within(dialog).queryByText(COPY.keepPageOpen)).toBeNull();
  });

  it("withdraw: a slow funds check beside a PIN lock keeps its hint out of the lock's described-by target", async () => {
    /*
      The one state in which the PIN's `aria-describedby` target and a wait are on screen together:
      the lock holds that target open (its banner and countdown), Back returns to the amount, and
      Continue starts the funds check. A hint inside the target would be read as part of why the
      PIN failed, every time the PIN field is focused.
    */
    server.use(
      http.post('*/api/transactions/withdraw/authorizations', () =>
        problem({
          status: 429,
          errorCode: 'PIN_LOCKED',
          detail: 'Too many incorrect PIN attempts.',
          extensions: { retryAfterSeconds: 900 },
        }),
      ),
    );
    renderWithdraw();
    await withdrawToPin();
    await userEvent.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));
    await screen.findByText('Too many incorrect PIN attempts.');
    const target = screen.getByRole('timer').parentElement as HTMLElement;
    expect(target.id).not.toBe('');
    await userEvent.click(screen.getByRole('button', { name: 'Back' }));

    let checkedAt = 0;
    server.use(
      http.get('*/api/accounts', () => {
        checkedAt = Date.now();
        return never();
      }),
    );
    installFakeClock();
    const user = fakeClockUser();
    await user.click(screen.getByRole('button', { name: /^Continue/ }));
    await waitFor(() => expect(checkedAt).toBeGreaterThan(0));

    await advanceUntil(checkedAt, 5_000);
    const region = hintRegion(COPY.slow, screen.getByRole('dialog'));
    expect(document.getElementById(target.id)).not.toContainElement(region);
    expect(document.getElementById(target.id)?.querySelector('[data-wait-hint]')).toBeNull();
    expect(screen.queryByRole('button', { name: COPY.stopWaiting })).toBeNull();
  });
});

describe('a change with no key, during an outage', () => {
  async function createHolidayFund() {
    renderWithProviders(<CreateAccountDialog open onClose={vi.fn()} />);
    fireEvent.change(screen.getByRole('textbox', { name: /account name/i }), {
      target: { value: 'Holiday Fund' },
    });
  }

  it('creating an account that answered 503 says the account may exist', async () => {
    server.use(
      http.post('*/api/accounts', () =>
        serviceUnavailable({ via: 'api', instance: '/api/accounts' }),
      ),
    );
    await createHolidayFund();
    await userEvent.click(screen.getByRole('button', { name: 'Create Account' }));

    await alertSays(COPY.accountUnknown);
  });

  it('creating an account with no answer ends at 65 s with the same words', async () => {
    let sentAt = 0;
    server.use(
      http.post('*/api/accounts', () => {
        sentAt = Date.now();
        return never();
      }),
    );
    await createHolidayFund();
    installFakeClock();
    const user = fakeClockUser();
    await user.click(screen.getByRole('button', { name: 'Create Account' }));
    await waitFor(() => expect(sentAt).toBeGreaterThan(0));

    await advanceUntil(sentAt, 65_100);

    await alertSays(COPY.accountUnknown);
  });

  it('an account opened by a create that answered 503 shows up in the list the dialog points to', async () => {
    server.use(
      http.post('*/api/accounts', () => {
        // The database kept the account; only the answer was lost.
        mockState.accounts.push({
          ...MAIN,
          id: '99999999-9999-4999-8999-999999999999',
          name: 'Holiday Fund',
          isPrimary: false,
        });
        return serviceUnavailable({ via: 'api', instance: '/api/accounts' });
      }),
    );
    renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });
    await screen.findByText('Main Account');

    await userEvent.click(screen.getByRole('button', { name: 'Add New Account' }));
    fireEvent.change(await screen.findByRole('textbox', { name: /account name/i }), {
      target: { value: 'Holiday Fund' },
    });
    await userEvent.click(screen.getByRole('button', { name: 'Create Account' }));
    await alertSays(COPY.accountUnknown);
    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(await screen.findByText('Holiday Fund')).toBeInTheDocument();
  });

  it('reads the list again once the dialog closes, not behind it while it is open', async () => {
    /*
      Behind an open modal the page is hidden from assistive technology, so a list read there that
      failed would put its bar where nobody hears it, in place of the card focus returns to. The
      list is read again when the visitor goes back to it.
    */
    const listReads: number[] = [];
    server.use(
      http.post('*/api/accounts', () =>
        serviceUnavailable({ via: 'api', instance: '/api/accounts' }),
      ),
      http.get('*/api/accounts', () => {
        listReads.push(Date.now());
        return undefined;
      }),
    );
    renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });
    await screen.findByText('Main Account');
    const loads = listReads.length;

    await userEvent.click(screen.getByRole('button', { name: 'Add New Account' }));
    fireEvent.change(await screen.findByRole('textbox', { name: /account name/i }), {
      target: { value: 'Holiday Fund' },
    });
    await userEvent.click(screen.getByRole('button', { name: 'Create Account' }));
    await alertSays(COPY.accountUnknown);
    await act(() => new Promise<void>((resolve) => setTimeout(resolve, 200)));
    expect(listReads).toHaveLength(loads);

    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));

    await waitFor(() => expect(listReads).toHaveLength(loads + 1));
  });

  async function fillPins() {
    renderWithProviders(<ChangePinDialog onClose={vi.fn()} />);
    for (const [name, pin] of [
      ['Current PIN', '123456'],
      ['New PIN', '246802'],
      ['Confirm new PIN', '246802'],
    ]) {
      await userEvent.click(
        within(screen.getByRole('group', { name })).getByLabelText('Digit 1 of 6'),
      );
      await userEvent.paste(pin);
    }
  }

  it('changing the PIN when the answer is 503 says the change may be saved', async () => {
    server.use(
      http.post('*/bff/auth/set-pin', () =>
        serviceUnavailable({ via: 'bff', instance: '/bff/auth/set-pin' }),
      ),
    );
    await fillPins();
    await userEvent.click(screen.getByRole('button', { name: 'Change PIN' }));

    await alertSays(COPY.saveUnknown);
  });

  it('changing the PIN with no answer ends at 65 s with the same words, not "check your connection"', async () => {
    let sentAt = 0;
    server.use(
      http.post('*/bff/auth/set-pin', () => {
        sentAt = Date.now();
        return never();
      }),
    );
    await fillPins();
    installFakeClock();
    const user = fakeClockUser();
    await user.click(screen.getByRole('button', { name: 'Change PIN' }));
    await waitFor(() => expect(sentAt).toBeGreaterThan(0));

    await advanceUntil(sentAt, 65_100);

    await alertSays(COPY.saveUnknown);
    expect(screen.queryByText(CONNECTION_FAILED)).not.toBeInTheDocument();
  });

  it('renaming an account that answered 503 says the change may be saved', async () => {
    const id = MAIN.id;
    server.use(
      http.patch('*/api/accounts/:id', () =>
        serviceUnavailable({ via: 'api', instance: `/api/accounts/${id}` }),
      ),
    );
    renderWithProviders(
      <RenameAccountDialog account={{ id, name: 'Main Account' }} onClose={vi.fn()} />,
    );
    fireEvent.change(screen.getByRole('textbox', { name: 'Account name' }), {
      target: { value: 'Renamed' },
    });
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    await alertSays(COPY.saveUnknown);
  });

  it('renaming the handle when the answer is 503 says the change may be saved', async () => {
    server.use(
      http.patch('*/bff/auth/azuretag', () =>
        serviceUnavailable({ via: 'bff', instance: '/bff/auth/azuretag' }),
      ),
    );
    renderWithProviders(<RenameAzureTagDialog currentTag="demo_user" onClose={vi.fn()} />);
    fireEvent.change(screen.getByRole('textbox', { name: /public handle/i }), {
      target: { value: 'new_handle' },
    });
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    await alertSays(COPY.saveUnknown);
  });

  it.each([
    [
      'creating an account',
      http.post,
      '*/api/accounts',
      'Create Account',
      async () => {
        await createHolidayFund();
      },
    ],
    [
      'changing the PIN',
      http.post,
      '*/bff/auth/set-pin',
      'Change PIN',
      async () => {
        await fillPins();
      },
    ],
    [
      'renaming an account',
      http.patch,
      '*/api/accounts/:id',
      'Save',
      async () => {
        renderWithProviders(
          <RenameAccountDialog account={{ id: MAIN.id, name: 'Main Account' }} onClose={vi.fn()} />,
        );
        fireEvent.change(screen.getByRole('textbox', { name: 'Account name' }), {
          target: { value: 'Renamed' },
        });
      },
    ],
    [
      'renaming the handle',
      http.patch,
      '*/bff/auth/azuretag',
      'Save',
      async () => {
        renderWithProviders(<RenameAzureTagDialog currentTag="demo_user" onClose={vi.fn()} />);
        fireEvent.change(screen.getByRole('textbox', { name: /public handle/i }), {
          target: { value: 'new_handle' },
        });
      },
    ],
  ])(
    '%s says it is slow while it waits, inside the dialog, with no way to stop it',
    async (_change, method, path, button, open) => {
      let sentAt = 0;
      server.use(
        method(path, () => {
          sentAt = Date.now();
          return never();
        }),
      );
      await open();
      const dialog = screen.getByRole('dialog');
      installFakeClock();
      const user = fakeClockUser();
      const clickedAt = Date.now();
      await user.click(within(dialog).getByRole('button', { name: button }));
      await waitFor(() => expect(sentAt).toBeGreaterThan(0));
      const shownAt = await hintShownAt(dialog);

      await advanceUntil(clickedAt, 4_900);
      expect(screen.queryByText(COPY.slow)).toBeNull();
      await advanceUntil(shownAt, 5_000);
      hintRegion(COPY.slow, dialog);
      await advanceUntil(shownAt, 20_000);
      hintRegion(COPY.stillTrying, dialog);

      expect(within(dialog).queryByRole('button', { name: COPY.stopWaiting })).toBeNull();
      expectNoNestedLiveRegions();
    },
  );
});
