import { act, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { mockState } from '../mocks/state';
import { renderWithProviders } from '../test/renderWithProviders';
import { StepUpModal } from '../features/auth/StepUpModal';
import { AccountsPage } from './AccountsPage';
import { REVEAL_TIMEOUT_MS } from '../components/AccountNumberField';

// AccountsPage does not render the root step-up modal (App does), so pair them here: the reveal
// call rides baseQueryWithStepUp into this shared modal exactly as in production.
function AccountsWithStepUp() {
  return (
    <>
      <AccountsPage />
      <StepUpModal />
    </>
  );
}

const MAIN = 'Main Account';
const MASKED_MAIN = 'AB-••••-••••-90';
const FULL_MAIN = 'AB-1234-5678-90';

const revealName = `Reveal full account number for ${MAIN}`;

beforeEach(() => {
  // Every scenario starts un-elevated (level 1); the elevated cases opt in explicitly. Pinning it
  // here keeps each test independent of ordering and of the global reset's timing.
  mockState.authLevel = 1;
});

afterEach(() => {
  vi.useRealTimers();
});

describe('account number reveal (ADR-0020)', () => {
  it('reveals the full number after a PIN step-up, toggles aria-pressed, and re-masks on Hide', async () => {
    const user = userEvent.setup();
    renderWithProviders(<AccountsWithStepUp />, { routerEntries: ['/accounts'] });

    expect(await screen.findByText(MASKED_MAIN)).toBeInTheDocument();
    const eye = screen.getByRole('button', { name: revealName });
    expect(eye).toHaveAttribute('aria-pressed', 'false');

    // Level 1 → the reveal 403s and the shared PIN modal opens.
    await user.click(eye);
    expect(await screen.findByText("Verify it's you")).toBeInTheDocument();
    await user.click(await screen.findByLabelText('Digit 1 of 6'));
    await user.paste('123456'); // onComplete auto-verifies → elevates → replays the reveal

    const revealedNumber = await screen.findByText(FULL_MAIN);
    expect(revealedNumber).toBeInTheDocument();
    // The revealed value must be the element's accessible name — no aria-label may shadow it.
    expect(revealedNumber).not.toHaveAttribute('aria-label');
    expect(screen.queryByText(MASKED_MAIN)).not.toBeInTheDocument();

    const hideBtn = screen.getByRole('button', { name: `Hide account number for ${MAIN}` });
    expect(hideBtn).toHaveAttribute('aria-pressed', 'true');
    await user.click(hideBtn);

    expect(await screen.findByText(MASKED_MAIN)).toBeInTheDocument();
    expect(screen.queryByText(FULL_MAIN)).not.toBeInTheDocument();
  });

  it('reveals directly without the modal when the session is already elevated', async () => {
    mockState.authLevel = 2;
    const user = userEvent.setup();
    renderWithProviders(<AccountsWithStepUp />, { routerEntries: ['/accounts'] });

    await user.click(await screen.findByRole('button', { name: revealName }));

    expect(await screen.findByText(FULL_MAIN)).toBeInTheDocument();
    expect(screen.queryByText("Verify it's you")).not.toBeInTheDocument();
  });

  it('copies the revealed number to the clipboard and shows a Copied status', async () => {
    mockState.authLevel = 2;
    const user = userEvent.setup();
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });

    renderWithProviders(<AccountsWithStepUp />, { routerEntries: ['/accounts'] });
    await user.click(await screen.findByRole('button', { name: revealName }));
    await screen.findByText(FULL_MAIN);

    await user.click(screen.getByRole('button', { name: `Copy account number for ${MAIN}` }));

    expect(writeText).toHaveBeenCalledWith(FULL_MAIN);
    expect(await screen.findByRole('status')).toHaveTextContent('Copied');
  });

  it('auto-rehides the number after the reveal timeout', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    mockState.authLevel = 2;
    renderWithProviders(<AccountsWithStepUp />, { routerEntries: ['/accounts'] });

    await user.click(await screen.findByRole('button', { name: revealName }));
    expect(await screen.findByText(FULL_MAIN)).toBeInTheDocument();

    await act(async () => {
      vi.advanceTimersByTime(REVEAL_TIMEOUT_MS);
    });

    await waitFor(() => expect(screen.getByText(MASKED_MAIN)).toBeInTheDocument());
    expect(screen.queryByText(FULL_MAIN)).not.toBeInTheDocument();
  });

  it('leaves the number masked (no error) when the PIN modal is cancelled', async () => {
    const user = userEvent.setup();
    renderWithProviders(<AccountsWithStepUp />, { routerEntries: ['/accounts'] });

    await user.click(await screen.findByRole('button', { name: revealName }));
    expect(await screen.findByText("Verify it's you")).toBeInTheDocument();

    await user.click(await screen.findByRole('button', { name: 'Cancel' }));

    await waitFor(() => expect(screen.queryByText("Verify it's you")).not.toBeInTheDocument());
    expect(screen.getByText(MASKED_MAIN)).toBeInTheDocument();
    expect(screen.queryByText(FULL_MAIN)).not.toBeInTheDocument();
  });
});
