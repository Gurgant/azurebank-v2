import { StrictMode } from 'react';
import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent, { type UserEvent } from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import type { ApiProblem } from '../../api/problemBaseQuery';
import { ACCOUNT_CLOSED, SIGN_OUT_FAILED } from '../../api/problemMessages';
import { apiSlice } from '../../features/api/apiSlice';
import { sessionExpired, signedOut } from '../../features/auth/authSlice';
import { NEW_COPY_READY } from '../../features/demo/demoWords';
import { useNewCopyToast } from '../../features/demo/useNewCopyToast';
import { seedMockSession } from '../../mocks/state';
import { makeTestStore, renderWithProviders, type TestStore } from '../../test/renderWithProviders';
import { AppToaster } from './AppToaster';
import { dismissProblemToasts, useAccountClosedToast, useProblemToast } from './useProblemToast';

/*
  What closes an error toast: its own Dismiss, or the end of the session it was about. The case in
  point is a sign-out that failed: its toast says "You're still signed in.", which is false once a
  later sign-out has worked, and on a phone it covered the sign-in page's email field.
*/

const TITLE = 'Service Unavailable';
const OUTAGE: ApiProblem = {
  status: 503,
  errorCode: 'SERVICE_UNAVAILABLE',
  title: TITLE,
  detail: 'The service is temporarily unavailable. Please try again later.',
  traceId: '4bf92f3577b34da6a3ce929d0e0e4736',
};

/**
 * The outlet, and a button for each kind of toast: a failed sign-out's, a closed account's, and
 * the demo's news.
 */
function Harness() {
  const showProblem = useProblemToast();
  const sayAccountClosed = useAccountClosedToast();
  const sayNewCopy = useNewCopyToast();
  return (
    <>
      <AppToaster />
      <button type="button" onClick={() => showProblem(OUTAGE, SIGN_OUT_FAILED)}>
        Fail to sign out
      </button>
      <button type="button" onClick={sayAccountClosed}>
        Close the account
      </button>
      <button type="button" onClick={sayNewCopy}>
        Say a new copy
      </button>
    </>
  );
}

async function signedIn(): Promise<TestStore> {
  seedMockSession();
  const store = makeTestStore();
  await store.dispatch(apiSlice.endpoints.getMe.initiate()).unwrap();
  return store;
}

/** The toasts on the page as the outlet draws them: items of its list, named by their titles. */
function toastsNamed(name: string): HTMLElement[] {
  return screen.queryAllByRole('listitem', { name });
}

async function failToSignOut(user: UserEvent) {
  await user.click(screen.getByRole('button', { name: 'Fail to sign out' }));
}

/** Tab from the page's last button until focus is on `target`; how many presses it took. */
async function tabTo(user: UserEvent, target: HTMLElement): Promise<number> {
  screen.getByRole('button', { name: 'Say a new copy' }).focus();
  for (let presses = 1; presses <= 5; presses += 1) {
    await user.tab();
    if (document.activeElement === target) return presses;
  }
  throw new Error(`Tab never reached ${target.outerHTML}`);
}

describe('an error toast', () => {
  it.each([
    ['a click', (user: UserEvent, dismiss: HTMLElement) => user.click(dismiss)],
    [
      'Tab, then Enter',
      async (user: UserEvent, dismiss: HTMLElement) => {
        // Into the toast, then to its Dismiss: the outlet is the page's last stop.
        expect(await tabTo(user, dismiss)).toBe(2);
        await user.keyboard('{Enter}');
      },
    ],
  ])('says Dismiss, and %s on it closes that toast and no other', async (_how, press) => {
    const store = await signedIn();
    const user = userEvent.setup();
    renderWithProviders(<Harness />, { store });

    // Two failures, two toasts: each is said under an id of its own.
    await failToSignOut(user);
    await failToSignOut(user);
    await waitFor(() => expect(toastsNamed(TITLE)).toHaveLength(2));
    const [newer, older] = toastsNamed(TITLE);

    // The words are the same as before: the caller's sentence, the problem's, the support code.
    expect(
      within(newer).getByText(
        `${SIGN_OUT_FAILED} ${OUTAGE.detail} Support code: ${OUTAGE.traceId}`,
      ),
    ).toBeInTheDocument();
    const dismiss = within(newer).getByRole('button', { name: 'Dismiss' });

    await press(user, dismiss);

    await waitFor(() => expect(toastsNamed(TITLE)).toEqual([older]));
    expect(within(older).getByRole('button', { name: 'Dismiss' })).toBeInTheDocument();
  });

  it('stays when the time of a toast that leaves by itself runs out', async () => {
    const store = await signedIn();
    const user = userEvent.setup();
    renderWithProviders(<Harness />, { store });
    await failToSignOut(user);
    await user.click(screen.getByRole('button', { name: 'Close the account' }));
    await user.click(screen.getByRole('button', { name: 'Say a new copy' }));
    await waitFor(() => expect(toastsNamed(NEW_COPY_READY)).toHaveLength(1));

    // Fluent times a toast with a CSS animation and closes it when that animation ends. jsdom
    // runs none, so the end is fired here, under the name React listens for in a window without
    // AnimationEvent, as jsdom's is. Only a toast with a timeout has a timer to end.
    const timers = document.querySelectorAll('[data-timer-status]');
    expect(timers).toHaveLength(1);
    for (const timer of timers) {
      fireEvent(timer, new Event('webkitAnimationEnd', { bubbles: true }));
    }

    await waitFor(() => expect(toastsNamed(NEW_COPY_READY)).toHaveLength(0));
    expect(toastsNamed(TITLE)).toHaveLength(1);
    expect(toastsNamed(ACCOUNT_CLOSED)).toHaveLength(1);
  });
});

