import { screen, waitFor, within } from '@testing-library/react';
import { menuDividerClassNames, tokens } from '@fluentui/react-components';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '../mocks/server';
import { mockState } from '../mocks/state';
import { renderWithProviders } from '../test/renderWithProviders';
import { enterPin } from '../test/pinFlow';
import { paintedIn } from '../test/fluentStates';
import { expectNoNestedLiveRegions } from '../test/liveRegions';
import { AccountsPage } from './AccountsPage';

/**
 * Account management from the card menu. Pins: rename through the
 * {Account,id} invalidation, the DoD blanket-'Account' invalidation on set-primary
 * (the badge MOVES and the list re-sorts primary-first), and the rule that 422
 * business codes render INLINE in the confirm dialog, which stays open.
 */

async function openMenu(accountName: string) {
  await userEvent.click(screen.getByRole('button', { name: `Account actions for ${accountName}` }));
}

/** A zero-balance account that is not the primary, so that it can be deleted. */
function seedTempFund(id: string) {
  mockState.accounts.push({
    id,
    accountNumber: 'AB-****-****-33',
    name: 'Temp Fund',
    type: 'Savings',
    balance: 0,
    isPrimary: false,
    createdAt: '2026-07-10T09:00:00.0000000Z',
  });
}

/** Holds every request that matches until the returned function is called; the mock answers. */
function holdUntilReleased(method: 'get' | 'patch', path: string) {
  let release = () => {};
  const held = new Promise<void>((resolve) => {
    release = resolve;
  });
  server.use(
    http[method](path, async () => {
      await held;
    }),
  );
  return release;
}

/** The sentence, inside the page's polite slot. The bar's own name is not the sentence. */
function saidInTheSlot(sentence: string) {
  const words = screen.getByText(sentence, { exact: true });
  expect(words.closest('[role="status"]')).not.toBeNull();
}

describe('rename account (A5)', () => {
  it('renames from the menu: dialog seeded with the current name, list refreshes', async () => {
    renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });
    await screen.findByText('Rainy Day');

    await openMenu('Rainy Day');
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Rename' }));

    const input = await screen.findByLabelText('Account name');
    expect(input).toHaveValue('Rainy Day');

    await userEvent.clear(input);
    await userEvent.type(input, 'Storm Fund');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    // The row's {Account,id} tag invalidates the list — never a hand-patched cache.
    expect(await screen.findByText('Storm Fund')).toBeInTheDocument();
    expect(screen.queryByText('Rainy Day')).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { level: 2, name: 'Storm Fund' })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Rainy Day' })).not.toBeInTheDocument();
    saidInTheSlot('"Rainy Day" is now called "Storm Fund".');
  });

  it('rejects a too-short name client-side before any request', async () => {
    let patches = 0;
    server.use(
      http.patch('*/api/accounts/:id', () => {
        patches += 1;
        return HttpResponse.json({ data: null, message: null }, { status: 500 });
      }),
    );
    renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });
    await screen.findByText('Rainy Day');

    await openMenu('Rainy Day');
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Rename' }));
    const input = await screen.findByLabelText('Account name');
    await userEvent.clear(input);
    await userEvent.type(input, 'X');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    expect(
      await screen.findByText('Account name must be at least 2 characters'),
    ).toBeInTheDocument();
    expect(patches).toBe(0);
  });
});

describe('set primary (A6)', () => {
  it('moves the Primary badge and re-sorts the list — the DoD blanket invalidation', async () => {
    renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });
    await screen.findByText('Main Account');

    // Seed order: primary first — Main Account leads.
    let names = screen.getAllByText(/^(Main Account|Rainy Day)$/).map((el) => el.textContent);
    expect(names[0]).toBe('Main Account');
    expect(screen.getAllByText('Primary')).toHaveLength(1);

    await openMenu('Rainy Day');
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Set as primary' }));

    // Blanket 'Account' invalidation refetches the list: badge count stays 1, the
    // list re-sorts, and Rainy Day now leads.
    await waitFor(() => {
      names = screen.getAllByText(/^(Main Account|Rainy Day)$/).map((el) => el.textContent);
      expect(names[0]).toBe('Rainy Day');
    });
    expect(screen.getAllByText('Primary')).toHaveLength(1);
    saidInTheSlot('"Rainy Day" is now your primary account.');

    // The new primary's menu no longer offers the action.
    await openMenu('Rainy Day');
    expect(await screen.findByRole('menuitem', { name: 'Rename' })).toBeInTheDocument();
    expect(screen.queryByRole('menuitem', { name: 'Set as primary' })).not.toBeInTheDocument();
    await userEvent.keyboard('{Escape}');
  });
});

