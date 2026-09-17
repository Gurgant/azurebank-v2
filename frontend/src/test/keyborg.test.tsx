import { act, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createKeyborg, disposeKeyborg, KEYBORG_FOCUSIN } from 'keyborg';
import { describe, expect, it } from 'vitest';
import { MOCK_USER, seedMockSession } from '../mocks/state';
import { SettingsPage } from '../pages/SettingsPage';
import { makeTestStore, renderWithProviders } from './renderWithProviders';

/**
 * The guard for `test/keyborg.ts`, written to fail if the install is ever removed or stops working.
 *
 * The first test pins the cause, the second its effect, the way `layout.test.tsx` does for the
 * layout stubs. Commenting out `installKeyborgBeforeUserEvent()` in `setup.ts` fails both, in 4 of
 * 4 runs on 2026-09-17 — verified by doing it, not by assuming it.
 */

/**
 * Comfortably past both of tabster's timers: `_restoreModalizerFocus` at 100ms, which is what
 * moved focus out of the dialog, and `_hiddenUpdate` at 250ms, which then hid the dialog.
 */
const PAST_TABSTER_TIMERS_MS = 500;

describe('keyborg installed before user-event (test/keyborg.ts)', () => {
  it('reports a focus() call as programmatic when keyborg starts after userEvent.setup()', () => {
    userEvent.setup();
    // What tabster does when Fluent renders its first focus-trapping component.
    const keyborg = createKeyborg(window);
    const button = document.createElement('button');
    document.body.append(button);
    const seen: (boolean | undefined)[] = [];
    const onFocusIn = (e: Event) =>
      seen.push(
        (e as CustomEvent<{ isFocusedProgrammatically?: boolean }>).detail
          .isFocusedProgrammatically,
      );
    document.addEventListener(KEYBORG_FOCUSIN, onFocusIn, true);
    try {
      button.focus();
    } finally {
      document.removeEventListener(KEYBORG_FOCUSIN, onFocusIn, true);
      button.remove();
      disposeKeyborg(keyborg);
    }

    // Without the install this is [undefined]: keyborg's check that it can wrap `focus` fails
    // under user-event's getter, so it stops telling a programmatic focus from a user's.
    expect(seen).toEqual([true]);
  });

  it("keeps focus in an open dialog after tabster's timers have run", async () => {
    const user = userEvent.setup();
    seedMockSession();
    const store = makeTestStore();
    store.dispatch({
      type: 'api/executeQuery/fulfilled',
      meta: { arg: { endpointName: 'getMe' } },
      payload: { user: { ...MOCK_USER } },
    });
    renderWithProviders(<SettingsPage />, { store, routerEntries: ['/settings'] });
    await screen.findByText(`${MOCK_USER.firstName} ${MOCK_USER.lastName}`);

    await user.click(screen.getByRole('button', { name: 'Change' }));
    const input = await screen.findByRole('textbox');

    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, PAST_TABSTER_TIMERS_MS));
    });

    // Without the install, focus is on "Log out", outside the dialog, and the surface carries
    // aria-hidden="true", so every role query inside the dialog fails from here on.
    expect(document.activeElement).toBe(input);
    expect(document.querySelector('.fui-DialogSurface')?.getAttribute('aria-hidden')).toBeNull();
  });
});
