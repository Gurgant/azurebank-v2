import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { http } from 'msw';
import { server } from '../mocks/server';
import { mockState, seedMockSession } from '../mocks/state';
import { renderWithProviders } from '../test/renderWithProviders';
import { StepUpModal } from '../features/auth/StepUpModal';
import { AccountsPage } from './AccountsPage';
import { REVEAL_TIMEOUT_MS } from '../components/AccountNumberField';
import { installFakeClock } from '../test/outage';

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

/** The number's own row: the button, then the box of buttons, then the row. */
function numberRow(buttonName: string) {
  const row = screen.getByRole('button', { name: buttonName }).parentElement?.parentElement;
  if (!row) throw new Error(`no row for ${buttonName}`);
  return row;
}

beforeEach(() => {
  // Every scenario starts un-elevated (level 1); the elevated cases opt in explicitly. Pinning it
  // here keeps each test independent of ordering and of the global reset's timing.
  mockState.authLevel = 1;
});

/*
  A session, seeded, because the step-up routes now demand one.

  The mock used to answer /bff/auth/verify-pin for a caller with NO session at all, so these
  tests never had to establish one and quietly exercised a state the product cannot reach: the
  real BFF reads the session first and answers 401. Aligning the mock (contract gate) made that
  visible. Seeding is the faithful fix — a user reaching a PIN prompt is, by construction,
  already signed in.
*/
beforeEach(() => {
  seedMockSession();
});

/*
  The copy tests put a clipboard of their own on the page, and the files of a worker share that
  page: what was there before each test goes back after it, or nothing when nothing was.
*/
let pageClipboard: PropertyDescriptor | undefined;
beforeEach(() => {
  pageClipboard = Object.getOwnPropertyDescriptor(navigator, 'clipboard');
});
afterEach(() => {
  if (pageClipboard) Object.defineProperty(navigator, 'clipboard', pageClipboard);
  else Reflect.deleteProperty(navigator, 'clipboard');
});