describe('delete account (A7)', () => {
  /*
    Since ADR-0049 a closure costs a PIN: the dialog mints a deletion authorisation on the sixth
    digit and DELETEs with it in the header. These run against the REAL mock handlers, so they are
    also the page-level pin of the mock's mint + gated DELETE. The surface is MoneyDialogShell's
    Fluent dialog — role 'dialog' named by its title, not the hand-rolled ConfirmDialog's
    'alertdialog'. The menu item is 'Delete' and the dialog's final button 'Delete account'.
  */
  it('deletes a zero-balance account after confirmation and the PIN', async () => {
    // Seed a deletable account directly — the create FLOW is already covered by
    // accounts.test.tsx.
    seedTempFund('019f7b3f-0000-7000-8000-0000000000a3');
    renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });
    await screen.findByText('Temp Fund');

    await openMenu('Temp Fund');
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Delete' }));
    expect(await screen.findByRole('dialog', { name: 'Delete account?' })).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Delete account' }));
    // The sixth digit is the submit: mint, then DELETE with the minted id.
    await enterPin();

    await waitFor(() => expect(screen.queryByText('Temp Fund')).not.toBeInTheDocument());
    expect(screen.queryByRole('heading', { name: 'Temp Fund' })).not.toBeInTheDocument();
    await waitFor(() =>
      expect(screen.getByRole('heading', { level: 1, name: 'Accounts' })).toHaveFocus(),
    );
    saidInTheSlot('"Temp Fund" was deleted.');
    // The rest of the list is untouched.
    expect(screen.getByText('Main Account')).toBeInTheDocument();
    expect(screen.getAllByText('Primary')).toHaveLength(1);

    // One authorisation, bound to the closure, spent exactly once.
    expect(mockState.stepUpAuthorizations.size).toBe(1);
    const [minted] = [...mockState.stepUpAuthorizations.values()];
    expect(minted).toMatchObject({ operation: 'AccountDeletion', consumed: true });
  });

  it('lands on the heading when another account has the same name as the one deleted', async () => {
    seedTempFund('019f7b3f-0000-7000-8000-0000000000a3');
    seedTempFund('019f7b3f-0000-7000-8000-0000000000a4');
    renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });
    expect(await screen.findAllByRole('heading', { level: 2, name: 'Temp Fund' })).toHaveLength(2);

    const [first] = screen.getAllByRole('button', { name: 'Account actions for Temp Fund' });
    await userEvent.click(first);
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Delete' }));
    await screen.findByRole('dialog', { name: 'Delete account?' });
    await userEvent.click(screen.getByRole('button', { name: 'Delete account' }));
    await enterPin();

    await waitFor(() =>
      expect(screen.getAllByRole('heading', { level: 2, name: 'Temp Fund' })).toHaveLength(1),
    );
    await waitFor(() =>
      expect(screen.getByRole('heading', { level: 1, name: 'Accounts' })).toHaveFocus(),
    );
    saidInTheSlot('"Temp Fund" was deleted.');
  });

  it('lands on the heading only once the deleted card has left the list', async () => {
    seedTempFund('019f7b3f-0000-7000-8000-0000000000a3');
    renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });
    await screen.findByText('Temp Fund');

    await openMenu('Temp Fund');
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Delete' }));
    await screen.findByRole('dialog', { name: 'Delete account?' });
    await userEvent.click(screen.getByRole('button', { name: 'Delete account' }));
    // The list read that the deletion's tags start is held: the page still shows the card.
    const answerList = holdUntilReleased('get', '*/api/accounts');
    await enterPin();

    // The deletion is done and said, and the dialog is gone; the card is still listed, and its
    // menu button has focus back. In jsdom this check alone does not tell the two halves apart: with
    // the deletion alone as the moment, the landing runs while the menu button holds focus, so it
    // never moves focus, and what goes red is the landing after the answer below.
    await screen.findByText('"Temp Fund" was deleted.', { exact: true });
    await waitFor(() =>
      expect(screen.queryByRole('dialog', { name: 'Delete account?' })).not.toBeInTheDocument(),
    );
    expect(screen.getByRole('heading', { level: 2, name: 'Temp Fund' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Account actions for Temp Fund' })).toHaveFocus();
    const heading = screen.getByRole('heading', { level: 1, name: 'Accounts' });
    expect(heading).not.toHaveFocus();

    answerList();
    await waitFor(() => expect(screen.queryByText('Temp Fund')).not.toBeInTheDocument());
    await waitFor(() => expect(heading).toHaveFocus());
  });

  it('a non-zero balance is refused INLINE and the dialog stays open (D17)', async () => {
    renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });
    await screen.findByText('Rainy Day');

    await openMenu('Rainy Day'); // balance 830 — the real business rule refuses it
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Delete' }));
    await screen.findByRole('dialog', { name: 'Delete account?' });
    await userEvent.click(screen.getByRole('button', { name: 'Delete account' }));
    // A WRONG pin on purpose, as in ADR-0049's Row 2 (2026-09-06, main 19742ff: 422
    // NON_ZERO_BALANCE, PinAccessFailedCount unchanged). With the correct pin the counter reads 0
    // in either order (checkPinInBand resets it on a match); with this one a mock that consulted
    // the PIN first answers 401 INVALID_PIN and the counter reads 1.
    await enterPin('000000');

    expect(
      await screen.findByText('Only accounts with a zero balance can be deleted.'),
    ).toBeInTheDocument();
    expect(screen.queryByText('Invalid PIN.')).toBeNull();
    expect(screen.getByRole('dialog', { name: 'Delete account?' })).toBeInTheDocument();
    // The guard answers before the PIN is consulted, so the attempt is not spent.
    expect(mockState.pinAttempts).toBe(0);

    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(screen.getByText('Rainy Day')).toBeInTheDocument();
  });

  it('the primary account is refused with its own mapped reason', async () => {
    // The REAL rule order checks balance BEFORE primary (AccountService), so only a
    // ZERO-balance primary reaches the PRIMARY_ACCOUNT_DELETE rule — mirror that.
    const primary = mockState.accounts.find((a) => a.isPrimary);
    primary!.balance = 0;
    renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });
    await screen.findByText('Main Account');

    await openMenu('Main Account');
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Delete' }));
    await screen.findByRole('dialog', { name: 'Delete account?' });
    await userEvent.click(screen.getByRole('button', { name: 'Delete account' }));
    // This was measured with the CORRECT pin; the WRONG one here makes the counter assertion able
    // to fail — it pins the mock's shared guards-before-checkPinInBand order (the order
    // measured on the balance guard), not a measured wrong-pin-on-primary row.
    await enterPin('000000');

    expect(
      await screen.findByText(
        'This is your primary account — set another account as primary first.',
      ),
    ).toBeInTheDocument();
    expect(screen.queryByText('Invalid PIN.')).toBeNull();
    expect(screen.getByRole('dialog', { name: 'Delete account?' })).toBeInTheDocument();
    expect(mockState.pinAttempts).toBe(0);

    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));
  });
});

