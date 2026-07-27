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
  });

  it('is one bar height, where there used to be three', () => {
    // History said 56px with 0 16px of padding, both wizards said 56px with 0 12px, and the
    // transaction detail declared no height at all and left-aligned its content. One value now.
    renderHeader(<PageHeader />, '/history');
    const bar = screen.getByText('History').parentElement as HTMLElement;
    expect(bar).toHaveStyle({ height: '56px' });
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
