import { act, cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '../mocks/server';
import { problem } from '../mocks/problem';
import { MOCK_USER, seedMockSession } from '../mocks/state';
import { makeTestStore, renderWithProviders } from '../test/renderWithProviders';
import {
  COPY,
  advanceUntil,
  emulateFocusFixup,
  expectSilentHintTakesNoRoom,
  fakeClockUser,
  hintRegion,
  hintShownAt,
  installFakeClock,
  never,
} from '../test/outage';
import { AppToaster } from '../components/feedback';
import { apiSlice } from '../features/api/apiSlice';
import { SettingsPage } from './SettingsPage';

// Seed the auth slice directly (the wire shape authSlice keys on), so there is no live getMe
// subscription re-rendering the page mid-interaction. seedMockSession() keeps the MSW /me + rename
// handlers consistent for the explicit refetch the success case performs.
function seededStore() {
  seedMockSession();
  const store = makeTestStore();
  store.dispatch({
    type: 'api/executeQuery/fulfilled',
    meta: { arg: { endpointName: 'getMe' } },
    payload: { user: { ...MOCK_USER } },
  });
  return store;
}

async function renderSettings() {
  const store = seededStore();
  const utils = renderWithProviders(<SettingsPage />, { store, routerEntries: ['/settings'] });
  await screen.findByText(`${MOCK_USER.firstName} ${MOCK_USER.lastName}`);
  return utils; // renderWithProviders returns the passed store
}

describe('settings page', () => {
  it('shows the session identity + @handle, and none of the fabricated PII', async () => {
    await renderSettings();
    const initials =
      `${MOCK_USER.firstName.charAt(0)}${MOCK_USER.lastName.charAt(0)}`.toUpperCase();

    expect(screen.getByText(`${MOCK_USER.firstName} ${MOCK_USER.lastName}`)).toBeInTheDocument();
    expect(screen.getByText(MOCK_USER.email)).toBeInTheDocument();
    expect(screen.getByText(initials)).toBeInTheDocument();
    expect(screen.getByText(`@${MOCK_USER.azureTag}`)).toBeInTheDocument();

    // The old mockUser fiction must be gone.
    expect(screen.queryByText(/\+1 \(555\)/)).not.toBeInTheDocument();
    expect(screen.queryByText(/United States/)).not.toBeInTheDocument();
    expect(screen.queryByText(/March 15, 1990/)).not.toBeInTheDocument();
  });

  it('says the name and the email once, and draws the handle as one row with its Change', async () => {
    await renderSettings();
    const profile = within(screen.getByRole('region', { name: 'Profile' }));

    expect({
      boxes: ['First name', 'Last name', 'Email address'].filter(
        (label) => profile.queryByText(label) !== null,
      ),
      names: profile.getAllByText(`${MOCK_USER.firstName} ${MOCK_USER.lastName}`).length,
      emails: profile.getAllByText(MOCK_USER.email).length,
      terms: profile.getAllByRole('term').map((term) => term.textContent),
      definitions: profile.getAllByRole('definition').map((value) => value.textContent),
      buttons: profile.getAllByRole('button').length,
    }).toStrictEqual({
      boxes: [],
      names: 1,
      emails: 1,
      terms: ['Public handle'],
      definitions: [`@${MOCK_USER.azureTag}`, 'How other people find you to send money.', 'Change'],
      buttons: 1,
    });
    // WCAG 2.5.3: the word a visitor sees is the start of the name a voice command is matched to.
    expect(profile.getByRole('button', { name: 'Change public handle' })).toHaveTextContent(
      /^Change$/,
    );
  });

  it("titles each card with a level-2 heading under the page's <h1>, and names the card so", async () => {
    await renderSettings();
    const cards = ['Profile', 'Security', 'Appearance', 'More settings', 'Session'];

    expect({
      tree: screen
        .getAllByRole('heading')
        .map((heading) => `${heading.tagName}:${heading.textContent}`),
      regions: cards.map(
        (name) =>
          within(screen.getByRole('region', { name })).getByRole('heading', { level: 2 })
            .textContent,
      ),
    }).toStrictEqual({
      tree: ['H1:Settings', ...cards.map((card) => `H2:${card}`)],
      regions: cards,
    });
  });

  it('offers Sign out in the Session card as a plain secondary button, with no inline style', async () => {
    await renderSettings();
    const session = screen.getByRole('region', { name: 'Session' });
    const signOut = within(session).getByRole('button', { name: 'Sign out' });
    within(session).getByText('End your session in this browser.');

    expect({
      inlineStyle: signOut.getAttribute('style'),
      danger: screen.queryByRole('region', { name: 'Danger zone' }),
      logOut: screen.queryByText(/log out/i),
    }).toStrictEqual({
      inlineStyle: null,
      danger: null,
      logOut: null,
    });
  });

  it('says beside the PIN everything it confirms', async () => {
    await renderSettings();
    expect(
      screen.getByText(
        'Used to confirm withdrawals, transfers and account closures, and to show full account numbers.',
      ),
    ).toBeInTheDocument();
  });

  it('lists future settings as honest "Coming soon" rows', async () => {
    await renderSettings();
    expect(screen.getByText('Password & two-factor')).toBeInTheDocument();
    expect(screen.getAllByText('Coming soon').length).toBeGreaterThanOrEqual(4);
    // Dark mode used to be one of them. The row is gone because the feature arrived (U7), and this
    // asserts the promise was REDEEMED rather than merely deleted — the control below is real.
    expect(screen.queryByText('Dark mode')).not.toBeInTheDocument();
    expect(screen.getByRole('radiogroup', { name: 'Theme' })).toBeInTheDocument();
  });

  it('renames the public handle; a getMe refetch reflects the new one', async () => {
    const user = userEvent.setup();
    const { store } = await renderSettings();

    expect(screen.getByText(`@${MOCK_USER.azureTag}`)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Change public handle' }));

    const input = await screen.findByRole('textbox');
    /*
      Focus it first, as a user must: fireEvent.change alone edits a field nothing has focused, and
      an open Fluent dialog with nothing focused inside is what tabster aria-hides (test/layout.ts).
      Measured 2026-09-11, full suite under eight spinning cores: without the click this test failed
      5 of 6 runs, four of them at the Save query below; with it, 0 of 6. The fifth failure was at
      the textbox query above, before any click, which this cannot reach.

      "Nothing focused inside" was half of it. The input IS focused when the dialog opens, and
      tabster moves focus to "Sign out" 100ms later because keyborg cannot mark that focus as
      programmatic (test/keyborg.ts). With this click in place, eight busy cores on main 9dc371d
      still failed this file 2 of 10 runs.
    */
    await user.click(input);
    fireEvent.change(input, { target: { value: 'newtag' } });
    await user.click(await screen.findByRole('button', { name: 'Save' }));

    // On success the dialog closes; the app propagates the new tag via the Session invalidation.
    // Here we drive that refetch explicitly (no live subscription) — the MSW handler updated the
    // session, so /me now returns the new handle.
    await waitFor(() => expect(screen.queryByRole('textbox')).not.toBeInTheDocument());
    await store.dispatch(apiSlice.endpoints.getMe.initiate(undefined, { forceRefetch: true }));

    expect(store.getState().auth.user?.azureTag).toBe('newtag');
    expect(await screen.findByText('@newtag')).toBeInTheDocument();
  });

  it('surfaces a taken handle and keeps the old one', async () => {
    const user = userEvent.setup();
    await renderSettings();

    await user.click(screen.getByRole('button', { name: 'Change public handle' }));
    const input = await screen.findByRole('textbox');
    await user.click(input); // focus first, as above
    fireEvent.change(input, { target: { value: 'friend' } }); // a seeded recipient → 409
    await user.click(await screen.findByRole('button', { name: 'Save' }));

    expect(await screen.findByText('That handle is already taken.')).toBeInTheDocument();
    expect(screen.getByText(`@${MOCK_USER.azureTag}`)).toBeInTheDocument(); // handle unchanged
  });

  /*
    U7 — the toggle is the whole point of the section, so what is asserted is the EFFECT on the
    document, not that a radio became checked. A control that changes its own state and nothing else
    is exactly the failure mode the "Coming soon" badge was honest about.
  */
  describe('appearance', () => {
    afterEach(() => {
      localStorage.clear();
      document.documentElement.removeAttribute('data-theme');
    });

    it('switches the document to dark and remembers the choice', async () => {
      const user = userEvent.setup();
      await renderSettings();

      await user.click(screen.getByRole('radio', { name: 'Dark' }));

      expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
      // Persisted as the PREFERENCE, not as the resolved theme: "dark" chosen explicitly and "dark"
      // because the machine is dark are different states, and only the first should survive the
      // machine changing its mind.
      expect(localStorage.getItem('azurebank.theme')).toBe('dark');
    });

    it('the document agrees with React on load, even when nothing set the attribute', async () => {
      // The handoff from `public/theme-init.js` to React, and the three ways it can be missed: the
      // script 404s behind a misconfigured static host, ANOTHER TAB writes the preference between
      // the head script and this mount, or — as here, and in every test — no script ran at all.
      // Fluent would take the stored preference while <html> kept whatever the document had, so the
      // custom properties and the component library would render two different themes at once.
      localStorage.setItem('azurebank.theme', 'dark');

      await renderSettings();

      expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
      expect(screen.getByRole('radio', { name: 'Dark' })).toBeChecked();
    });

    it('going back to System hands the decision to the device', async () => {
      const user = userEvent.setup();
      await renderSettings();

      await user.click(screen.getByRole('radio', { name: 'Dark' }));
      await user.click(screen.getByRole('radio', { name: 'System' }));

      expect(localStorage.getItem('azurebank.theme')).toBe('system');
      // jsdom reports no `matchMedia`, which `systemPrefersDark` treats as "not dark" — the same
      // conservative branch a browser without the query would take.
      expect(document.documentElement.getAttribute('data-theme')).toBe('light');
    });
  });
});

/*
  The handle dialog, past the two answers above. A handle the form accepts and the server
  refuses comes back as a 400 with an `errors` dictionary keyed `AzureTag`, in PascalCase: the
  one shape this route can emit (measured 2026-08-04, quoted in
  src/components/dialogs/server-validation.test.tsx). The rule below is made up, in that
  envelope: what is held is where its sentence lands.
*/
describe('settings page: the handle dialog and what the server refuses', () => {
  /** Every rename sent from now on, as its body; the handler that answers is left alone. */
  function watchRenames() {
    const bodies: unknown[] = [];
    server.use(
      http.patch('*/bff/auth/azuretag', async ({ request }) => {
        bodies.push(await request.clone().json());
      }),
    );
    return bodies;
  }

  async function openAndType(user: ReturnType<typeof userEvent.setup>, handle: string) {
    await user.click(screen.getByRole('button', { name: 'Change public handle' }));
    const input = await screen.findByRole('textbox');
    await user.click(input); // focus first, as above
    fireEvent.change(input, { target: { value: handle } });
    return input;
  }

  it("puts the server's rule about the handle under the field, and says it once", async () => {
    server.use(
      http.patch('*/bff/auth/azuretag', () =>
        problem({ status: 400, errors: { AzureTag: ['This handle is reserved.'] } }),
      ),
    );
    const user = userEvent.setup();
    await renderSettings();
    await openAndType(user, 'support');
    await user.click(await screen.findByRole('button', { name: 'Save' }));

    expect(await screen.findByText('This handle is reserved.')).toBeVisible();
    // On the field, and in no bar above it: the envelope's own "One or more validation errors
    // occurred." would say the same thing a second time, and less. One alert, the field's.
    expect(screen.getAllByRole('alert').map((alert) => alert.textContent)).toEqual([
      'This handle is reserved.',
    ]);
    expect(screen.queryByText(/One or more validation errors occurred/)).not.toBeInTheDocument();
    // The dialog is still open on what was typed, and the handle is unchanged.
    expect(screen.getByRole('textbox')).toBeInTheDocument();
    expect(screen.getByText(`@${MOCK_USER.azureTag}`)).toBeInTheDocument();
  });

  it('says a refusal that is not about the handle in the bar, where it is announced', async () => {
    server.use(
      http.patch('*/bff/auth/azuretag', () =>
        problem({ status: 400, errors: { Request: ['The request could not be read.'] } }),
      ),
    );
    const user = userEvent.setup();
    await renderSettings();
    await openAndType(user, 'newtag');
    await user.click(await screen.findByRole('button', { name: 'Save' }));

    // No field of this form owns it, so the visitor is not left looking at a form that did nothing.
    expect(await screen.findByRole('alert')).toHaveTextContent(
      'One or more validation errors occurred.',
    );
    expect(screen.getByRole('textbox')).toBeInTheDocument();
  });

  it('Escape closes the dialog and saves nothing', async () => {
    const renames = watchRenames();
    const user = userEvent.setup();
    await renderSettings();
    await openAndType(user, 'newtag');

    await user.keyboard('{Escape}');

    await waitFor(() => expect(screen.queryByRole('textbox')).not.toBeInTheDocument());
    expect(renames).toEqual([]);
    expect(screen.getByText(`@${MOCK_USER.azureTag}`)).toBeInTheDocument();
  });
});

/*
  Sign out on the Session card while the server has not answered. The shell's two Sign out
  controls are held in layout.test.tsx; this one keeps a pending state of its own, the page's.
*/
describe('settings page: Sign out while its answer is awaited', () => {
  // Unmount first, then restore the clock: a hint still mounted on a fake clock would go on
  // ticking into the next test.
  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  const signOutButton = () =>
    within(screen.getByRole('region', { name: 'Session' })).getByRole('button', {
      name: 'Sign out',
    });

  it('asks the server once however often it is pressed, and keeps its focus', async () => {
    let answer: () => void = () => {};
    let asked = 0;
    server.use(
      http.post('*/bff/auth/logout', async () => {
        asked += 1;
        await new Promise<void>((resolve) => {
          answer = resolve;
        });
        return HttpResponse.json({ message: 'Logged out successfully' });
      }),
    );
    const { router } = await renderSettings();
    const user = userEvent.setup();
    const signOut = signOutButton();
    // A browser hands a disabled button's focus to the page; jsdom does not (src/test/outage.ts).
    const stopEmulating = emulateFocusFixup();
    try {
      await user.click(signOut);
      await waitFor(() => expect(signOut).toHaveAttribute('aria-busy', 'true'));
      expect(signOut).toHaveAttribute('aria-disabled', 'true');
      await waitFor(() => expect(asked).toBe(1));
      await user.click(signOut);
      await user.click(signOut);

      expect(asked).toBe(1);
      expect(signOut).toHaveFocus();
      act(() => answer());
      await waitFor(() => {
        expect(router.state.location.pathname).toBe('/login');
        expect(signOutButton()).not.toHaveAttribute('aria-busy');
      });
      expect(asked).toBe(1);
    } finally {
      stopEmulating();
    }
  });

  it('says it is slow under the button from 5 s, and offers no way to stop it', async () => {
    server.use(http.post('*/bff/auth/logout', never));
    await renderSettings();
    const session = screen.getByRole('region', { name: 'Session' });
    const signOut = signOutButton();
    installFakeClock();
    const user = fakeClockUser();
    const pressedAt = Date.now();
    await user.click(signOut);
    const shownAt = await hintShownAt(session);
    // Silent, it holds no room under the button, so a quick sign-out moves nothing.
    expectSilentHintTakesNoRoom(session);

    await advanceUntil(pressedAt, 4_900);
    expect(within(session).queryByText(COPY.slow)).not.toBeInTheDocument();
    await advanceUntil(shownAt, 5_000);
    const region = hintRegion(COPY.slow, session);
    expect(signOut.compareDocumentPosition(region) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    await advanceUntil(shownAt, 20_000);
    hintRegion(COPY.stillTrying, session);
    expect(screen.queryByRole('button', { name: COPY.stopWaiting })).not.toBeInTheDocument();
  });

  it('after a sign-out that failed, it takes a press again and asks again', async () => {
    let asked = 0;
    server.use(
      http.post('*/bff/auth/logout', () => {
        asked += 1;
        return problem({ status: 503, title: 'Service Unavailable', detail: 'Try again shortly.' });
      }),
    );
    // The toast is how the test knows the refusal has come back.
    renderWithProviders(
      <>
        <AppToaster />
        <SettingsPage />
      </>,
      { store: seededStore(), routerEntries: ['/settings'] },
    );
    await screen.findByText(`${MOCK_USER.firstName} ${MOCK_USER.lastName}`);
    const user = userEvent.setup();
    const signOut = signOutButton();

    await user.click(signOut);
    // The title, matched whole: for half a second the toaster's live region holds all of the
    // toast's words as one text, which a pattern would find as well.
    await screen.findByText('Service Unavailable');
    await waitFor(() => {
      expect(signOut).not.toHaveAttribute('aria-busy');
      expect(signOut).not.toHaveAttribute('aria-disabled');
    });
    expect(asked).toBe(1);
    await user.click(signOut);

    await waitFor(() => {
      expect(asked).toBe(2);
      expect(screen.getAllByText('Service Unavailable')).toHaveLength(2);
    });
  });
});
