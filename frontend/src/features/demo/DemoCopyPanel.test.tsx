import { act, cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AppToaster } from '../../components/feedback';
import { server } from '../../mocks/server';
import { seedMockDemoCopy } from '../../mocks/state';
import { enableDemoMode, rememberDemoCopy } from '../../test/demoMode';
import { expectNoNestedLiveRegions } from '../../test/liveRegions';
import { emulateFocusFixup } from '../../test/outage';
import { makeTestStore, renderWithProviders, type TestStore } from '../../test/renderWithProviders';
import { apiSlice } from '../api/apiSlice';
import { forgetDemoCopy, writeDemoCopy } from './demoCopyStorage';
import { DemoCopyPanel } from './DemoCopyPanel';

/*
  The dashboard's panel about the demo copy: what it tells the copy's owner, what it tells anyone
  else who is signed in to the copy, and what it keeps from them.

  The owner is whoever is signed in on the browser that keeps the copy's sign-in details. Every
  test starts signed in to the first copy of the mock's pool (src/mocks/state.ts); what the browser
  keeps is each test's own. The addresses, the passwords and the handles typed out below are the
  pool's: fixtures no server knows.

  The words are typed out here and not imported from the product, so a test fails the day the words
  on screen are no longer these. The apostrophe is ASCII; the dot between the day and the hour, and
  between the address and the password, is U+00B7.
*/
const WORDS = {
  heading: 'Your private copy',
  notTheOwner:
    'This is a private demo copy. It works for 24 hours from its first use, then it is closed and deleted.',
  pin: 'PIN: 123456, unless you changed it',
  show: 'Show sign-in details',
  hide: 'Hide sign-in details',
  startOver: 'Start over',
  startOverTitle: 'Start over with a new copy?',
  keepThisCopy: 'Keep this copy',
} as const;

/** "Other visitors can't see this copy. It works until January 5, 2026 · 9:15 AM, then …" */
const OWNER =
  /^Other visitors can't see this copy\. It works until ([A-Z][a-z]+ \d{1,2}, \d{4}) · (\d{1,2}:\d{2} (?:AM|PM)), then it is closed and deleted\.$/;

const CLAIM = '*/bff/auth/demo/claim';
const KEY = 'azurebank.demoCopy';
/** The first copy of the mock's pool: its address, what signs in to it, whom it can pay. */
const FIRST_COPY = 'demo-k7m2x9q4w8e1r5t3@azurebank.example';
const FIRST_PASSWORD = 'Xk7p-Rm3w-Hn8d-Tq5v';
const FIRST_CONTACTS = 'Contacts you can pay: @jane_k7m2 and @mike_k7m2';
/** The pool's second copy, which nobody in these tests is signed in to. */
const SECOND_COPY = 'demo-4h9d2s7f1g6j3k8a@azurebank.example';
const SECOND_PASSWORD = 'Fb4t-Wy9c-Kz2g-Ne6s';
/**
 * A copy's end that is a whole minute in the middle of July, far from any day a zone changes its
 * clocks, and years ahead: the day and the hour it is printed as name one instant in whichever
 * zone the suite runs.
 */
const A_FAR_END = '2031-07-15T12:30:00.000Z';
/** What a stored value must never become on the page: an element. */
const MARKUP = '<img src=x onerror=alert(1)>';

type KeptCopy = Parameters<typeof rememberDemoCopy>[0];

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  // A test that made storage refuse a call puts it back itself; one that failed before that line
  // would hand its refusal to every test after it.
  vi.restoreAllMocks();
});

/**
 * The panel beside the toasts' outlet, on a page whose card is the class it is handed. The box
 * around it is the test's: what the panel draws is what that box holds, and nothing else is in it.
 */
function PanelOnAPage() {
  return (
    <>
      <AppToaster />
      <div data-testid="where-the-panel-goes">
        <DemoCopyPanel className="the-pages-card" />
      </div>
    </>
  );
}

/** Everything the panel draws, as markup: `''` when it draws nothing at all. */
const drawn = () => screen.getByTestId('where-the-panel-goes').innerHTML;

/** Tells the store who is signed in, as the app's first question would have (`GET /bff/auth/me`). */
function signIn(store: TestStore, user: { email: string }) {
  store.dispatch({
    type: 'api/executeQuery/fulfilled',
    meta: { arg: { endpointName: 'getMe' } },
    payload: { user: { ...user } },
  });
}

