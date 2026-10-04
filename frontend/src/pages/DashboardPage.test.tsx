import { Route, Routes } from 'react-router-dom';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { http } from 'msw';
import { AppToaster } from '../components/feedback';
import { apiSlice } from '../features/api/apiSlice';
import { AuthBootstrap } from '../features/auth';
import { server } from '../mocks/server';
import { MOCK_USER, mockState, seedMockDemoCopy } from '../mocks/state';
import { problem, serviceUnavailable } from '../mocks/problem';
import { enableDemoMode, rememberDemoCopy } from '../test/demoMode';
import { emulateFocusFixup, never } from '../test/outage';
import { makeTestStore, renderWithProviders, type TestStore } from '../test/renderWithProviders';
import { DashboardPage } from './DashboardPage';
import { resolveScopedAccountId } from './dashboardScope';

/**
 * The dashboard, and the one property the whole design hangs on.
 *
 * **A running balance beside a cross-account total cannot reconcile.** `balanceAfter` is
 * per-account; the hero is a sum. Printing both is how the old screen came to show €2,200.50 on its
 * newest row under a €2,080.50 heading — two numbers contradicting each other on a banking home
 * page, which is the kind of thing that costs trust rather than points.
 *
 * So the scope selector is not decoration: it decides what the page is ABOUT, and the Balance
 * column exists only when the answer is one account. That is what most of this file asserts.
 */

function renderDashboard() {
  return renderWithProviders(
    <Routes>
      <Route path="/" element={<DashboardPage />} />
      <Route path="/transactions/:id" element={<div>TX DETAIL</div>} />
      <Route path="/accounts" element={<div>ACCOUNTS PAGE</div>} />
      <Route path="/about" element={<div>ABOUT PAGE</div>} />
    </Routes>,
    { routerEntries: ['/'] },
  );
}

/** The ledger's column headers, which are the observable side of the thesis. */
const columns = () => [...document.querySelectorAll('th')].map((th) => th.textContent?.trim());