describe('account number reveal (ADR-0020)', () => {
  it('reveals the full number after a PIN step-up, renames its toggle, and re-masks on Hide', async () => {
    const user = userEvent.setup();
    renderWithProviders(<AccountsWithStepUp />, { routerEntries: ['/accounts'] });

    expect(await screen.findByText(MASKED_MAIN)).toBeInTheDocument();
    const eye = screen.getByRole('button', { name: revealName });
    // The name carries the state; aria-pressed as well would announce "Hide …, pressed".
    expect(eye).not.toHaveAttribute('aria-pressed');

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

    /*
      findByRole, not getByRole, and the reason is the PAGE's accessibility rather than the button's.

      While the PIN modal is open, tabster aria-hides everything outside it — correctly; that is what
      a modal is. Un-hiding on close goes through the same debounced pass that hid it
      (`ModalizerAPI.hiddenUpdate` = `setTimeout(…, 250)`), so for up to a quarter second after the
      modal settles, this page is still out of the accessibility tree and a role query cannot see it.
      Measured: a bare `getByRole` here failed 6 out of 6 runs with eight busy cores, and passed
      unloaded — the window is real, it is just usually narrower than the gap between two statements.

      Waiting is the honest answer rather than a workaround: the state does settle, so the assertion
      is "this becomes reachable", which is exactly what a user experiences. Same rule as the P1.9
      sweep noted in `test/viewport.ts` — a query that follows an async transition is `findBy*`.
    */
    const hideBtn = await screen.findByRole('button', {
      name: `Hide account number for ${MAIN}`,
    });
    expect(hideBtn).not.toHaveAttribute('aria-pressed');
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

  it('while the reveal waits, its button says so and no progress bar without a name is exposed', async () => {
    // Elevated, so no modal comes up and the page stays in the accessibility tree; the read is
    // held, so the wait can be looked at. The button carries a spinner while it waits, and the
    // button's name is what says the wait. A spinner left exposed inside it is a progress bar
    // with no name of its own: axe reported that in a browser on 2026-10-06
    // (`aria-progressbar-name`, serious), where the spinner this button replaced had a name.
    mockState.authLevel = 2;
    let answer = () => {};
    const held = new Promise<void>((resolve) => {
      answer = resolve;
    });
    // It answers nothing itself: once released, the mock's own handler answers the read.
    server.use(
      http.get('*/api/accounts/:id/full-number', async () => {
        await held;
      }),
    );
    const user = userEvent.setup();
    renderWithProviders(<AccountsWithStepUp />, { routerEntries: ['/accounts'] });

    await user.click(await screen.findByRole('button', { name: revealName }));

    const waiting = await screen.findByRole('button', {
      name: `Revealing account number for ${MAIN}`,
    });
    expect(waiting).toHaveAttribute('aria-disabled', 'true');
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument();

    answer();
    expect(await screen.findByText(FULL_MAIN)).toBeInTheDocument();
  });

  it('copies the revealed number to the clipboard and shows a Copied status', async () => {
    mockState.authLevel = 2;
    const user = userEvent.setup();
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });

    renderWithProviders(<AccountsWithStepUp />, { routerEntries: ['/accounts'] });
    await user.click(await screen.findByRole('button', { name: revealName }));
    await screen.findByText(FULL_MAIN);

    const field = numberRow(`Hide account number for ${MAIN}`);
    const beforeCopy = within(field).getByRole('status');
    expect(beforeCopy).toBeEmptyDOMElement();
    expect(getComputedStyle(beforeCopy).position).toBe('absolute');

    await user.click(screen.getByRole('button', { name: `Copy account number for ${MAIN}` }));

    expect(writeText).toHaveBeenCalledWith(FULL_MAIN);
    expect(within(field).getByRole('status')).toHaveTextContent('Copied');
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
    expect(within(numberRow(revealName)).getByRole('status')).toHaveTextContent(
      'Account number hidden again.',
    );

    await user.click(screen.getByRole('button', { name: revealName }));
    expect(await screen.findByText(FULL_MAIN)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: `Hide account number for ${MAIN}` }));
    await waitFor(() => expect(screen.getByText(MASKED_MAIN)).toBeInTheDocument());
    expect(within(numberRow(revealName)).getByRole('status')).not.toHaveTextContent(
      'Account number hidden again.',
    );
  });

  it('a copy that finishes after the number was hidden again does not say Copied', async () => {
    installFakeClock();
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    let finishCopy = () => {};
    const writeText = vi.fn(
      () =>
        new Promise<void>((resolve) => {
          finishCopy = resolve;
        }),
    );
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    mockState.authLevel = 2;
    renderWithProviders(<AccountsWithStepUp />, { routerEntries: ['/accounts'] });

    await user.click(await screen.findByRole('button', { name: revealName }));
    await screen.findByText(FULL_MAIN);
    await user.click(screen.getByRole('button', { name: `Copy account number for ${MAIN}` }));
    expect(writeText).toHaveBeenCalledWith(FULL_MAIN);

    await act(async () => {
      vi.advanceTimersByTime(REVEAL_TIMEOUT_MS);
    });
    await waitFor(() => expect(screen.getByText(MASKED_MAIN)).toBeInTheDocument());
    const status = within(numberRow(revealName)).getByRole('status');
    expect(status).toHaveTextContent('Account number hidden again.');

    await act(async () => {
      finishCopy();
    });
    expect(status).toHaveTextContent('Account number hidden again.');
    expect(status).not.toHaveTextContent('Copied');
  });

  it('the PIN modal says what the PIN is asked for, and no transfer', async () => {
    // The number's reveal is the only thing that brings this modal up (ADR-0041), and until
    // 2026-10-06 it asked for the PIN "to authorize this transfer".
    const user = userEvent.setup();
    renderWithProviders(<AccountsWithStepUp />, { routerEntries: ['/accounts'] });

    await user.click(await screen.findByRole('button', { name: revealName }));

    const modal = await screen.findByRole('alertdialog', { name: /verify it's you/i });
    expect(modal).toHaveTextContent('Enter your 6-digit PIN to show the full account number.');
    expect(modal).not.toHaveTextContent(/transfer/i);
  });

  it('the PIN modal gives focus back to the button that brought it up', async () => {
    const user = userEvent.setup();
    renderWithProviders(<AccountsWithStepUp />, { routerEntries: ['/accounts'] });

    // Cancelled: back on the button, which can be pressed again.
    const eye = await screen.findByRole('button', { name: revealName });
    await user.click(eye);
    await screen.findByText("Verify it's you");
    await user.click(await screen.findByRole('button', { name: 'Cancel' }));
    await waitFor(() => expect(screen.queryByText("Verify it's you")).not.toBeInTheDocument());
    await waitFor(() => expect(screen.getByRole('button', { name: revealName })).toHaveFocus());

    // Answered: on the same button, which now hides the number it showed.
    await user.click(screen.getByRole('button', { name: revealName }));
    await screen.findByText("Verify it's you");
    await user.click(await screen.findByLabelText('Digit 1 of 6'));
    await user.paste('123456');
    await screen.findByText(FULL_MAIN);
    await waitFor(() =>
      expect(screen.getByRole('button', { name: `Hide account number for ${MAIN}` })).toHaveFocus(),
    );
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

describe("the number's buttons count as actions on the page", () => {
  const primarySentence = (name: string) => `"${name}" is now your primary account.`;

  async function setPrimary(user: ReturnType<typeof userEvent.setup>, name: string) {
    await user.click(screen.getByRole('button', { name: `Account actions for ${name}` }));
    await user.click(await screen.findByRole('menuitem', { name: 'Set as primary' }));
  }

  it("Reveal, Copy and Hide each clear the page's sentence", async () => {
    mockState.authLevel = 2;
    const user = userEvent.setup();
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    renderWithProviders(<AccountsWithStepUp />, { routerEntries: ['/accounts'] });
    await screen.findByText('Rainy Day');

    await setPrimary(user, 'Rainy Day');
    await screen.findByText(primarySentence('Rainy Day'), { exact: true });
    await user.click(screen.getByRole('button', { name: revealName }));
    await screen.findByText(FULL_MAIN);
    expect(screen.queryByText(primarySentence('Rainy Day'), { exact: true })).toBeNull();

    await setPrimary(user, MAIN);
    await screen.findByText(primarySentence(MAIN), { exact: true });
    await user.click(screen.getByRole('button', { name: `Copy account number for ${MAIN}` }));
    expect(writeText).toHaveBeenCalledWith(FULL_MAIN);
    expect(screen.queryByText(primarySentence(MAIN), { exact: true })).toBeNull();

    await setPrimary(user, 'Rainy Day');
    await screen.findByText(primarySentence('Rainy Day'), { exact: true });
    await user.click(screen.getByRole('button', { name: `Hide account number for ${MAIN}` }));
    expect(screen.getByText(MASKED_MAIN)).toBeInTheDocument();
    expect(screen.queryByText(primarySentence('Rainy Day'), { exact: true })).toBeNull();
  });

  it('says nothing for a new primary whose answer arrives after a Reveal', async () => {
    mockState.authLevel = 2;
    const user = userEvent.setup();
    let release = () => {};
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(
      http.patch('*/api/accounts/:id/set-primary', async () => {
        await held;
      }),
    );
    renderWithProviders(<AccountsWithStepUp />, { routerEntries: ['/accounts'] });
    await screen.findByText('Rainy Day');

    await setPrimary(user, 'Rainy Day');
    await user.click(screen.getByRole('button', { name: revealName }));
    await screen.findByText(FULL_MAIN);

    release();
    // The answer has arrived once the list it refreshes puts Rainy Day first.
    await waitFor(() => {
      const names = screen.getAllByText(/^(Main Account|Rainy Day)$/).map((el) => el.textContent);
      expect(names[0]).toBe('Rainy Day');
    });
    expect(screen.queryByText(primarySentence('Rainy Day'), { exact: true })).toBeNull();
  });
});
