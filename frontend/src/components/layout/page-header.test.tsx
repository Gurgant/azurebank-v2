import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { FluentProvider } from '@fluentui/react-components';
import { describe, expect, it, vi } from 'vitest';
import pageHeaderSource from './PageHeader.tsx?raw';
import { azureBankLightTheme } from '../../theme/fluentTheme';
import { PageHeader } from './PageHeader';
import { TransactionDetailPage } from '../../pages/TransactionDetailPage';
import { renderWithProviders } from '../../test/renderWithProviders';

/**
 * The header's contract, and one property that matters more than the rest.
 *
 * The transfer wizards' Back and Close carry `disabled={keyLive}` — an idempotency key is alive,
 * so leaving could mean spending twice — and every exit routes through the page's own
 * `requestLeave`, which re-checks it. Moving those controls into a shared component is only safe if
 * the component decides nothing: it takes handlers and calls them.
 */

function renderHeader(ui: React.ReactElement, at = '/history') {
  return render(
    <FluentProvider theme={azureBankLightTheme}>
      <MemoryRouter initialEntries={[at]}>{ui}</MemoryRouter>
    </FluentProvider>,
  );
}

describe('PageHeader', () => {
  it('never navigates on its own', () => {
    // A source assertion, because this is the property no rendered output can prove absent. A
    // header that synthesised its own destination would walk straight through the wizards' guard,
    // and nothing would fail until somebody sent money twice.
    //
    // COMMENTS ARE STRIPPED FIRST. The prose in that file explains at length why it must not
    // navigate, so a naive scan matches its own explanation and the test passes — or, as it did
    // here, fails — for reasons that have nothing to do with the code.
    const code = pageHeaderSource.replace(/\/\*[\s\S]*?\*\//g, '').replace(/^\s*\/\/.*$/gm, '');

    expect(code).not.toMatch(/useNavigate/);
    expect(code).not.toMatch(/\bnavigate\s*\(/);
    // Reading the location is fine, and is how a place gets its title.
    expect(code).toMatch(/useLocation/);
  });

  it("reads a place's title from the same table the navigation reads", () => {
    renderHeader(<PageHeader />, '/history');
    // Not "Transaction History", which is what the page used to say while the nav said "History".
    expect(screen.getByText('History')).toBeInTheDocument();
  });

  it('offers no way out on a place, because a tab has no parent', () => {
    const { container } = renderHeader(<PageHeader />, '/history');
    expect(container.querySelectorAll('button')).toHaveLength(0);
  });

  it('keeps the title centred when a state has no exits at all', () => {
    // The success receipt and the "we couldn't confirm your transfer" screen deliberately render
    // no controls. Both edges still reserve their width — the hand-rolled version spaced one side
    // with a 40px span and the other with a bare <span/>, leaving the title 40px off centre on the
    // one screen a user reads most carefully.
    renderHeader(<PageHeader title="Transfer Complete" />);

    // Reached through the title, NOT through `container.firstElementChild` — FluentProvider wraps
    // its subtree in a themed div, so the container's first child is that wrapper and the edges
    // destructured off it are `undefined`, which is an assertion that cannot fail for the reason
    // it names.
    const bar = screen.getByText('Transfer Complete').parentElement as HTMLElement;
    const [left, , right] = [...bar.children] as HTMLElement[];

    expect(left).toHaveStyle({ width: '40px' });
    expect(right).toHaveStyle({ width: '40px' });
    // Equal edges alone do NOT centre anything — they only make the two ends symmetric. What puts
    // the title in the middle is the bar's own distribution, and without this line the test passed
    // 9/9 with `justifyContent` deleted from the component. Verified by deleting it.
    expect(bar).toHaveStyle({ justifyContent: 'space-between' });
  });

  it('is one bar height, where there used to be three', () => {
    // History said 56px with 0 16px of padding, both wizards said 56px with 0 12px, and the
    // transaction detail declared no height at all and left-aligned its content. One value now.
    renderHeader(<PageHeader />, '/history');
    const bar = screen.getByText('History').parentElement as HTMLElement;
    expect(bar).toHaveStyle({ height: '56px' });
  });

  it('puts the title in the heading tree, exactly once', () => {
    // Measured before this was added: /history, /transactions/:id, /transfer and /transfer/internal
    // each rendered ZERO headings of any level at 500px and at 1440px, while /settings and /login
    // in the same app rendered one. Heading navigation found nothing on four pages.
    //
    // h1 rather than h2 is safe here BECAUSE of that emptiness — there is no competing heading to
    // rank under. The failure shape to avoid is the one PR #48 fixed on the auth pages, where two
    // elements both claimed h1 and one of them was hidden behind a media query. PageHeader has no
    // media query and each page renders exactly one of it.
    const { container } = renderHeader(<PageHeader title="Send Money" />);

    const headings = container.querySelectorAll('h1');
    expect(headings).toHaveLength(1);
    expect(headings[0]).toHaveTextContent('Send Money');
    // No heading of any other level sneaks in alongside it.
    expect(container.querySelectorAll('h2,h3,h4,h5,h6')).toHaveLength(0);
  });

  it('announces no heading at all rather than an empty one', () => {
    // `heading` falls back to '' when a caller omits `title` on a path that matches no nav place.
    // Unreachable in the app today, which is precisely why it would rot unnoticed: an empty <h1>
    // is worse than none, because it puts a blank entry in the rotor that leads nowhere.
    const { container } = renderHeader(<PageHeader />, '/nowhere-in-the-nav');

    expect(container.querySelectorAll('h1')).toHaveLength(0);
    // The bar still renders, with both edges reserved, so nothing shifts.
    expect(container.querySelector('button')).toBeNull();
  });

  it('calls the handler it was given, and nothing else', async () => {
    const onBack = vi.fn();
    const onClose = vi.fn();
    renderHeader(<PageHeader title="Send Money" onBack={onBack} onClose={onClose} />);

    await userEvent.click(screen.getByRole('button', { name: 'Back' }));
    expect(onBack).toHaveBeenCalledTimes(1);
    expect(onClose).not.toHaveBeenCalled();

    await userEvent.click(screen.getByRole('button', { name: 'Close' }));
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('cannot be activated while the money guard is up', async () => {
    const onBack = vi.fn();
    const onClose = vi.fn();
    renderHeader(
      <PageHeader
        title="Review Transfer"
        onBack={onBack}
        backDisabled
        onClose={onClose}
        closeDisabled
      />,
    );

    const back = screen.getByRole('button', { name: 'Back' });
    const close = screen.getByRole('button', { name: 'Close' });
    expect(back).toBeDisabled();
    expect(close).toBeDisabled();

    await userEvent.click(back);
    await userEvent.click(close);
    expect(onBack).not.toHaveBeenCalled();
    expect(onClose).not.toHaveBeenCalled();
  });

  it('disables each side independently', () => {
    renderHeader(<PageHeader title="x" onBack={() => {}} backDisabled onClose={() => {}} />);
    expect(screen.getByRole('button', { name: 'Back' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Close' })).toBeEnabled();
  });
});

describe('a leaf page', () => {
  it('goes back to its section, not to wherever you came from', async () => {
    // Opened from the dashboard's recent feed, `navigate(-1)` returned to the dashboard while the
    // navigation insisted History was the current place — a header and a nav contradicting each
    // other on one screen. The up-link is deterministic now.
    renderWithProviders(
      <Routes>
        <Route path="/dashboard" element={<div>DASHBOARD</div>} />
        <Route path="/history" element={<div>HISTORY</div>} />
        <Route path="/transactions/:id" element={<TransactionDetailPage />} />
      </Routes>,
      { routerEntries: ['/dashboard', '/transactions/019f7b3f-0000-7000-8000-000000000b02'] },
    );

    const header = await screen.findByText('Transaction Details');
    await userEvent.click(
      within(header.parentElement as HTMLElement).getByRole('button', { name: 'Back' }),
    );

    expect(screen.getByText('HISTORY')).toBeInTheDocument();
    expect(screen.queryByText('DASHBOARD')).not.toBeInTheDocument();
  });
});