describe('the scope decides what the page is about', () => {
  it('sums every account, and prints NO running balance while it does', async () => {
    renderDashboard();
    const heading = await screen.findByRole('heading', { level: 1 });

    // 1250.50 + 830.00
    expect(heading).toHaveTextContent('€2,080.50');
    expect(screen.getByText(/Across 2 accounts/)).toBeInTheDocument();

    // The load-bearing absence. A Balance column here would be a per-account figure under a
    // cross-account heading.
    expect(columns()).toEqual(['When', 'Entry', 'Amount', 'Status']);
  });

  it('re-points the heading at one account and only THEN shows the balance column', async () => {
    renderDashboard();
    await screen.findByRole('heading', { level: 1 });

    await userEvent.click(screen.getByRole('button', { name: /Main Account/ }));

    expect(await screen.findByRole('heading', { level: 1 })).toHaveTextContent('€1,250.50');
    expect(columns()).toEqual(['When', 'Entry', 'Amount', 'Balance', 'Status']);
    expect(screen.getByRole('button', { name: /Main Account/ })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
  });

  it('names the accounts by their masked numbers, and marks the primary one', async () => {
    renderDashboard();
    await screen.findByRole('heading', { level: 1 });

    const main = screen.getByRole('button', { name: /Main Account/ });
    // The tail is how a person tells two accounts apart; the full number never appears here.
    // `maskAccountNumber` renders `AB-••••-••••-90`: the bank prefix and the last two digits.
    expect(main.textContent).toMatch(/AB-.*90/);
    expect(main).toHaveTextContent('PRIMARY');
    expect(screen.getByRole('button', { name: /Rainy Day/ })).toHaveTextContent('€830.00');
  });

  it('treats a lone account as the scope, chips or no chips', async () => {
    // One account means "all accounts" IS that account: one ledger, so the running balance
    // reconciles, and withholding the column would be the rule declining to apply itself where it
    // is trivially true. No chips render in that case — nothing to choose between — so nothing
    // else would ever set the scope.
    //
    // The state is trimmed rather than the handler overridden. A hand-written response has to
    // reproduce the envelope AND the field names exactly, and my first attempt got both wrong
    // (`message` missing, `accountType`/`currency` instead of `type`), so the page rendered its
    // accounts-error state and the test failed for a reason that had nothing to do with scope.
    // Trimming the seed cannot drift from the handler, because the handler still builds it.
    mockState.accounts = mockState.accounts.slice(0, 1);
    renderDashboard();

    expect(await screen.findByRole('heading', { level: 1 })).toHaveTextContent('€1,250.50');
    expect(columns()).toEqual(['When', 'Entry', 'Amount', 'Balance', 'Status']);
    expect(screen.queryByRole('button', { name: /All accounts/ })).toBeNull();
  });

  it('scopes the month to the selected account, figures and all', async () => {
    // This test used to assert the opposite — that the card SAID it could not be filtered, because
    // `getTransactionSummary` took no AccountId. It does now, so the assertion is the behaviour
    // that replaced the apology.
    //
    // The money figure is what makes it an assertion rather than a label check: the seed splits
    // transactions across both accounts on purpose, so a scoped total that still matched the
    // all-accounts one would mean the parameter reached the query and changed nothing.
    renderDashboard();
    await screen.findByRole('heading', { level: 1 });

    const moneyIn = async () => {
      const row = (await screen.findByText('Money in')).parentElement!;
      return row.textContent!.replace('Money in', '').trim();
    };

    /*
      ABSOLUTE figures, not "the scoped one differs from the other".

      The relative form was the original, and it is a bad oracle twice over. It passes on any wrong
      number that merely differs from the total, and it FAILS when the two are legitimately equal —
      which is exactly how it read when the month rolled over and every figure went to zero:
      `expected '+€0.00' not to be '+€0.00'`, a message that names no expectation and points at
      nothing. These come from the seed's own amounts: money in across both accounts is the 1250.50
      salary plus 175 of top-ups and a transfer in; Rainy Day's share of that is 175.
    */
    expect(await screen.findByText(/Across all 2 accounts/)).toBeInTheDocument();
    await waitFor(async () => expect(await moneyIn()).toBe('+€1,525.50'));

    await userEvent.click(screen.getByRole('button', { name: /Rainy Day/ }));

    expect(await screen.findByText('Rainy Day only')).toBeInTheDocument();
    await waitFor(async () => expect(await moneyIn()).toBe('+€175.00'));
  });
});

describe('the scope as a query argument', () => {
  // A pure function, so the stale case is reachable at all. The version written inline in the
  // component was covered by a render test that removed the account and re-rendered — and that test
  // passed with the check DELETED, because a second `render` mounts a fresh component whose `scope`
  // is back to 'all'. It asserted nothing; this asserts the rule.
  const accounts = [{ id: 'a' }, { id: 'b' }];

  it('sends the picked account while it exists', () => {
    expect(resolveScopedAccountId('b', accounts)).toBe('b');
  });

  it('sends nothing for the all-accounts state', () => {
    expect(resolveScopedAccountId('all', accounts)).toBeUndefined();
  });

  it('falls back to the total when the picked account is gone', () => {
    // The one the label already got right and the query did not: an account removed from under a
    // mounted page must not keep being asked about, or the card reports 403 for a scope the page
    // no longer shows as selected.
    expect(resolveScopedAccountId('b', [{ id: 'a' }])).toBeUndefined();
  });

  it('falls back while the accounts are still loading, rather than guessing', () => {
    expect(resolveScopedAccountId('b', [])).toBeUndefined();
  });
});

describe('the ledger', () => {
  it('opens a transaction, and strikes through one that did not stand', async () => {
    renderDashboard();
    await screen.findByRole('heading', { level: 1 });

    // A reversed withdrawal put the money back. A bare minus sign says it did not.
    const reversed = screen.getByText('ATM — disputed').closest('tr')!;
    expect(within(reversed).getByText('Reversed')).toBeInTheDocument();
    expect(within(reversed).getByText(/-€30\.00/)).toHaveStyle({ textDecoration: 'line-through' });

    await userEvent.click(screen.getByRole('button', { name: 'Salary' }));
    expect(await screen.findByText('TX DETAIL')).toBeInTheDocument();
  });

  it('marks each entry Completed, Pending or Reversed in words, not only in colour', async () => {
    renderDashboard();
    await screen.findByRole('heading', { level: 1 });

    const statuses = [...document.querySelectorAll('tbody tr')].map((tr) =>
      tr.lastElementChild?.textContent?.trim(),
    );
    expect(statuses).toEqual(['Completed', 'Completed', 'Pending', 'Completed', 'Reversed']);
  });
});

describe('the rest of the page', () => {
  it('turns the one pending item into a task you can open', async () => {
    renderDashboard();
    await screen.findByRole('heading', { level: 1 });

    const attention = screen.getByText('Needs attention').closest('section')!;
    // "To @john_d", not "Dinner split": the counterparty leads on a transfer now, because WHO the
    // money went to is the identifying fact and the note is why. The two screens used to disagree
    // about which of them to show for the same row.
    await userEvent.click(within(attention).getByRole('button', { name: /To @john_d/ }));
    expect(await screen.findByText('TX DETAIL')).toBeInTheDocument();
  });

  it('offers a way to reach a human that goes somewhere real', async () => {
    // The old copy promised a support team available 24/7. There is no team — this is a portfolio
    // project — and a link to nowhere is the same defect as a button that cannot do what it says.
    renderDashboard();
    await screen.findByRole('heading', { level: 1 });

    expect(screen.queryByText(/24\/7/)).toBeNull();
    await userEvent.click(screen.getByRole('link', { name: /Get in touch/ }));
    expect(await screen.findByText('ABOUT PAGE')).toBeInTheDocument();
  });

  it('hides every figure at once when asked, and says which state it is in', async () => {
    renderDashboard();
    await screen.findByRole('heading', { level: 1 });

    await userEvent.click(screen.getByRole('button', { name: 'Hide balances' }));

    expect(await screen.findByRole('heading', { level: 1 })).not.toHaveTextContent('€2,080.50');
    // The chips must go too: hiding the total while printing its parts underneath hides nothing.
    expect(screen.getByRole('button', { name: /Main Account/ })).not.toHaveTextContent('1,250.50');
    // The name says which state it is in; aria-pressed as well read "Show balances, pressed".
    expect(screen.getByRole('button', { name: 'Show balances' })).not.toHaveAttribute(
      'aria-pressed',
    );
  });

  it('opens the deposit dialog over the real account list', async () => {
    renderDashboard();
    await screen.findByRole('heading', { level: 1 });

    await userEvent.click(screen.getByRole('button', { name: /Deposit/ }));
    expect(await screen.findByRole('dialog')).toBeInTheDocument();
  });
});

describe('a partial failure', () => {
  it('keeps the summary sectional: the ledger and the balance survive it', async () => {
    server.use(
      http.get('*/api/transactions/summary', () =>
        problem({ status: 500, detail: 'Summary unavailable' }),
      ),
    );
    renderDashboard();

    // D22: accounts gate the page, everything else fails alone.
    expect(await screen.findByRole('heading', { level: 1 })).toHaveTextContent('€2,080.50');
    expect(await screen.findByText(/Could not load this month/)).toBeInTheDocument();
    expect(screen.getByText('Salary')).toBeInTheDocument();
  });

  it('gates the whole page when the accounts themselves fail', async () => {
    server.use(
      http.get('*/api/accounts', () => problem({ status: 500, detail: 'Accounts unavailable' })),
    );
    renderDashboard();

    expect(
      await screen.findByText(/Could not load your accounts|Accounts unavailable/),
    ).toBeInTheDocument();
    expect(screen.queryByRole('heading', { level: 1 })).toBeNull();
  });
});

/*
  The dashboard on the demo: the panel that says what the visitor holds, where it sits, and what
  starting over from it does to the page around it.

  The words are typed out here and not imported from the product, so a test fails the day the words
  on screen are no longer these. The address is the second copy's of the mock's pool
  (src/mocks/state.ts): a fixture no server knows.
*/
const DEMO = {
  heading: 'Your private copy',
  notTheOwner:
    'This is a private demo copy. It works for 24 hours from its first use, then it is closed and deleted.',
  startOver: 'Start over',
  startOverTitle: 'Start over with a new copy?',
  newCopy: 'You have a new copy.',
  welcome: 'Welcome to AzureBank',
} as const;

const CLAIM = '*/bff/auth/demo/claim';
const ACCOUNTS = '*/api/accounts';
const KEY = 'azurebank.demoCopy';
const SECOND_COPY = 'demo-4h9d2s7f1g6j3k8a@azurebank.example';
/** What a demo copy starts with, 12,450.00 and 2,300.00, and that sum with ten more in it. */
const STARTING_SUM = '€14,750.00';
const SUM_WITH_TEN_MORE = '€14,760.00';

/**
 * The dashboard as the app draws it for whoever is signed in: the app asks who that is from its
 * root, beside its routes, and mounts the toasts' outlet there (src/App.tsx).
 */
function renderSignedInDashboard(store?: TestStore) {
  return renderWithProviders(
    <>
      <AuthBootstrap />
      <AppToaster />
      <Routes>
        <Route path="/" element={<DashboardPage />} />
        <Route path="/accounts" element={<div>ACCOUNTS PAGE</div>} />
      </Routes>
    </>,
    { routerEntries: ['/'], store },
  );
}

/** Every panel on the page: the region its heading names. None is `[]`, not an error. */
const panels = () => screen.queryAllByRole('region', { name: DEMO.heading });

/** What the page's one level-1 heading reads: the balance, or the welcome. `null` for none. */
const levelOne = () => document.querySelector('h1')?.textContent ?? null;

/** The panel's "Start over". With the dialog open the page has a second button of that name. */
function panelStartOver(): HTMLButtonElement[] {
  const panel = panels()[0];
  return panel
    ? (within(panel).queryAllByRole('button', { name: DEMO.startOver }) as HTMLButtonElement[])
    : [];
}

/** Where focus is: the focused control by its name, and whether it is the panel's "Start over". */
function focus() {
  const active = document.activeElement;
  const on =
    active === null || active === document.body
      ? 'the page'
      : active.getAttribute('role') === 'alertdialog'
        ? 'the dialog itself'
        : (active.getAttribute('aria-label') ?? active.textContent);
  return { on, onThePanelsButton: active !== null && active === panelStartOver()[0] };
}

/** The title of each dialog that is open. A closed one is in the page, hidden, and is not one. */
const openDialogs = () =>
  screen
    .queryAllByRole('alertdialog')
    .map((dialog) => within(dialog).queryByRole('heading')?.textContent);

/** Whether each element is in the page and comes after the one before it, in document order. */
function inOrder(...elements: (Element | null | undefined)[]): boolean {
  return elements.every((element, index) => {
    if (!element) return false;
    const before = elements[index - 1];
    return (
      index === 0 ||
      (!!before &&
        (before.compareDocumentPosition(element) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0)
    );
  });
}

/** The accounts are in the cache and no read of the page is on its way. */
function readsAtRest(store: TestStore): boolean {
  const reads = Object.values(store.getState().api.queries);
  return (
    reads.some((read) => read?.endpointName === 'getAccounts' && read.status === 'fulfilled') &&
    reads.every((read) => read?.status !== 'pending')
  );
}

/**
 * The owner of the mock's first copy on the dashboard, with ten more in the copy's first account
 * than a copy starts with: a sum on the page that no new copy has.
 */
async function openTheOwnersDashboard() {
  enableDemoMode();
  rememberDemoCopy(seedMockDemoCopy().copy);
  mockState.accounts[0].balance += 10;
  const requests = { accounts: 0, claims: 0 };
  server.use(
    http.get(ACCOUNTS, () => {
      requests.accounts += 1;
    }),
    http.post(CLAIM, () => {
      requests.claims += 1;
    }),
  );
  const view = renderSignedInDashboard();
  await waitFor(() =>
    expect({ balance: levelOne(), panels: panels().length }).toStrictEqual({
      balance: SUM_WITH_TEN_MORE,
      panels: 1,
    }),
  );
  return { ...view, requests };
}

/**
 * "Start over" in the panel, then in the dialog, and the page left to come to rest.
 *
 * Returns what the page showed each time it changed on the way, with who the store said was
 * signed in at that moment. A change of the page is the only moment looked at: between two of
 * them a visitor sees what the last one drew.
 */
async function startOverFromThePanel(store: TestStore) {
  const seen: {
    signedInAs: string | null;
    balance: string | null;
    panels: number;
    saysSomeoneElses: number;
  }[] = [];
  const look = () =>
    seen.push({
      signedInAs: store.getState().auth.user?.email ?? null,
      balance: levelOne(),
      panels: panels().length,
      saysSomeoneElses: screen.queryAllByText(DEMO.notTheOwner).length,
    });
  const watcher = new MutationObserver(look);
  watcher.observe(document.body, {
    subtree: true,
    childList: true,
    characterData: true,
    attributes: true,
  });
  try {
    await userEvent.click(panelStartOver()[0]);
    await waitFor(() => expect(openDialogs()).toStrictEqual([DEMO.startOverTitle]));
    // Inside the dialog: "Start over" is also the panel's button, and the first words of the
    // dialog's title.
    await userEvent.click(
      within(screen.getByRole('alertdialog', { name: DEMO.startOverTitle })).getByRole('button', {
        name: DEMO.startOver,
      }),
    );
    await screen.findAllByText(DEMO.newCopy);
    await waitFor(() =>
      expect({
        signedInAs: store.getState().auth.user?.email,
        dialogs: openDialogs(),
        readsAtRest: readsAtRest(store),
      }).toStrictEqual({ signedInAs: SECOND_COPY, dialogs: [], readsAtRest: true }),
    );
  } finally {
    watcher.disconnect();
  }
  look();
  return seen;
}

describe("on the demo: the panel about the visitor's copy", () => {
  afterEach(() => {
    // A test that spied on storage puts it back itself; one that failed before that line would
    // hand its spy to every test after it.
    vi.restoreAllMocks();
  });

  it('without the tag the dashboard has no "Your private copy", and nothing reads the demo\'s key', async () => {
    // A copy under the key for the very address that is signed in, as a visit to the demo on this
    // origin would have left one: everything an owner has, but for the tag.
    rememberDemoCopy({
      email: MOCK_USER.email,
      password: 'Xk7p-Rm3w-Hn8d-Tq5v',
      pin: '123456',
      contacts: ['jane_k7m2', 'mike_k7m2'],
      expiresAt: '2031-07-15T12:30:00.000Z',
    });
    const reads = vi.spyOn(Storage.prototype, 'getItem');
    const readsOfTheKey = () => reads.mock.calls.filter(([key]) => key === KEY).length;
    const signedInAndLoaded = async (store: TestStore) => {
      await waitFor(() => expect(store.getState().auth.user?.email).toBe(MOCK_USER.email));
      await screen.findByRole('heading', { level: 1 });
    };
    try {
      const off = renderSignedInDashboard();
      await signedInAndLoaded(off.store);
      // The page's column is found by the page's alert, which is one of its children. The boxes
      // looked at are the others.
      const alert = document.querySelector('[role="alert"]');
      const boxes = Array.from(alert?.parentElement?.children ?? []).filter((box) => box !== alert);
      const offTheDemo = {
        panels: panels().length,
        readsOfTheKey: readsOfTheKey(),
        // No dialog in the page either, open or closed: the one the panel opens is the demo's.
        dialogs: document.querySelectorAll('[role="alertdialog"]').length,
        // The column was found and holds more than the alert. Without either, the count below
        // would be a count of nothing, and 0 whatever the page left behind.
        pageHasItsAlert: alert !== null,
        boxesBesideTheAlert: boxes.length > 0,
        // And nothing is left where the panel would be: no child of the page's column is an
        // empty box, but for the alert, which is there, empty, on purpose.
        emptyBoxes: boxes.filter((box) => box.innerHTML === '').length,
      };
      off.unmount();

      // The same visitor on the same browser, on a page that says it is the demo.
      enableDemoMode();
      const on = renderSignedInDashboard();
      await signedInAndLoaded(on.store);
      await waitFor(() =>
        expect({
          offTheDemo,
          onTheDemo: { panels: panels().length, keyWasRead: readsOfTheKey() > 0 },
        }).toStrictEqual({
          offTheDemo: {
            panels: 0,
            readsOfTheKey: 0,
            dialogs: 0,
            pageHasItsAlert: true,
            boxesBesideTheAlert: true,
            emptyBoxes: 0,
          },
          onTheDemo: { panels: 1, keyWasRead: true },
        }),
      );
    } finally {
      reads.mockRestore();
    }
  });

  it("the panel sits between the page's alert and its sections, and wears the page's card", async () => {
    await openTheOwnersDashboard();
    const panel = panels()[0];
    const alert = document.querySelector('[role="alert"]');
    const hero = document.querySelector('h1')?.closest('section');
    // What makes a card of a box on this page, as the browser would compute it.
    const card = (box: Element | null | undefined) => {
      if (!box) return null;
      const style = getComputedStyle(box);
      return {
        radius: style.borderRadius,
        padding: style.paddingTop,
        shadow: style.boxShadow,
        background: style.backgroundColor,
      };
    };

    expect({
      // A child of the page's column, as the alert and the sections' grid are: not inside either.
      besideTheAlert: panel.parentElement === alert?.parentElement,
      order: inOrder(alert, panel, hero),
      heroIsACard: card(hero)?.radius,
      wearsTheSameCard: card(panel),
    }).toStrictEqual({
      besideTheAlert: true,
      order: true,
      heroIsACard: '14px',
      wearsTheSameCard: card(hero),
    });
  });

  it("starting over shows the new copy's balance and never the old one's", async () => {
    const { store, requests } = await openTheOwnersDashboard();
    const accountsReadsBefore = requests.accounts;
    // A browser hands a disabled button's focus to the page; jsdom does not (src/test/outage.ts).
    // The dialog's confirm is disabled while the claim runs, with focus on it.
    const stopEmulating = emulateFocusFixup();
    try {
      const seen = await startOverFromThePanel(store);

      const asTheNewOwner = seen.filter(({ signedInAs }) => signedInAs === SECOND_COPY);
      expect({
        claims: requests.claims,
        // The page asked for the accounts again, after the claim: the sum is the new copy's own.
        accountsReads: { before: accountsReadsBefore, after: requests.accounts },
        // Every balance the page showed from the moment the store named the new owner. There was
        // at least one such moment, and the old copy's sum is in none: where it stood there was
        // no sum at all until the new one came.
        balancesAsTheNewOwner: [...new Set(asTheNewOwner.map(({ balance }) => balance))],
        endsAt: levelOne(),
        // Once the dialog has closed. No route changed, so nothing else moves focus.
        focus: focus(),
      }).toStrictEqual({
        claims: 1,
        accountsReads: { before: 1, after: 2 },
        balancesAsTheNewOwner: [null, STARTING_SUM],
        endsAt: STARTING_SUM,
        focus: { on: DEMO.startOver, onThePanelsButton: true },
      });
    } finally {
      stopEmulating();
    }
  });

  it("after starting over the panel never reads as someone else's", async () => {
    const { store } = await openTheOwnersDashboard();
    const buttonBefore = panelStartOver()[0];

    const seen = await startOverFromThePanel(store);

    const asTheNewOwner = seen.filter(({ signedInAs }) => signedInAs === SECOND_COPY);
    expect({
      // The page changed at least once while the store named the new owner, so the two lists
      // below are about moments that were looked at.
      lookedAsTheNewOwner: asTheNewOwner.length > 0,
      saidSomeoneElses: [...new Set(seen.map(({ saysSomeoneElses }) => saysSomeoneElses))],
      // One panel all the way through: it never left the page, so neither did its button.
      panels: [...new Set(seen.map(({ panels: count }) => count))],
      sameButton: panelStartOver()[0] === buttonBefore,
      // And it is the new copy's panel: the contacts are the second copy's.
      contacts: Array.from(panels()[0].querySelectorAll('p'))
        .map((line) => line.textContent)
        .filter((line) => line?.startsWith('Contacts you can pay:')),
      kept: (JSON.parse(localStorage.getItem(KEY) ?? '{}') as { email?: string }).email,
    }).toStrictEqual({
      lookedAsTheNewOwner: true,
      saidSomeoneElses: [0],
      panels: [1],
      sameButton: true,
      contacts: ['Contacts you can pay: @jane_p3x8 and @mike_p3x8'],
      kept: SECOND_COPY,
    });
  });

  it("in the demo, signing in to another copy never shows the last one's balance", async () => {
    // CONTROL: green before this change
    enableDemoMode();
    // Two copies claimed, as the demo's own sign-in page can sign in to either.
    const first = seedMockDemoCopy();
    const second = seedMockDemoCopy();
    const store = makeTestStore();
    const signIn = ({ copy: { email, password } }: typeof first) =>
      store.dispatch(apiSlice.endpoints.login.initiate({ email, password })).unwrap();

    // Before the page is drawn: on the first copy, its accounts read with ten more in them than a
    // copy starts with; signed out, which drops nothing; the starting sums back; on the second.
    await signIn(first);
    mockState.accounts[0].balance += 10;
    const readOnTheFirstCopy = await store
      .dispatch(apiSlice.endpoints.getAccounts.initiate())
      .unwrap();
    await store.dispatch(apiSlice.endpoints.logout.initiate()).unwrap();
    mockState.accounts[0].balance -= 10;
    await signIn(second);

    // Every balance the page shows, from its first render on.
    const seen: (string | null)[] = [];
    const watcher = new MutationObserver(() => seen.push(levelOne()));
    watcher.observe(document.body, { subtree: true, childList: true, characterData: true });
    try {
      renderSignedInDashboard(store);
      await waitFor(() =>
        expect({ aSumIsShown: levelOne() !== null, readsAtRest: readsAtRest(store) }).toStrictEqual(
          { aSumIsShown: true, readsAtRest: true },
        ),
      );
    } finally {
      watcher.disconnect();
    }

    expect({
      readOnTheFirstCopy: readOnTheFirstCopy.reduce((sum, account) => sum + account.balance, 0),
      signedInAs: store.getState().auth.user?.email,
      seen: [...new Set(seen)],
      endsAt: levelOne(),
    }).toStrictEqual({
      readOnTheFirstCopy: 14760,
      signedInAs: SECOND_COPY,
      seen: [null, STARTING_SUM],
      endsAt: STARTING_SUM,
    });
  });

  it("the panel is there whatever the page's reads are doing", async () => {
    enableDemoMode();
    rememberDemoCopy(seedMockDemoCopy().copy);
    /** The page once the panel is on it and `ready` holds: what leads it, and in which order. */
    const look = async (ready: () => boolean) => {
      await waitFor(() =>
        expect({ panels: panels().length, ready: ready() }).toStrictEqual({
          panels: 1,
          ready: true,
        }),
      );
      const alert = document.querySelector('[role="alert"]');
      return {
        levelOne: levelOne(),
        retries: screen.queryAllByRole('button', { name: 'Retry' }).length,
        alertThenPanel: inOrder(alert, panels()[0]),
        panelThenLevelOne: inOrder(panels()[0], document.querySelector('h1')),
      };
    };

    // The accounts are on their way, and stay there.
    server.use(http.get(ACCOUNTS, never));
    const loading = renderSignedInDashboard();
    const whileTheAccountsLoad = await look(() => true);
    loading.unmount();
    server.resetHandlers();

    // The accounts cannot be read: their bar stands in for the page's sections.
    server.use(
      http.get(ACCOUNTS, () =>
        serviceUnavailable({ via: 'api', instance: '/api/accounts', retryAfterSeconds: 1 }),
      ),
    );
    const down = renderSignedInDashboard();
    const whenTheAccountsFailed = await look(
      () => screen.queryAllByRole('button', { name: 'Retry' }).length === 1,
    );
    down.unmount();
    server.resetHandlers();

    // No account at all: the page is its welcome.
    mockState.accounts = [];
    renderSignedInDashboard();
    const withNoAccounts = await look(() => levelOne() !== null);

    expect({ whileTheAccountsLoad, whenTheAccountsFailed, withNoAccounts }).toStrictEqual({
      whileTheAccountsLoad: {
        levelOne: null,
        retries: 0,
        alertThenPanel: true,
        panelThenLevelOne: false,
      },
      whenTheAccountsFailed: {
        levelOne: null,
        retries: 1,
        alertThenPanel: true,
        panelThenLevelOne: false,
      },
      withNoAccounts: {
        levelOne: DEMO.welcome,
        retries: 0,
        alertThenPanel: true,
        panelThenLevelOne: true,
      },
    });
  });
});
