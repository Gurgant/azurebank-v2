import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { renderWithProviders } from '../../test/renderWithProviders';
import { Logo } from './Logo';
import { Sidebar } from '../layout/Sidebar';
import { LoginPage } from '../../pages/LoginPage';
import { RegisterPage } from '../../pages/RegisterPage';

/**
 * Two contracts, and the second one is the reason this file exists.
 *
 * The component contract is ordinary: size, accessibility, and the asset it points at. The
 * placement contract is not — the previous version of this component was written, reviewed and
 * complete, and rendered in exactly zero places for as long as it existed. A brand mark nobody
 * renders is indistinguishable from no brand mark, and nothing in the suite would have said so.
 * These tests fail if the mark is unwired from any surface that is supposed to carry it.
 */

/** Decorative images have no role, so they are found by their source rather than by query. */
function marks(container: HTMLElement) {
  return container.querySelectorAll('img[src="/favicon.svg"]');
}

describe('Logo', () => {
  it('is decorative by default — no accessible name, nothing announced', () => {
    const { container } = renderWithProviders(<Logo size={32} />);

    const img = container.querySelector('img');
    expect(img).toBeInTheDocument();
    expect(img).toHaveAttribute('alt', '');
    // An empty alt maps the element to `presentation`, which is what keeps a screen reader from
    // reading the mark out a second time next to the wordmark it already sits beside.
    expect(screen.queryByRole('img')).not.toBeInTheDocument();
  });

  it('takes an accessible name when it stands alone', () => {
    renderWithProviders(<Logo size={32} label="AzureBank" />);

    expect(screen.getByRole('img', { name: 'AzureBank' })).toBeInTheDocument();
  });

  it('is square — one size drives both dimensions', () => {
    const { container } = renderWithProviders(<Logo size={48} />);

    const img = container.querySelector('img');
    expect(img).toHaveAttribute('width', '48');
    expect(img).toHaveAttribute('height', '48');
  });

  it('renders the generated favicon, not the bare master', () => {
    const { container } = renderWithProviders(<Logo size={24} />);

    // The coupling that keeps the tab and the app showing the same object. logo.svg is the
    // unplated 4:3 master and would letterbox; favicon.svg is the square plated tile.
    expect(container.querySelector('img')).toHaveAttribute('src', '/favicon.svg');
  });
});

describe('Logo placement', () => {
  it('is on the sign-in page, once per breakpoint lockup', () => {
    const { container } = renderWithProviders(<LoginPage />, { routerEntries: ['/login'] });

    // Two: the desktop brand panel and the mobile header. Both are in the DOM at all times —
    // a media query decides which one is visible, not a conditional render.
    expect(marks(container)).toHaveLength(2);
  });

  it('is on the registration page, once per breakpoint lockup', () => {
    const { container } = renderWithProviders(<RegisterPage />, { routerEntries: ['/register'] });

    expect(marks(container)).toHaveLength(2);
  });

  it('is in the shell sidebar, beside the wordmark', () => {
    const { container } = renderWithProviders(<Sidebar userName="Test User" />);

    expect(marks(container)).toHaveLength(1);
    expect(screen.getByText('AzureBank')).toBeInTheDocument();
  });
});