describe('the end of a session', () => {
  it.each([
    ['signed out', signedOut],
    ['expired', sessionExpired],
  ])('takes the error toasts away: %s', async (_how, end) => {
    const store = await signedIn();
    const user = userEvent.setup();
    renderWithProviders(<Harness />, { store });
    await failToSignOut(user);
    await failToSignOut(user);
    await waitFor(() => expect(toastsNamed(TITLE)).toHaveLength(2));

    act(() => {
      store.dispatch(end());
    });

    await waitFor(() => expect(toastsNamed(TITLE)).toHaveLength(0));
  });

  it('takes them away under StrictMode too, as the app runs in development', async () => {
    // StrictMode mounts each toast twice, and Fluent says "unmounted" for the first of the two.
    const store = await signedIn();
    const user = userEvent.setup();
    renderWithProviders(
      <StrictMode>
        <Harness />
      </StrictMode>,
      { store },
    );
    await failToSignOut(user);
    await waitFor(() => expect(toastsNamed(TITLE)).toHaveLength(1));

    act(() => {
      store.dispatch(signedOut());
    });

    await waitFor(() => expect(toastsNamed(TITLE)).toHaveLength(0));
  });

  it('leaves "You have a new copy.", said before the sign-out and after it', async () => {
    const store = await signedIn();
    const user = userEvent.setup();
    renderWithProviders(<Harness />, { store });
    await failToSignOut(user);
    await user.click(screen.getByRole('button', { name: 'Say a new copy' }));
    await waitFor(() => expect(toastsNamed(NEW_COPY_READY)).toHaveLength(1));
    expect(toastsNamed(TITLE)).toHaveLength(1);

    act(() => {
      store.dispatch(signedOut());
    });

    await waitFor(() => expect(toastsNamed(TITLE)).toHaveLength(0));
    expect(toastsNamed(NEW_COPY_READY)).toHaveLength(1);

    await user.click(screen.getByRole('button', { name: 'Say a new copy' }));
    await waitFor(() => expect(toastsNamed(NEW_COPY_READY)).toHaveLength(2));
  });
});

describe("a closed account's toast", () => {
  it('says Dismiss, and a click on it closes the toast', async () => {
    const store = await signedIn();
    const user = userEvent.setup();
    renderWithProviders(<Harness />, { store });

    await user.click(screen.getByRole('button', { name: 'Close the account' }));
    await waitFor(() => expect(toastsNamed(ACCOUNT_CLOSED)).toHaveLength(1));
    const [toast] = toastsNamed(ACCOUNT_CLOSED);

    await user.click(within(toast).getByRole('button', { name: 'Dismiss' }));

    await waitFor(() => expect(toastsNamed(ACCOUNT_CLOSED)).toHaveLength(0));
  });
});

describe('the record of error toasts', () => {
  it('lets go of a toast closed with Escape, and of those left when the outlet goes', async () => {
    const store = await signedIn();
    const user = userEvent.setup();
    const { unmount } = renderWithProviders(<Harness />, { store });
    const dismissToast = vi.fn<(toastId: string) => void>();

    await failToSignOut(user);
    await waitFor(() => expect(toastsNamed(TITLE)).toHaveLength(1));
    dismissProblemToasts(dismissToast);
    expect(dismissToast).toHaveBeenCalledTimes(1);

    // Escape in the toaster closes every toast in it, and none is left to take back.
    act(() => {
      toastsNamed(TITLE)[0].focus();
    });
    await user.keyboard('{Escape}');
    await waitFor(() => expect(toastsNamed(TITLE)).toHaveLength(0));
    dismissToast.mockClear();
    dismissProblemToasts(dismissToast);
    expect(dismissToast).not.toHaveBeenCalled();

    // A toast still open when its outlet unmounts, as at the end of a test, is not kept either.
    await failToSignOut(user);
    await waitFor(() => expect(toastsNamed(TITLE)).toHaveLength(1));
    unmount();
    dismissProblemToasts(dismissToast);
    expect(dismissToast).not.toHaveBeenCalled();
  });
});
