import { readFileSync } from 'node:fs';
import { Provider } from 'react-redux';
import {
  createMemoryRouter,
  createRoutesFromElements,
  Route,
  RouterProvider,
} from 'react-router-dom';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { RouteAnnouncer } from '../components/layout/RouteAnnouncer';
import { ShellOrBare } from '../components/layout/ShellOrBare';
import { apiSlice } from '../features/api/apiSlice';
import { serviceUnavailable } from '../mocks/problem';
import { server } from '../mocks/server';
import { mockState, seedMockSession } from '../mocks/state';
import { COPY } from '../test/outage';
import { makeTestStore, type TestStore } from '../test/renderWithProviders';
import { ThemeProvider } from '../theme/ThemeProvider';
import { ContactPage } from './ContactPage';
// Imported while the file is collected, as not-found.test.tsx does: the first import of App
// transforms every module of the app, and the test that opens the old address then only
// evaluates them again.
import '../App';

/**
 * The page where I say who I am and how to reach me, and where the app says what it is: a
 * portfolio project and not a bank. It is the one page a visitor reaches signed out as well as
 * signed in, on one route, and what differs between the two is held here: the way back to
 * sign-in, and the app's own navigation.
 *
 * The route is rendered as `App.tsx` renders it: inside `ShellOrBare`, which draws nothing of the
 * page until the session check has answered, so each store here has asked it first; and under
 * `RouteAnnouncer` with the route's title in its handle, on a data router of its own, because
 * `RouteAnnouncer` reads titles from the data router's matches and a descendant `<Routes>` is not
 * among them. The old address, `/about`, is opened in the whole app instead, through the route
 * table App.tsx declares and not a copy of it.
 *
 * The words are typed out here and not imported from the page, so a test fails the day the words
 * on screen are no longer these.
 */
const WORDS = {
  title: 'Contact',
  me: 'Vladislav Aleshaev',
  role: 'Full-stack engineer (.NET, React, Azure)',
  firstPerson:
    'I built AzureBank as my portfolio project. I designed the system, wrote part of the code myself and direct the AI coding agents that write the rest. I read the code I merge and I merge every pull request myself.',
  quickest: 'The quickest way to reach me is LinkedIn.',
  project: 'About this project',
  security:
    "Found a security problem? Please report it privately through GitHub's private vulnerability reporting.",
} as const;

/** Every link that leaves the app, in the order a visitor reads them. */
const OUTSIDE_LINKS = [
  { name: 'LinkedIn profile', href: 'https://www.linkedin.com/in/vladislav-aleshaev/' },
  { name: 'GitHub profile', href: 'https://github.com/Gurgant' },
  { name: 'Source code', href: 'https://github.com/Gurgant/azurebank-v2' },
  {
    name: 'private vulnerability reporting',
    href: 'https://github.com/Gurgant/azurebank-v2/security',
  },
] as const;

async function renderContact(store?: TestStore) {
  const router = createMemoryRouter(
    createRoutesFromElements(
      <Route element={<RouteAnnouncer />}>
        <Route
          path="/contact"
          handle={{ title: 'Contact' }}
          element={
            <ShellOrBare>
              <ContactPage />
            </ShellOrBare>
          }
        />
        <Route path="/login" element={<div>LOGIN PAGE</div>} />
      </Route>,
    ),
    { initialEntries: ['/contact'] },
  );
  const theStore = store ?? (await signedOut());
  return render(
    <ThemeProvider>
      <Provider store={theStore}>
        <RouterProvider router={router} />
      </Provider>
    </ThemeProvider>,
  );
}

/** A store whose session check was answered 401, as the app's is for a visitor with no session. */
async function signedOut(): Promise<TestStore> {
  // The setup signs every test in, because the guarded pages need it.
  mockState.session = null;
  const store = makeTestStore();
  await store.dispatch(apiSlice.endpoints.getMe.initiate());
  expect(store.getState().auth.status).toBe('anonymous');
  return store;
}

/** A store that has read the session, as the app has once a visitor is signed in. */
async function signedIn(): Promise<TestStore> {
  seedMockSession();
  const store = makeTestStore();
  await store.dispatch(apiSlice.endpoints.getMe.initiate()).unwrap();
  return store;
}