/** A store of its own for a test, with that user signed in. */
function signedInStore(user: { email: string }): TestStore {
  const store = makeTestStore();
  signIn(store, user);
  return store;
}

/**
 * The panel, on the demo, for a visitor signed in to the first copy of the mock's pool.
 *
 * `kept` is what the browser keeps. Left out, it is that copy as its claim left it, with an end
 * that can be read back from the page; an object changes members of it; `null` is a browser that
 * keeps nothing. `demo: false` leaves the tag off the page.
 */
function renderPanel({
  demo = true,
  kept,
}: { demo?: boolean; kept?: Partial<KeptCopy> | null } = {}) {
  if (demo) enableDemoMode();
  const claimed = seedMockDemoCopy();
  const copy = kept === null ? null : { ...claimed.copy, expiresAt: A_FAR_END, ...kept };
  if (copy) rememberDemoCopy(copy);
  const store = signedInStore(claimed.user);
  const claims = { sent: 0 };
  server.use(
    http.post(CLAIM, () => {
      claims.sent += 1;
    }),
  );
  const view = renderWithProviders(<PanelOnAPage />, { store });
  return {
    ...view,
    user: claimed.user,
    copy,
    claims,
    /** Every claim the store has sent has its answer: a count read after this would have moved. */
    settled: () =>
      act(async () => {
        await Promise.all(store.dispatch(apiSlice.util.getRunningMutationsThunk()));
      }),
  };
}

/** Every panel on the page: the region its heading names. None is `[]`, not an error. */
const panels = () => screen.queryAllByRole('region', { name: WORDS.heading });

/** What the panel says, top to bottom: its headings, its lines, its buttons. `null` for no panel. */
function said() {
  const panel = panels()[0];
  if (!panel) return null;
  return {
    headings: within(panel)
      .queryAllByRole('heading', { level: 2 })
      .map((heading) => heading.textContent),
    lines: Array.from(panel.querySelectorAll('p')).map((line) => line.textContent),
    buttons: within(panel)
      .queryAllByRole('button')
      .map((button) => button.textContent),
  };
}

/** The panel's buttons of that name. With the dialog open the page has a second "Start over". */
function panelButtons(name: string): HTMLButtonElement[] {
  const panel = panels()[0];
  return panel ? (within(panel).queryAllByRole('button', { name }) as HTMLButtonElement[]) : [];
}

/** The details toggle, whichever of its two names it has. */
const toggles = () => [...panelButtons(WORDS.show), ...panelButtons(WORDS.hide)];

/**
 * The toggle as the page has it: its name, its state, whether it says which element it shows and
 * hides (`aria-controls`), whether an element of that id is in the page, and what that element
 * reads.
 */
function toggleNow() {
  return toggles().map((toggle) => {
    const controls = toggle.getAttribute('aria-controls');
    const controlled = controls ? document.getElementById(controls) : null;
    return {
      name: toggle.textContent,
      expanded: toggle.getAttribute('aria-expanded'),
      hasAriaControls: controls !== null && controls !== '',
      controlledInThePage: controlled !== null,
      controlled: controlled?.textContent ?? null,
    };
  });
}

/** The instant the owner's line prints, read back from its words; `null` when it prints none. */
function printedEnd(): string | null {
  const printed = said()
    ?.lines.map((line) => OWNER.exec(line ?? ''))
    .find((match) => match !== null);
  return printed ? new Date(`${printed[1]} ${printed[2]}`).toISOString() : null;
}

/** Whether the page holds these words anywhere: in what it shows, in an attribute, in a script. */
const pageHolds = (words: string) => document.documentElement.outerHTML.includes(words);

/** Every confirm dialog in the page, open or closed: a closed one is there, hidden. */
const dialogsInThePage = () => document.querySelectorAll('[role="alertdialog"]').length;

/** The title of each dialog that is open. */
const openDialogs = () =>
  screen
    .queryAllByRole('alertdialog')
    .map((dialog) => within(dialog).queryByRole('heading')?.textContent);