describe('a sentence for what the action did', () => {
  it('stays until the next action starts, and the bar is not nested in another live region', async () => {
    renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });
    await screen.findByText('Main Account');

    await openMenu('Rainy Day');
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Set as primary' }));

    expect(
      await screen.findByText('"Rainy Day" is now your primary account.', { exact: true }),
    ).toBeInTheDocument();
    saidInTheSlot('"Rainy Day" is now your primary account.');
    expectNoNestedLiveRegions();

    await userEvent.click(screen.getByRole('button', { name: 'Deposit to Rainy Day' }));
    expect(
      screen.queryByText('"Rainy Day" is now your primary account.', { exact: true }),
    ).not.toBeInTheDocument();
  });

  it('says nothing for a new primary whose answer arrives after another action started', async () => {
    renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });
    await screen.findByText('Main Account');
    const answerSetPrimary = holdUntilReleased('patch', '*/api/accounts/:id/set-primary');

    await openMenu('Rainy Day');
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Set as primary' }));
    await userEvent.click(screen.getByRole('button', { name: 'Add account' }));
    await screen.findByRole('dialog');

    answerSetPrimary();
    // The answer has arrived once the list it refreshes puts Rainy Day first.
    await waitFor(() => {
      const names = screen.getAllByText(/^(Main Account|Rainy Day)$/).map((el) => el.textContent);
      expect(names[0]).toBe('Rainy Day');
    });
    expect(
      screen.queryByText('"Rainy Day" is now your primary account.', { exact: true }),
    ).not.toBeInTheDocument();
  });
});