/** A paragraph's words as a visitor reads them, its link's words included. */
const paragraph = (start: string) =>
  screen.getByText(
    (_, element) => element?.tagName === 'P' && !!element.textContent?.startsWith(start),
  ).textContent;

describe('Contact', () => {
  it('introduces me first, then the project, under one <h1> and two cards named by their <h2>', async () => {
    await renderContact();

    expect({
      tree: screen
        .getAllByRole('heading')
        .map((heading) => `${heading.tagName}:${heading.textContent}`),
      regions: [WORDS.me, WORDS.project].map(
        (name) =>
          within(screen.getByRole('region', { name })).getByRole('heading', { level: 2 })
            .textContent,
      ),
      me: within(screen.getByRole('region', { name: WORDS.me }))
        .getAllByRole('paragraph')
        .map((p) => p.textContent),
      security: paragraph('Found a security problem?'),
    }).toStrictEqual({
      tree: [`H1:${WORDS.title}`, `H2:${WORDS.me}`, `H2:${WORDS.project}`],
      regions: [WORDS.me, WORDS.project],
      me: [WORDS.role, WORDS.firstPerson, WORDS.quickest],
      security: WORDS.security,
    });
    const project = screen.getByRole('region', { name: WORDS.project });
    expect(
      within(project).getByText(/AzureBank is a portfolio project, not a real bank\./),
    ).toBeVisible();
    expect(within(project).getByText(/No money moves/)).toBeVisible();
    expect(within(project).getByText(/A \.NET API and a BFF that holds the session/)).toBeVisible();
  });

  it('links to my two profiles, the source and the private security channel, each in a new tab told nothing of this one', async () => {
    await renderContact();

    const links = OUTSIDE_LINKS.map(({ name }) => {
      const link = screen.getByRole('link', { name });
      return {
        name,
        href: link.getAttribute('href'),
        target: link.getAttribute('target'),
        rel: link.getAttribute('rel'),
      };
    });

    expect(links).toEqual(
      OUTSIDE_LINKS.map(({ name, href }) => ({
        name,
        href,
        target: '_blank',
        rel: 'noreferrer noopener',
      })),
    );
  });

  it('is read by Tab in the order it is drawn: the way back, the three profiles, the security channel', async () => {
    await renderContact();
    const user = userEvent.setup();

    const reached: (string | null)[] = [];
    for (let i = 0; i < 5; i += 1) {
      await user.tab();
      reached.push(document.activeElement?.textContent ?? null);
    }

    expect(reached).toEqual(['Back to sign in', ...OUTSIDE_LINKS.map(({ name }) => name)]);
  });

  it('underlines the security link, which sits inside a sentence', async () => {
    // `index.css` takes the underline off every link, and Vitest loads no stylesheet: without its
    // rule here, jsdom's own underline would pass this test whatever the page sets. The longhand, on
    // the page and here: jsdom does not expand the shorthand, and reports its own default for it. Told apart by
    // its colour alone, the link measured 1.71:1 against the sentence in light and 2.21:1 in dark,
    // and axe's `link-in-text-block` (WCAG 1.4.1) failed on it: the rule asks for 3:1 or an
    // underline.
    const reset = /^a \{[^}]*\}/m.exec(readFileSync('src/index.css', 'utf8'));
    expect(reset?.[0]).toContain('text-decoration: none');
    const sheet = document.createElement('style');
    sheet.textContent = reset![0];
    document.head.append(sheet);
    try {
      await renderContact();

      const link = screen.getByRole('link', { name: 'private vulnerability reporting' });

      expect(getComputedStyle(link).textDecorationLine).toBe('underline');
    } finally {
      sheet.remove();
    }
  });

  it('carries no address to write to and no number to call', async () => {
    // The repository is public, and so is this page: what it shows is harvested the day it
    // ships. The channel is a profile, which has its own controls.
    await renderContact();

    const hrefs = screen.getAllByRole('link').map((link) => link.getAttribute('href') ?? '');

    expect(hrefs.filter((href) => /^(mailto|tel):/i.test(href))).toEqual([]);
    // No address in the words either: the page has no at-sign at all.
    expect(document.body.textContent).not.toMatch(/@/);
  });

  it('signed out, stands alone and leads back to sign-in', async () => {
    await renderContact();

    // No app navigation around a visitor who has nowhere in the app to go.
    expect(screen.queryByRole('navigation', { name: 'Main navigation' })).not.toBeInTheDocument();
    const back = screen.getByRole('link', { name: 'Back to sign in' });
    expect(back).toHaveAttribute('href', '/login');

    await userEvent.click(back);
    expect(await screen.findByText('LOGIN PAGE')).toBeInTheDocument();
  });

  it("signed in, sits inside the app's navigation, lit on Contact, and offers no way back to sign-in", async () => {
    await renderContact(await signedIn());

    expect(screen.getByRole('heading', { name: WORDS.title, level: 1 })).toBeVisible();
    // The shell is the way back: the visitor's name and the places of the app are around the page.
    expect(screen.getByText('Demo User')).toBeInTheDocument();
    const [nav] = screen.getAllByRole('navigation', { name: 'Main navigation' });
    expect(within(nav).getByRole('link', { name: 'Home' })).toHaveAttribute('href', '/dashboard');
    expect(within(nav).getByRole('link', { name: 'Contact' })).toHaveAttribute(
      'aria-current',
      'page',
    );
    expect(screen.queryByRole('link', { name: 'Back to sign in' })).not.toBeInTheDocument();
  });

  it('when the service cannot answer the check, says so instead of offering sign-in', async () => {
    // A 503 says nothing about the session, so the status stays 'unknown' over a session the
    // visitor may still have, and a way back to sign-in would read as being signed out.
    let down = true;
    server.use(
      http.get('*/bff/auth/me', () =>
        down ? serviceUnavailable({ via: 'bff', instance: '/bff/auth/me' }) : undefined,
      ),
    );
    const store = makeTestStore();
    await store.dispatch(apiSlice.endpoints.getMe.initiate());
    expect(store.getState().auth.status).toBe('unknown');
    await renderContact(store);

    expect(
      await screen.findByRole('heading', { level: 1, name: COPY.unavailableTitle }),
    ).toBeVisible();
    expect(document.title).toBe(`${COPY.unavailableTitle} · AzureBank`);
    expect(screen.getByRole('alert')).toHaveTextContent(COPY.unavailable);
    expect(screen.queryByRole('link', { name: 'Back to sign in' })).not.toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: WORDS.title })).not.toBeInTheDocument();

    // Back up: Try again finds the session, and the page is drawn in the shell under its own title.
    down = false;
    fireEvent.click(screen.getByRole('button', { name: COPY.tryAgain }));

    expect(await screen.findByRole('heading', { name: WORDS.title, level: 1 })).toBeVisible();
    await waitFor(() => expect(document.title).toBe('Contact · AzureBank'));
    const [nav] = screen.getAllByRole('navigation', { name: 'Main navigation' });
    expect(within(nav).getByRole('link', { name: 'Home' })).toHaveAttribute('href', '/dashboard');
    expect(screen.queryByRole('link', { name: 'Back to sign in' })).not.toBeInTheDocument();
  });
});

describe('the old address, /about', () => {
  afterEach(() => {
    cleanup();
    window.history.replaceState(null, '', '/');
  });

  it('lands on /contact in place of itself, under the title "Contact · AzureBank"', async () => {
    // App.tsx builds its router once, from the address the window holds when the module is
    // evaluated, so the address goes in first and App is imported again after the registry is
    // emptied, as not-found.test.tsx does.
    window.history.replaceState(null, '', '/about');
    const entries = window.history.length;
    vi.resetModules();
    const { default: App } = await import('../App');
    render(<App />);

    expect(await screen.findByRole('heading', { name: WORDS.title, level: 1 })).toBeVisible();
    await waitFor(() =>
      expect({
        address: window.location.pathname,
        title: document.title,
        // `replace`: the old address is not left behind it, so Back does not bounce off it.
        entries: window.history.length,
      }).toEqual({ address: '/contact', title: 'Contact · AzureBank', entries }),
    );
  });
});
