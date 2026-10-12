import { Route, Routes } from 'react-router-dom';
import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { ShellOrBare } from '../components/layout/ShellOrBare';
import { apiSlice } from '../features/api/apiSlice';
import { seedMockSession } from '../mocks/state';
import { makeTestStore, renderWithProviders, type TestStore } from '../test/renderWithProviders';
import { AboutPage } from './AboutPage';

/**
 * The page where the app says what it is: a portfolio project and not a bank, and who wrote it.
 * It is the one page a visitor reaches signed out as well as signed in, on one route, and what
 * differs between the two is held here: the way back to sign-in, and the app's own navigation.
 *
 * The route is rendered as `App.tsx` renders it, inside `ShellOrBare`.
 */

function renderAbout(store: TestStore = makeTestStore()) {
  return renderWithProviders(
    <Routes>
      <Route
        path="/about"
        element={
          <ShellOrBare>
            <AboutPage />
          </ShellOrBare>
        }
      />
      <Route path="/login" element={<div>LOGIN PAGE</div>} />
    </Routes>,
    { store, routerEntries: ['/about'] },
  );
}

/** A store that has read the session, as the app has once a visitor is signed in. */
async function signedIn(): Promise<TestStore> {
  seedMockSession();
  const store = makeTestStore();
  await store.dispatch(apiSlice.endpoints.getMe.initiate()).unwrap();
  return store;
}

describe('About this project', () => {
  it('says it is a portfolio project and not a bank, and who wrote it', () => {
    renderAbout();

    expect(screen.getByRole('heading', { name: 'About this project', level: 1 })).toBeVisible();
    expect(screen.getByText(/AzureBank is a portfolio project, not a real bank\./)).toBeVisible();
    expect(screen.getByText(/No money moves/)).toBeVisible();
    expect(screen.getByText(/Vladislav Aleshaev — sole developer\./)).toBeVisible();
  });

  it('links to the two profiles, each in a new tab that is told nothing of this one', () => {
    renderAbout();

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

  it('carries no address to write to and no number to call', () => {
    // The repository is public, and so is this page: what it shows is harvested the day it
    // ships. The channel is a profile, which has its own controls.
    renderAbout();

    const hrefs = screen.getAllByRole('link').map((link) => link.getAttribute('href') ?? '');

    expect(hrefs.filter((href) => /^(mailto|tel):/i.test(href))).toEqual([]);
    // No address in the words either: the page has no at-sign at all.
    expect(document.body.textContent).not.toMatch(/@/);
  });

  it('signed out, stands alone and leads back to sign-in', async () => {
    renderAbout();

    // No app navigation around a visitor who has nowhere in the app to go.
    expect(screen.queryByRole('navigation', { name: 'Main navigation' })).not.toBeInTheDocument();
    const back = screen.getByRole('link', { name: 'Back to sign in' });
    expect(back).toHaveAttribute('href', '/login');

    await userEvent.click(back);
    expect(await screen.findByText('LOGIN PAGE')).toBeInTheDocument();
  });

  it("signed in, sits inside the app's navigation and offers no way back to sign-in", async () => {
    renderAbout(await signedIn());

    expect(screen.getByRole('heading', { name: 'About this project', level: 1 })).toBeVisible();
    // The shell is the way back: the visitor's name and the places of the app are around the page.
    expect(screen.getByText('Demo User')).toBeInTheDocument();
    const [nav] = screen.getAllByRole('navigation', { name: 'Main navigation' });
    expect(within(nav).getByRole('link', { name: 'Home' })).toHaveAttribute('href', '/dashboard');
    expect(screen.queryByRole('link', { name: 'Back to sign in' })).not.toBeInTheDocument();
  });
});
