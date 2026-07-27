import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { renderWithProviders } from '../../test/renderWithProviders';
import { LoginPage } from '../../pages/LoginPage';
import { RegisterPage } from '../../pages/RegisterPage';

/**
 * The auth frame's contract, asserted through the two pages that use it rather than through the
 * component alone — the point of the extraction is that those two pages can no longer disagree,
 * and that is only observable from outside.
 *
 * **The brand panel is queried through the DOM, never through a role or `getByText`.** jsdom does
 * not evaluate media queries, so its base `display: none` always applies there and the panel is
 * absent from the accessibility tree at every simulated width. A heading count taken with
 * `getAllByRole` would therefore have found ONE `<h1>` even on the version that shipped two — a
 * test passing for the wrong reason. `querySelectorAll` counts what is in the document regardless
 * of what CSS would hide, which is the only thing that makes this assertion mean anything.
 */

const PAGES = [
  { name: 'sign in', ui: <LoginPage />, entry: '/login', title: 'Welcome back' },
  { name: 'register', ui: <RegisterPage />, entry: '/register', title: 'Create Account' },
] as const;

describe.each(PAGES)('$name', ({ ui, entry, title }) => {
  it('puts exactly one level-one heading in the document, and it is the form', () => {
    const { container } = renderWithProviders(ui, { routerEntries: [entry] });

    // Both pages marked the brand panel's headline as `<h1>` AND the form title as `<h1>`. The
    // panel is hidden below 1024px, so a desktop had two level-one headings and a phone had one —
    // and neither arrangement made the page's subject unambiguous. The form wins because it is the
    // only heading that renders at every width.
    const headings = container.querySelectorAll('h1');
    expect(headings).toHaveLength(1);
    expect(headings[0]).toHaveTextContent(title);
  });

  it('dates the copyright from the clock, not from a literal', () => {
    const { container } = renderWithProviders(ui, { routerEntries: [entry] });

    // It read "© 2024" on the sign-in page — wrong within months of being typed, on the first
    // screen anyone sees — and registration carried no copyright at all. One panel, one live year.
    const year = new Date().getFullYear();
    expect(container.textContent).toMatch(new RegExp(`©\\s*${year}\\s+AzureBank`));
  });

  it('carries the same three promises in the brand panel', () => {
    const { container } = renderWithProviders(ui, { routerEntries: [entry] });
    const panel = container.querySelector('aside');

    expect(panel).not.toBeNull();
    for (const promise of [
      'Bank-grade security protection',
      'Instant transfers & payments',
      '24/7 account access',
    ]) {
      expect(panel?.textContent).toContain(promise);
    }
  });
});

describe('the two auth pages', () => {
  const panelTextOf = (ui: React.ReactElement, entry: string) => {
    const { container, unmount } = renderWithProviders(ui, { routerEntries: [entry] });
    const text = container.querySelector('aside')?.textContent ?? '';
    unmount();
    return text;
  };

  it('say different things inside the same frame', () => {
    // The headline and the sentence under it are the ONLY panel copy that ever differed; everything
    // else was duplicated verbatim. So those must differ and the rest must not.
    const login = panelTextOf(<LoginPage />, '/login');
    const register = panelTextOf(<RegisterPage />, '/register');

    expect(login).toContain('Banking Made Simple');
    expect(register).toContain('Start Your Financial Journey');
    expect(login).not.toBe(register);

    for (const shared of ['Bank-grade security protection', 'AzureBank']) {
      expect(login).toContain(shared);
      expect(register).toContain(shared);
    }
  });

  it('put different fine print under the form, which is why the footer is a slot', () => {
    const { unmount } = renderWithProviders(<LoginPage />, { routerEntries: ['/login'] });
    expect(screen.getByText(/bank-grade encryption/i)).toBeInTheDocument();
    unmount();

    renderWithProviders(<RegisterPage />, { routerEntries: ['/register'] });
    expect(screen.getByText(/Terms of Service and Privacy Policy/i)).toBeInTheDocument();
  });
});