/** Where focus is: the focused control by its name, and whether the panel holds it. */
function focus() {
  const active = document.activeElement;
  if (active === null || active === document.body) return { on: 'the page', inThePanel: false };
  return {
    on:
      active.getAttribute('role') === 'alertdialog'
        ? 'the dialog itself'
        : (active.getAttribute('aria-label') ?? active.textContent),
    inThePanel: panels()[0]?.contains(active) ?? false,
  };
}

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

describe("the dashboard's panel about the demo copy", () => {
  it('renders nothing without the tag', () => {
    // Off the demo, with everything else an owner has: signed in, and the copy kept.
    const off = renderPanel({ demo: false });
    const withoutTheTag = { drawn: drawn(), dialogs: dialogsInThePage() };
    off.unmount();

    // The same visitor and the same browser, on a page that says it is the demo.
    enableDemoMode();
    renderWithProviders(<PanelOnAPage />, { store: off.store });

    expect({ withoutTheTag, withTheTag: said()?.headings }).toStrictEqual({
      withoutTheTag: { drawn: '', dialogs: 0 },
      withTheTag: [WORDS.heading],
    });
  });

  it('draws nothing while nobody is signed in', () => {
    enableDemoMode();
    rememberDemoCopy({ ...seedMockDemoCopy().copy, expiresAt: A_FAR_END });
    const store = makeTestStore();
    renderWithProviders(<PanelOnAPage />, { store });
    const signedOut = { drawn: drawn(), dialogs: dialogsInThePage() };

    act(() => signIn(store, { email: FIRST_COPY }));

    expect({ signedOut, signedIn: said()?.headings }).toStrictEqual({
      signedOut: { drawn: '', dialogs: 0 },
      signedIn: [WORDS.heading],
    });
  });

  it("the owner reads the copy's end, its PIN and its contacts", () => {
    const { copy } = renderPanel();
    const panel = panels()[0];
    const heading = panel?.querySelector('h2');
    const lines = Array.from(panel?.querySelectorAll('p') ?? []);

    expect({
      panels: panels().length,
      // A section, named by its heading: a landmark a screen reader can go to.
      element: panel?.tagName,
      wearsThePagesCard: panel?.classList.contains('the-pages-card'),
      // What the heading's own class declares its box as. Fluent's `Text` draws inline whatever
      // element it is asked for, a heading too.
      headingDrawnAs: heading ? getComputedStyle(heading).display : null,
      said: said(),
      // The instant printed is the kept copy's end, in the zone the suite runs in: read back from
      // the words, and never typed out as an hour.
      until: printedEnd(),
      order: inOrder(
        panel?.querySelector('h2'),
        ...lines,
        toggles()[0],
        panelButtons(WORDS.startOver)[0],
      ),
    }).toStrictEqual({
      panels: 1,
      element: 'SECTION',
      wearsThePagesCard: true,
      headingDrawnAs: 'block',
      said: {
        headings: [WORDS.heading],
        lines: [expect.stringMatching(OWNER), WORDS.pin, FIRST_CONTACTS],
        buttons: [WORDS.show, WORDS.startOver],
      },
      until: copy?.expiresAt,
      order: true,
    });
  });

  it('the sign-in details are behind a toggle that says its state', async () => {
    renderPanel();
    const details = `Email: ${FIRST_COPY} · Password: ${FIRST_PASSWORD}`;
    const look = () => ({
      toggle: toggleNow(),
      lines: said()?.lines.length,
      passwordOnThePage: screen.queryAllByText(FIRST_PASSWORD, { exact: false }).length,
    });

    const closed = look();
    await userEvent.click(toggles()[0]);
    const shown = screen.queryAllByText(details)[0];
    const open = {
      ...look(),
      order: inOrder(toggles()[0], shown),
      breaks: shown ? getComputedStyle(shown).overflowWrap : null,
    };
    await userEvent.click(toggles()[0]);
    const closedAgain = look();

    // Closed, the toggle still carries the id, and no element of that id is in the page: the
    // details are in the page only while they are shown.
    const shut = {
      toggle: [
        {
          name: WORDS.show,
          expanded: 'false',
          hasAriaControls: true,
          controlledInThePage: false,
          controlled: null,
        },
      ],
      lines: 3,
      passwordOnThePage: 0,
    };
    expect({ closed, open, closedAgain }).toStrictEqual({
      closed: shut,
      open: {
        toggle: [
          {
            name: WORDS.hide,
            expanded: 'true',
            hasAriaControls: true,
            controlledInThePage: true,
            controlled: details,
          },
        ],
        lines: 4,
        passwordOnThePage: 1,
        // Under the button that showed them, so they are read next.
        order: true,
        // An address and a password have no space in them: the line is declared to break inside
        // a word where it must.
        breaks: 'anywhere',
      },
      closedAgain: shut,
    });
  });

  it('someone signed in from another browser reads what the copy is, and nothing of its sign-in', () => {
    // Signed in to the copy, on a browser that keeps nothing: its email and password were typed.
    renderPanel({ kept: null });

    expect({
      said: said(),
      dialogs: dialogsInThePage(),
      passwordOnThePage: pageHolds(FIRST_PASSWORD),
    }).toStrictEqual({
      said: { headings: [WORDS.heading], lines: [WORDS.notTheOwner, WORDS.pin], buttons: [] },
      dialogs: 0,
      passwordOnThePage: false,
    });
  });

  it('a stored copy for another address makes no owner', () => {
    // The browser keeps a copy, and it is not the one this visitor is signed in to.
    renderPanel({
      kept: {
        email: SECOND_COPY,
        password: SECOND_PASSWORD,
        contacts: ['jane_p3x8', 'mike_p3x8'],
      },
    });

    expect({
      kept: (JSON.parse(localStorage.getItem(KEY) ?? '{}') as { email?: string }).email,
      said: said(),
      dialogs: dialogsInThePage(),
      // Nothing of the copy the browser keeps is on the page of somebody else's.
      ofTheKeptCopy: [SECOND_COPY, SECOND_PASSWORD, 'jane_p3x8', 'mike_p3x8'].filter(pageHolds),
    }).toStrictEqual({
      kept: SECOND_COPY,
      said: { headings: [WORDS.heading], lines: [WORDS.notTheOwner, WORDS.pin], buttons: [] },
      dialogs: 0,
      ofTheKeptCopy: [],
    });
  });

  it('a browser that refuses to store: the owner still sees the copy while the page lives', async () => {
    enableDemoMode();
    // The mock's claim counts a copy's 24 hours from now: with the date held at a whole minute,
    // the copy it hands out ends at one too, and the page's words can be read back to it.
    vi.useFakeTimers({ toFake: ['Date'], now: new Date('2031-07-14T12:30:00.000Z') });
    const denied = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('SecurityError');
    });
    try {
      // A store nobody is signed in to, until the claim is answered.
      const { store } = renderWithProviders(<PanelOnAPage />);
      const beforeTheClaim = drawn();

      const claimed = await act(() =>
        store.dispatch(apiSlice.endpoints.claimDemoCopy.initiate()).unwrap(),
      );

      expect({
        beforeTheClaim,
        refusedWrites: denied.mock.calls.filter(([key]) => key === KEY).length,
        key: localStorage.getItem(KEY),
        claimedUntil: claimed.copy.expiresAt,
        said: said(),
        until: printedEnd(),
      }).toStrictEqual({
        beforeTheClaim: '',
        refusedWrites: 1,
        key: null,
        claimedUntil: A_FAR_END,
        said: {
          headings: [WORDS.heading],
          lines: [expect.stringMatching(OWNER), WORDS.pin, FIRST_CONTACTS],
          buttons: [WORDS.show, WORDS.startOver],
        },
        until: A_FAR_END,
      });
    } finally {
      denied.mockRestore();
    }
  });

  it('"Start over" opens the one dialog, and closing it gives focus back', async () => {
    const { claims, settled } = renderPanel();
    const keyBefore = localStorage.getItem(KEY);
    // A browser hands a disabled button's focus to the page; jsdom does not (src/test/outage.ts).
    // Nothing here waits, so it changes nothing: it is on so that focus is read as a browser
    // would leave it.
    const stopEmulating = emulateFocusFixup();
    try {
      // Kept in the page, closed, from the first render: one dialog, beside the panel.
      const atRest = { inThePage: dialogsInThePage(), open: openDialogs() };

      await userEvent.click(panelButtons(WORDS.startOver)[0]);
      await waitFor(() => expect(openDialogs()).toStrictEqual([WORDS.startOverTitle]));
      // Asked for by its name: with the dialog open the page has two level-2 headings.
      const dialog = screen.getByRole('alertdialog', { name: WORDS.startOverTitle });
      const open = {
        levelTwoHeadings: screen
          .getAllByRole('heading', { level: 2 })
          .map((heading) => heading.textContent),
        // "Start over" is the dialog's confirm too, and the first words of its title: the
        // confirm is the one inside the dialog.
        startOverOnThePage: screen.queryAllByRole('button', { name: WORDS.startOver }).length,
        confirmsInTheDialog: within(dialog).queryAllByRole('button', { name: WORDS.startOver })
          .length,
        dialogInThePanel: panels()[0]?.contains(dialog),
        // Focus moved off the button when the dialog opened, so focus on it afterwards is a
        // return, not a button that never lost it. Here it moves whatever the dialog's styles
        // say. That a browser moves it too is asserted in one, for the component under this
        // dialog (e2e/confirmDialog.spec.ts).
        focusLeftTheButton: document.activeElement !== panelButtons(WORDS.startOver)[0],
      };

      await userEvent.click(within(dialog).getByRole('button', { name: WORDS.keepThisCopy }));
      const afterKeepThisCopy = { dialogs: openDialogs(), focus: focus() };

      await userEvent.click(panelButtons(WORDS.startOver)[0]);
      await waitFor(() => expect(openDialogs()).toStrictEqual([WORDS.startOverTitle]));
      await userEvent.keyboard('{Escape}');
      const afterEscape = { dialogs: openDialogs(), focus: focus() };
      await settled();

      expect({
        atRest,
        open,
        afterKeepThisCopy,
        afterEscape,
        claims: claims.sent,
        keyUnchanged: localStorage.getItem(KEY) === keyBefore,
      }).toStrictEqual({
        atRest: { inThePage: 1, open: [] },
        open: {
          levelTwoHeadings: [WORDS.heading, WORDS.startOverTitle],
          startOverOnThePage: 2,
          confirmsInTheDialog: 1,
          dialogInThePanel: false,
          focusLeftTheButton: true,
        },
        afterKeepThisCopy: { dialogs: [], focus: { on: WORDS.startOver, inThePanel: true } },
        afterEscape: { dialogs: [], focus: { on: WORDS.startOver, inThePanel: true } },
        claims: 0,
        keyUnchanged: true,
      });
    } finally {
      stopEmulating();
    }
  });

  it('a stored copy is shown as text, whatever it holds', async () => {
    // A key anything on the page's origin can write to: what it holds is drawn as it was typed.
    renderPanel({ kept: { password: MARKUP, contacts: [MARKUP, MARKUP] } });

    const closed = { lines: said()?.lines, images: document.querySelectorAll('img').length };
    await userEvent.click(toggles()[0]);

    expect({
      closed,
      open: { lines: said()?.lines, images: document.querySelectorAll('img').length },
    }).toStrictEqual({
      closed: {
        lines: [
          expect.stringMatching(OWNER),
          WORDS.pin,
          `Contacts you can pay: @${MARKUP} and @${MARKUP}`,
        ],
        images: 0,
      },
      open: {
        lines: [
          expect.stringMatching(OWNER),
          WORDS.pin,
          `Contacts you can pay: @${MARKUP} and @${MARKUP}`,
          `Email: ${FIRST_COPY} · Password: ${MARKUP}`,
        ],
        images: 0,
      },
    });
  });

  it('the contacts line follows what is stored', () => {
    const three = renderPanel({ kept: { contacts: ['jane_k7m2', 'mike_k7m2', 'anna_k7m2'] } });
    const withThree = said()?.lines;
    three.unmount();

    // The same owner, on a browser whose kept copy names nobody to pay.
    rememberDemoCopy({
      email: FIRST_COPY,
      password: FIRST_PASSWORD,
      pin: '123456',
      contacts: [],
      expiresAt: A_FAR_END,
    });
    renderWithProviders(<PanelOnAPage />, { store: three.store });

    expect({ withThree, withNone: said() }).toStrictEqual({
      withThree: [
        expect.stringMatching(OWNER),
        WORDS.pin,
        'Contacts you can pay: @jane_k7m2 and @mike_k7m2 and @anna_k7m2',
      ],
      // Still the owner's panel, with one line fewer.
      withNone: {
        headings: [WORDS.heading],
        lines: [expect.stringMatching(OWNER), WORDS.pin],
        buttons: [WORDS.show, WORDS.startOver],
      },
    });
  });

  it("a copy another tab forgot: at the next render the panel is nobody's, and the sign-in details are gone", async () => {
    const { rerender } = renderPanel();
    await userEvent.click(toggles()[0]);
    const before = { lines: said()?.lines.length, passwordOnThePage: pageHolds(FIRST_PASSWORD) };

    // Another tab's "Forget this copy": the key goes, and nothing on this page is told.
    localStorage.removeItem(KEY);
    rerender(<PanelOnAPage />);

    expect({
      before,
      after: {
        said: said(),
        dialogs: dialogsInThePage(),
        passwordOnThePage: pageHolds(FIRST_PASSWORD),
      },
    }).toStrictEqual({
      before: { lines: 4, passwordOnThePage: true },
      after: {
        said: { headings: [WORDS.heading], lines: [WORDS.notTheOwner, WORDS.pin], buttons: [] },
        dialogs: 0,
        passwordOnThePage: false,
      },
    });
  });

  it('told that the copy was kept or forgotten on this page, the panel follows at once', () => {
    // Signed in to the copy, on a browser that keeps nothing of it yet.
    const { user } = renderPanel({ kept: null });
    const withNoneKept = said()?.lines;

    // Through the storage module, as the product keeps and forgets a copy: it tells whoever
    // listens, and nothing else draws the panel again.
    act(() =>
      writeDemoCopy({
        email: user.email,
        password: FIRST_PASSWORD,
        pin: '123456',
        contacts: ['jane_k7m2', 'mike_k7m2'],
        expiresAt: A_FAR_END,
      }),
    );
    const onceKept = said()?.lines;
    act(() => forgetDemoCopy());

    expect({ withNoneKept, onceKept, onceForgotten: said()?.lines }).toStrictEqual({
      withNoneKept: [WORDS.notTheOwner, WORDS.pin],
      onceKept: [expect.stringMatching(OWNER), WORDS.pin, FIRST_CONTACTS],
      onceForgotten: [WORDS.notTheOwner, WORDS.pin],
    });
  });

  it("the PIN line is the demo's, whatever PIN the browser keeps", () => {
    // A kept copy whose PIN is not the demo's: the line is no reading of the key.
    renderPanel({ kept: { pin: '987654' } });

    expect({ lines: said()?.lines, keptPinOnThePage: pageHolds('987654') }).toStrictEqual({
      lines: [expect.stringMatching(OWNER), WORDS.pin, FIRST_CONTACTS],
      keptPinOnThePage: false,
    });
  });

  it("a kept copy past its end is still its owner's here, and is not removed", () => {
    // Signed in, so the copy lives: a copy's end is looked at where a copy is offered, on the
    // sign-in page, and whoever is signed in keeps the copy while the session lasts.
    renderPanel();
    const ahead = { until: printedEnd(), buttons: said()?.buttons };
    cleanup();

    vi.useFakeTimers({ toFake: ['Date'], now: new Date('2031-07-16T12:30:00.000Z') });
    renderWithProviders(<PanelOnAPage />, { store: signedInStore({ email: FIRST_COPY }) });

    expect({
      ahead,
      past: {
        now: new Date().toISOString(),
        until: printedEnd(),
        buttons: said()?.buttons,
        kept: (JSON.parse(localStorage.getItem(KEY) ?? '{}') as { email?: string }).email,
      },
    }).toStrictEqual({
      ahead: { until: A_FAR_END, buttons: [WORDS.show, WORDS.startOver] },
      past: {
        now: '2031-07-16T12:30:00.000Z',
        until: A_FAR_END,
        buttons: [WORDS.show, WORDS.startOver],
        kept: FIRST_COPY,
      },
    });
  });

  it('the panel is no live region', async () => {
    renderPanel();
    // With the details open: everything the panel can show is on the page.
    await userEvent.click(toggles()[0]);
    const panel = panels()[0];

    expectNoNestedLiveRegions();
    expect({
      panels: panels().length,
      lines: said()?.lines.length,
      liveInThePanel: panel?.querySelectorAll('[role="status"], [role="alert"], [aria-live]')
        .length,
      panelInALiveRegion: panel?.closest('[role="status"], [role="alert"], [aria-live]') !== null,
    }).toStrictEqual({ panels: 1, lines: 4, liveInThePanel: 0, panelInALiveRegion: false });
  });
});
