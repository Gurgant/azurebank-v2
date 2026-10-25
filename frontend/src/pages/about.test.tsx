import { Provider } from 'react-redux';
import {
  createMemoryRouter,
  createRoutesFromElements,
  Route,
  RouterProvider,
} from 'react-router-dom';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http } from 'msw';
import { describe, expect, it } from 'vitest';
import { RouteAnnouncer } from '../components/layout/RouteAnnouncer';
import { ShellOrBare } from '../components/layout/ShellOrBare';
import { apiSlice } from '../features/api/apiSlice';
import { serviceUnavailable } from '../mocks/problem';
import { server } from '../mocks/server';
import { mockState, seedMockSession } from '../mocks/state';
import { COPY } from '../test/outage';
import { makeTestStore, type TestStore } from '../test/renderWithProviders';
import { ThemeProvider } from '../theme/ThemeProvider';
import { AboutPage } from './AboutPage';

/**
 * The page where the app says what it is: a portfolio project and not a bank, and who wrote it.
 * It is the one page a visitor reaches signed out as well as signed in, on one route, and what
 * differs between the two is held here: the way back to sign-in, and the app's own navigation.
 *
 * The route is rendered as `App.tsx` renders it: inside `ShellOrBare`, which draws nothing of the
 * page until the session check has answered, so each store here has asked it first; and under
 * `RouteAnnouncer` with the route's title in its handle, on a data router of its own, because
 * `RouteAnnouncer` reads titles from the data router's matches and a descendant `<Routes>` is not
 * among them.
 */

async function renderAbout(store?: TestStore) {
  const router = createMemoryRouter(
    createRoutesFromElements(
      <Route element={<RouteAnnouncer />}>
        <Route
          path="/about"
          handle={{ title: 'About this project' }}
          element={
            <ShellOrBare>
              <AboutPage />
            </ShellOrBare>
          }
        />
        <Route path="/login" element={<div>LOGIN PAGE</div>} />
      </Route>,
    ),
    { initialEntries: ['/about'] },
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

describe('About this project', () => {
  it('says it is a portfolio project and not a bank, and who wrote it', async () => {
    await renderAbout();

    expect(screen.getByRole('heading', { name: 'About this project', level: 1 })).toBeVisible();
    expect(screen.getByText(/AzureBank is a portfolio project, not a real bank\./)).toBeVisible();
    expect(screen.getByText(/No money moves/)).toBeVisible();
    expect(screen.getByText(/Vladislav Aleshaev — sole developer\./)).toBeVisible();
  });

  it('links to the two profiles, each in a new tab that is told nothing of this one', async () => {
    await renderAbout();

    const links = ['LinkedIn', 'GitHub'].map((name) => {
      const link = screen.getByRole('link', { name });
      return {
        name,
        href: link.getAttribute('href'),
        target: link.getAttribute('target'),
        rel: link.getAttribute('rel'),
      };
    });

    expect(links).toEqual([
      {
        name: 'LinkedIn',
        href: 'https://www.linkedin.com/in/vladislav-aleshaev/',
        target: '_blank',
        rel: 'noreferrer noopener',
      },
      {
        name: 'GitHub',
        href: 'https://github.com/Gurgant',
        target: '_blank',
        rel: 'noreferrer noopener',
      },
    ]);
  });

  it('carries no address to write to and no number to call', async () => {
    // The repository is public, and so is this page: what it shows is harvested the day it
    // ships. The channel is a profile, which has its own controls.
    await renderAbout();

    const hrefs = screen.getAllByRole('link').map((link) => link.getAttribute('href') ?? '');

    expect(hrefs.filter((href) => /^(mailto|tel):/i.test(href))).toEqual([]);
    // No address in the words either: the page has no at-sign at all.
    expect(document.body.textContent).not.toMatch(/@/);
  });

  it('signed out, stands alone and leads back to sign-in', async () => {
    await renderAbout();

    // No app navigation around a visitor who has nowhere in the app to go.
    expect(screen.queryByRole('navigation', { name: 'Main navigation' })).not.toBeInTheDocument();
    const back = screen.getByRole('link', { name: 'Back to sign in' });
    expect(back).toHaveAttribute('href', '/login');

    await userEvent.click(back);
    expect(await screen.findByText('LOGIN PAGE')).toBeInTheDocument();
  });

  it("signed in, sits inside the app's navigation and offers no way back to sign-in", async () => {
    await renderAbout(await signedIn());

    expect(screen.getByRole('heading', { name: 'About this project', level: 1 })).toBeVisible();
    // The shell is the way back: the visitor's name and the places of the app are around the page.
    expect(screen.getByText('Demo User')).toBeInTheDocument();
    const [nav] = screen.getAllByRole('navigation', { name: 'Main navigation' });
    expect(within(nav).getByRole('link', { name: 'Home' })).toHaveAttribute('href', '/dashboard');
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
    await renderAbout(store);

    expect(
      await screen.findByRole('heading', { level: 1, name: COPY.unavailableTitle }),
    ).toBeVisible();
    expect(document.title).toBe(`${COPY.unavailableTitle} · AzureBank`);
    expect(screen.getByRole('alert')).toHaveTextContent(COPY.unavailable);
    expect(screen.queryByRole('link', { name: 'Back to sign in' })).not.toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'About this project' })).not.toBeInTheDocument();

    // Back up: Try again finds the session, and the page is drawn in the shell under its own title.
    down = false;
    fireEvent.click(screen.getByRole('button', { name: COPY.tryAgain }));

    expect(
      await screen.findByRole('heading', { name: 'About this project', level: 1 }),
    ).toBeVisible();
    await waitFor(() => expect(document.title).toBe('About this project · AzureBank'));
    const [nav] = screen.getAllByRole('navigation', { name: 'Main navigation' });
    expect(within(nav).getByRole('link', { name: 'Home' })).toHaveAttribute('href', '/dashboard');
    expect(screen.queryByRole('link', { name: 'Back to sign in' })).not.toBeInTheDocument();
  });
});