describe('a dialog whose answer arrives after another action started', () => {
  /** Holds the answer to a POST of `path` until the returned function is called. */
  function holdPostUntilReleased(path: string) {
    let release = () => {};
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(
      http.post(path, async () => {
        await held;
      }),
    );
    return release;
  }

  it('says nothing for a create that was cancelled before its answer arrived', async () => {
    renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });
    await screen.findByText('Main Account');
    const answerCreate = holdPostUntilReleased('*/api/accounts');

    await userEvent.click(screen.getByRole('button', { name: 'Add account' }));
    await userEvent.type(await screen.findByLabelText('Account name'), 'Holiday Pot');
    await userEvent.click(screen.getByRole('button', { name: 'Create Account' }));
    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());

    await userEvent.click(screen.getByRole('button', { name: 'Deposit to Rainy Day' }));
    answerCreate();
    // The answer has arrived once the list it refreshes holds the new account.
    await screen.findByText('Holiday Pot');
    expect(
      screen.queryByText('Your new account "Holiday Pot" is open.', { exact: true }),
    ).not.toBeInTheDocument();
  });

  it('says nothing for a rename that was cancelled before its answer arrived', async () => {
    renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });
    await screen.findByText('Rainy Day');
    const answerRename = holdUntilReleased('patch', '*/api/accounts/:id');

    await openMenu('Rainy Day');
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Rename' }));
    const input = await screen.findByLabelText('Account name');
    await userEvent.clear(input);
    await userEvent.type(input, 'Storm Fund');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());

    await userEvent.click(screen.getByRole('button', { name: 'Deposit to Main Account' }));
    answerRename();
    await screen.findByText('Storm Fund');
    expect(
      screen.queryByText('"Rainy Day" is now called "Storm Fund".', { exact: true }),
    ).not.toBeInTheDocument();
  });
});

describe('what the card says about what each action does', () => {
  it('sets Delete apart from the other items, with a separator directly before it', async () => {
    renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });
    await screen.findByText('Rainy Day');

    // Both shapes of the menu: with Set as primary (Rainy Day) and without (the primary itself).
    for (const name of ['Rainy Day', 'Main Account']) {
      await openMenu(name);
      const deleteItem = await screen.findByRole('menuitem', { name: 'Delete' });
      const before = deleteItem.previousElementSibling;
      expect(before?.classList.contains(menuDividerClassNames.root)).toBe(true);
      expect(
        deleteItem.parentElement?.querySelectorAll(`.${menuDividerClassNames.root}`),
      ).toHaveLength(1);
      await userEvent.keyboard('{Escape}');
      await waitFor(() => expect(screen.queryByRole('menuitem', { name: 'Delete' })).toBeNull());
    }
  });

  it('keeps Delete red in every state, and on the hover fill while it is pressed', async () => {
    // Fluent's pressed fill, colorNeutralBackground1Pressed, leaves the red at 4.39:1 in light;
    // on the hover fill it reads at 5.32:1.
    renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });
    await screen.findByText('Rainy Day');
    await openMenu('Rainy Day');
    const deleteItem = await screen.findByRole('menuitem', { name: 'Delete' });

    const red = paintedIn(deleteItem, 'rest', 'color');
    expect(red).not.toBe(tokens.colorNeutralForeground2);
    for (const state of ['hover', 'pointer press'] as const) {
      expect({ state, text: paintedIn(deleteItem, state, 'color') }).toEqual({ state, text: red });
    }
    expect(paintedIn(deleteItem, 'pointer press', 'background-color')).toBe(
      tokens.colorNeutralBackground1Hover,
    );
  });

  it('frames Deposit as money arriving and Withdraw as money leaving, as before', async () => {
    renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });
    await screen.findByText('Rainy Day');

    await userEvent.click(screen.getByRole('button', { name: 'Deposit to Rainy Day' }));
    const deposit = await screen.findByRole('dialog', { name: 'Deposit Money' });
    expect(deposit.querySelector('[data-tone]')?.getAttribute('data-tone')).toBe('credit');
    await userEvent.click(within(deposit).getByRole('button', { name: 'Close' }));

    await userEvent.click(await screen.findByRole('button', { name: 'Withdraw from Rainy Day' }));
    const withdraw = await screen.findByRole('dialog', { name: 'Withdraw Money' });
    expect(withdraw.querySelector('[data-tone]')?.getAttribute('data-tone')).toBe('debit');
    await userEvent.click(within(withdraw).getByRole('button', { name: 'Close' }));
  });
});
