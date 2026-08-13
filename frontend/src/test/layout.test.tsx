import { act, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it } from 'vitest';
import { mockState, seedMockSession } from '../mocks/state';
import { renderWithProviders } from './renderWithProviders';
import { StepUpModal } from '../features/auth/StepUpModal';
import { AccountsPage } from '../pages/AccountsPage';

/**
 * The guard for `test/layout.ts`, written to fail if the stubs are ever removed.
 *
 * Removing `installLayoutStubs()` from `setup.ts` makes the first two expectations below fail
 * deterministically — verified by doing exactly that, not by assuming it. Everything here is a
 * consequence of the causal chain documented in `layout.ts`; this file is the executable half of
 * that document.
 */

function AccountsWithStepUp() {
  return (
    <>
      <AccountsPage />
      <StepUpModal />
    </>
  );
}

/**
 * Comfortably past tabster's debounce.
 *
 * `ModalizerAPI.hiddenUpdate()` schedules `_hiddenUpdate()` with `setTimeout(…, 250)`
 * (tabster/dist/index.js:5354), and that pass is what used to hide the open dialog. Asserting
 * before it has had a chance to run would pass for the wrong reason — it is precisely the
 * queries that landed INSIDE that window which used to pass while the suite was broken.
 */
const PAST_TABSTER_HIDDEN_UPDATE_MS = 500;

beforeEach(() => {
  mockState.authLevel = 1;
  seedMockSession();
});

describe('jsdom layout stubs (test/layout.ts)', () => {
  it('leaves an OPEN Fluent dialog in the accessibility tree, after tabster has had its say', async () => {
    const user = userEvent.setup();
    renderWithProviders(<AccountsWithStepUp />, { routerEntries: ['/accounts'] });

    // Level 1 → the reveal 403s and the shared PIN modal opens.
    await user.click(
      await screen.findByRole('button', { name: 'Reveal full account number for Main Account' }),
    );
    await screen.findByText("Verify it's you");

    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, PAST_TABSTER_HIDDEN_UPDATE_MS));
    });

    const surface = document.querySelector('.fui-DialogSurface');
    expect(surface).not.toBeNull();
    /*
      The measured symptom, stated as the assertion.

      Without the stubs this attribute is "true", put there by
      ModalizerAPI._hiddenUpdate → toggle → augmentAttribute, and never removed. Asserting the
      attribute rather than the query result is deliberate: it names the actual defect, so a
      failure here says WHAT broke instead of only that some button went missing.
    */
    expect(surface?.getAttribute('aria-hidden')).toBeNull();

    // And the consequence the suite actually depends on: role queries still reach inside.
    expect(screen.getByRole('button', { name: 'Cancel' })).toBeInTheDocument();
  });

  it('puts initial focus where the browser would — on the PIN input, not the surface', async () => {
    const user = userEvent.setup();
    renderWithProviders(<AccountsWithStepUp />, { routerEntries: ['/accounts'] });

    await user.click(
      await screen.findByRole('button', { name: 'Reveal full account number for Main Account' }),
    );
    await screen.findByText("Verify it's you");

    /*
      This is the CAUSE, pinned separately from its effect above.

      Without the stubs, tabster finds nothing focusable in the dialog and Fluent falls through to
      `resetFocus(surface)` — so `document.activeElement` is the surface `<div>`, and because that
      fallback focuses without the programmatic flag the modalizer never activates. With them, the
      ladder terminates where the product intends: `PinInput`'s first digit, which carries
      `autoFocus`. If this ever regresses to the surface, the aria-hidden failure above is next.
    */
    expect(document.activeElement).toBe(screen.getByLabelText('Digit 1 of 6'));
  });

  it('still reports no box for a display:none element, so hidden stays hidden', () => {
    renderWithProviders(
      <div>
        <span data-testid="shown">shown</span>
        <div style={{ display: 'none' }}>
          <span data-testid="hidden">hidden</span>
        </div>
      </div>,
    );

    // The stubs answer "is this rendered at all", and must keep answering NO where the real
    // answer is no — otherwise they would trade one fabricated fact for another.
    const shown = screen.getByTestId('shown');
    const hidden = screen.getByTestId('hidden');

    expect(shown.getBoundingClientRect().height).toBeGreaterThan(0);
    expect(shown.getClientRects()).toHaveLength(1);
    expect((shown as HTMLElement).offsetParent).not.toBeNull();

    expect(hidden.getBoundingClientRect().height).toBe(0);
    expect(hidden.getClientRects()).toHaveLength(0);
    expect((hidden as HTMLElement).offsetParent).toBeNull();
  });
});
