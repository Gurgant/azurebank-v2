import { act, cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AppToaster } from '../../components/feedback';
import { problem, serviceUnavailable } from '../../mocks/problem';
import { server } from '../../mocks/server';
import { mockState, seedMockDemoCopy } from '../../mocks/state';
import { enableDemoMode, rememberDemoCopy } from '../../test/demoMode';
import { expectNoNestedLiveRegions } from '../../test/liveRegions';
import {
  COPY,
  advanceUntil,
  fakeClockUser,
  hintRegion,
  hintShownAt,
  installFakeClock,
  never,
} from '../../test/outage';
import { renderWithProviders } from '../../test/renderWithProviders';
import { apiSlice } from '../api/apiSlice';
import { StartOverDialog } from './StartOverDialog';

/*
  The one dialog that asks before a claim replaces the copy this browser keeps: what it asks, what
  each button sends, and what it says for each answer a claim can get.

  Every test is on the demo, signed in to the first copy of the mock's pool (src/mocks/state.ts),
  with that copy remembered by the browser as a claim would have left it. A claim the mock answers
  itself hands out the pool's second copy. The two addresses are typed out below: fixtures no
  server knows.

  The words are typed out here and not imported from the product, so a test fails the day the words
  on screen are no longer these. The apostrophes are ASCII; the dash in the connection's sentence is
  U+2014.
*/
const WORDS = {
  title: 'Start over with a new copy?',
  message:
    "You'll get a fresh copy with the starting balances and history. This browser will forget the current copy, and it will be deleted later.",
  confirm: 'Start over',
  cancel: 'Keep this copy',
  poolEmpty:
    'All demo copies are in use right now. Please try again later. Your current copy is unchanged.',
  dailyLimit: 'This network has used its demo copies for today. Your current copy is unchanged.',
  rateLimited: 'Too many attempts. Please wait a minute and try again.',
  newCopy: 'You have a new copy.',
  connection: "Couldn't reach the server — check your connection and try again.",
  fallback: 'Something went wrong. Please try again.',
} as const;

const CLAIM = '*/bff/auth/demo/claim';
const KEY = 'azurebank.demoCopy';
const FIRST_COPY = 'demo-k7m2x9q4w8e1r5t3@azurebank.example';
const SECOND_COPY = 'demo-4h9d2s7f1g6j3k8a@azurebank.example';

/**
 * The API's refusal for an empty pool, with the API's own sentence: `PoolEmptyDetail` in
 * backend/src/AzureBank.Shared/Exceptions/DemoRefusalException.cs, as the day's limit further
 * down gives `DailyLimitDetail`. src/features/demo/demoContract.test.ts holds both against that
 * file.
 */
const poolEmpty = () =>
  problem({
    status: 429,
    errorCode: 'DEMO_POOL_EMPTY',
    detail: 'All demo copies are in use right now. Please try again later.',
    instance: '/api/auth/demo/claim',
  });

afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

/** Whose copy the browser keeps, read from its storage with nothing of the product in between. */
function keptAddress(): string | null {
  const raw = localStorage.getItem(KEY);
  return raw === null ? null : (JSON.parse(raw) as { email: string }).email;
}

/** What each alert inside `scope` says. */
function alertsIn(scope: HTMLElement): (string | null)[] {
  return Array.from(scope.querySelectorAll('[role="alert"]')).map((alert) => alert.textContent);
}

/**
 * How many elements on the page say "You have a new copy.". Counted, not looked up as one: the
 * toaster says a toast's words a second time, for half a second, in a live region of its own
 * beside the toast (@fluentui/react-toast, components/AriaLive/useAriaLive.js), so a query that
 * expects one element finds one or two depending on when it looks.
 */
function saysNewCopy(): number {
  return screen.queryAllByText(WORDS.newCopy).length;
}

/** What each button inside `scope` is called: its label where it has no text (the X). */
function buttonsIn(scope: HTMLElement): (string | null)[] {
  return within(scope)
    .getAllByRole('button')
    .map((button) => button.getAttribute('aria-label') ?? button.textContent);
}

/**
 * The dialog, open, beside the toasts' outlet, as a page that keeps it mounted would have it.
 *
 * `answer` is this test's answer to a claim. Without one, or when it returns nothing, the claim
 * goes on to the mock's own handler. Either way the claim is counted here first.
 */
function renderDialog(answer?: () => Response | Promise<Response> | undefined) {
  enableDemoMode();
  rememberDemoCopy(seedMockDemoCopy().copy);
  const claims = { sent: 0 };
  server.use(
    http.post(CLAIM, () => {
      claims.sent += 1;
      return answer?.();
    }),
  );
  const onClose = vi.fn();
  const onStartedOver = vi.fn();
  const ui = (isOpen: boolean) => (
    <>
      <AppToaster />
      <StartOverDialog isOpen={isOpen} onClose={onClose} onStartedOver={onStartedOver} />
    </>
  );
  const { store, rerender } = renderWithProviders(ui(true));
  const dialog = screen.getByRole('alertdialog');
  return {
    claims,
    onClose,
    onStartedOver,
    store,
    dialog,
    /** The element the dialog names as its description. */
    description: document.getElementById(dialog.getAttribute('aria-describedby') ?? ''),
    startOver: () => within(dialog).getByRole('button', { name: WORDS.confirm }),
    keepThisCopy: () => within(dialog).getByRole('button', { name: WORDS.cancel }),
    /** Closed and opened again by the page that keeps it. */
    openAgain: () => {
      rerender(ui(false));
      rerender(ui(true));
    },
    /**
     * Every claim the store has sent has its answer. A claim that a press set going is running by
     * the time the press returns, so a count read after this is a count that would have moved.
     */
    settled: () =>
      act(async () => {
        await Promise.all(store.dispatch(apiSlice.util.getRunningMutationsThunk()));
      }),
  };
}

describe('the dialog that asks before starting over', () => {
  it('asks its question in these words: the title, the message, the buttons', () => {
    const { dialog, description } = renderDialog();

    expect({
      headings: within(dialog)
        .getAllByRole('heading', { level: 2 })
        .map((heading) => heading.textContent),
      description: description?.textContent,
      buttons: buttonsIn(dialog),
    }).toStrictEqual({
      headings: [WORDS.title],
      description: WORDS.message,
      buttons: ['Close', WORDS.cancel, WORDS.confirm],
    });
    expect(dialog).toHaveAccessibleName(WORDS.title);
    expect(dialog).toHaveAccessibleDescription(WORDS.message);
  });

  it('"Keep this copy" sends nothing', async () => {
    // CONTROL: green before this change
    const { claims, onClose, keepThisCopy, settled } = renderDialog();

    await userEvent.click(keepThisCopy());
    await settled();

    expect({ claims: claims.sent, closed: onClose.mock.calls.length }).toStrictEqual({
      claims: 0,
      closed: 1,
    });
  });

  it('Escape sends nothing', async () => {
    // CONTROL: green before this change
    const { claims, onClose, settled } = renderDialog();

    await userEvent.keyboard('{Escape}');
    await settled();

    expect({ claims: claims.sent, closed: onClose.mock.calls.length }).toStrictEqual({
      claims: 0,
      closed: 1,
    });
  });

  it('"Start over" sends one claim', async () => {
    const { claims, startOver, settled } = renderDialog();

    await userEvent.click(startOver());
    await settled();

    expect(claims.sent).toBe(1);
  });

  it('a new copy: the dialog closes, the toast says so, the browser holds the new copy', async () => {
    const { dialog, onClose, onStartedOver, startOver, store } = renderDialog();
    const keptBefore = keptAddress();

    await userEvent.click(startOver());
    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
    const said = await screen.findAllByText(WORDS.newCopy);

    expect({
      keptBefore,
      kept: keptAddress(),
      signedInAs: store.getState().auth.user?.email,
      closed: onClose.mock.calls.length,
      toldItStartedOver: onStartedOver.mock.calls.length,
      alerts: alertsIn(dialog),
      saidInsideTheDialog: said.some((words) => dialog.contains(words)),
      // The dialog is kept mounted by its page, and the next time it is opened it has to ask
      // again: its confirm is "Start over" once more, not the wait's "Loading...".
      buttons: buttonsIn(dialog),
    }).toStrictEqual({
      keptBefore: FIRST_COPY,
      kept: SECOND_COPY,
      signedInAs: SECOND_COPY,
      closed: 1,
      toldItStartedOver: 1,
      alerts: [],
      saidInsideTheDialog: false,
      buttons: ['Close', WORDS.cancel, WORDS.confirm],
    });
    // Closed first: whoever is told that the visitor started over is told by a closed dialog.
    expect(onClose.mock.invocationCallOrder[0]).toBeLessThan(
      onStartedOver.mock.invocationCallOrder[0],
    );
  });

  it('all copies in use: its sentence, once, and the dialog stays', async () => {
    const { claims, dialog, onClose, onStartedOver, startOver } = renderDialog(poolEmpty);

    await userEvent.click(startOver());

    // Not the API's sentence alone: the dialog's own, which adds what became of the copy.
    await waitFor(() => expect(alertsIn(dialog)).toStrictEqual([WORDS.poolEmpty]));
    expect({
      claims: claims.sent,
      closed: onClose.mock.calls.length,
      toldItStartedOver: onStartedOver.mock.calls.length,
      kept: keptAddress(),
      saysNewCopy: saysNewCopy(),
      // Still a dialog to answer: every button can be pressed again.
      buttons: buttonsIn(dialog),
      disabled: within(dialog)
        .getAllByRole('button')
        .filter((button) => (button as HTMLButtonElement).disabled).length,
    }).toStrictEqual({
      claims: 1,
      closed: 0,
      toldItStartedOver: 0,
      kept: FIRST_COPY,
      saysNewCopy: 0,
      buttons: ['Close', WORDS.cancel, WORDS.confirm],
      disabled: 0,
    });
  });

  it("this network's copies are used up: its sentence, and no countdown", async () => {
    const { dialog, onClose, startOver } = renderDialog(() =>
      // The API's refusal, which names a wait in its body and in its header. The dialog counts
      // nothing down: the wait is the rest of the day.
      problem({
        status: 429,
        errorCode: 'DEMO_DAILY_LIMIT',
        detail: 'This network has used its demo copies for today. Please try again later.',
        instance: '/api/auth/demo/claim',
        extensions: { retryAfterSeconds: 600 },
        headers: { 'Retry-After': '600' },
      }),
    );

    await userEvent.click(startOver());

    await waitFor(() => expect(alertsIn(dialog)).toStrictEqual([WORDS.dailyLimit]));
    expect({
      timers: screen.queryAllByRole('timer').length,
      closed: onClose.mock.calls.length,
      kept: keptAddress(),
    }).toStrictEqual({ timers: 0, closed: 0, kept: FIRST_COPY });
  });

  it('too many attempts: its sentence', async () => {
    const { claims, dialog, startOver, store } = renderDialog();
    // The mock's own limiter, with its budget spent (`AUTH_PERMIT_LIMIT` in src/mocks/handlers.ts,
    // ten): it answers before the claim's handler takes a copy, in the limiter's own shape (a
    // `Retry-After` header and no wait in the body).
    mockState.authCallTimes = Array.from({ length: 10 }, () => Date.now());

    await userEvent.click(startOver());

    await waitFor(() => expect(alertsIn(dialog)).toStrictEqual([WORDS.rateLimited]));
    const refusals = Object.values(store.getState().api.mutations).map((entry) => entry?.error);
    expect({
      claims: claims.sent,
      // What the store kept of the refusal: the limiter's, with the header's wait read into it.
      refusals,
      timers: screen.queryAllByRole('timer').length,
      kept: keptAddress(),
    }).toStrictEqual({
      claims: 1,
      refusals: [
        expect.objectContaining({ errorCode: 'RATE_LIMIT_EXCEEDED', retryAfterSeconds: 60 }),
      ],
      timers: 0,
      kept: FIRST_COPY,
    });
  });

  it('the service does not answer: the outage sentence, and no word about the copy', async () => {
    const { dialog, startOver } = renderDialog(() =>
      serviceUnavailable({ via: 'bff', instance: '/bff/auth/demo/claim' }),
    );

    await userEvent.click(startOver());

    await waitFor(() => expect(alertsIn(dialog)).toStrictEqual([COPY.unavailable]));
    // After a claim with no answer nobody knows whether the copy changed, so nothing says it did not.
    expect(dialog.textContent).not.toContain('unchanged');
    expect(keptAddress()).toBe(FIRST_COPY);
  });

  it("no answer in 65 s: the outage sentence, not the connection's", async () => {
    const { dialog, startOver } = renderDialog(never);
    installFakeClock();
    const pressedAt = Date.now();
    await fakeClockUser().click(startOver());

    await advanceUntil(pressedAt, 65_100);

    await waitFor(() => expect(alertsIn(dialog)).toStrictEqual([COPY.unavailable]));
    expect({
      // The wait is over, and its hint went with it.
      hints: dialog.querySelectorAll('[data-wait-hint]').length,
      saysUnchanged: dialog.textContent?.includes('unchanged'),
    }).toStrictEqual({ hints: 0, saysUnchanged: false });
  });

  it('the connection fails: the connection sentence', async () => {
    const { dialog, startOver } = renderDialog(() => HttpResponse.error());

    await userEvent.click(startOver());

    await waitFor(() => expect(alertsIn(dialog)).toStrictEqual([WORDS.connection]));
    expect(dialog.textContent).not.toContain('unchanged');
  });

  it('an answer that cannot be read: the connection sentence', async () => {
    const { dialog, startOver } = renderDialog(
      () =>
        // A gateway's own error page: an answer came back, but not one the app can read.
        new HttpResponse('<html>Bad Gateway</html>', {
          status: 502,
          headers: { 'Content-Type': 'text/html' },
        }),
    );

    await userEvent.click(startOver());

    await waitFor(() => expect(alertsIn(dialog)).toStrictEqual([WORDS.connection]));
  });

  it('any other refusal: what the server said, or the fallback', async () => {
    const answers = [
      // A 429 that is none of the dialog's three: the API's refusal of a copy that has used up its
      // changes, with the API's sentence
      // (backend/src/AzureBank.Shared/Exceptions/DemoRefusalException.cs). The limiter's sentence
      // is for the limiter's code, not for every 429.
      () =>
        problem({
          status: 429,
          errorCode: 'DEMO_COPY_LIMIT',
          detail:
            'This demo copy has reached its limit of changes. Start over to get a fresh copy.',
          instance: '/api/auth/demo/claim',
        }),
      // The API's own 500, with its sentence.
      () =>
        problem({
          status: 500,
          errorCode: 'INTERNAL_ERROR',
          detail: 'An unexpected error occurred. Please try again later.',
        }),
      // A refusal that says nothing.
      () => new HttpResponse(null, { status: 500 }),
    ];
    const { claims, dialog, startOver } = renderDialog(() => answers.shift()?.());

    await userEvent.click(startOver());
    await waitFor(() =>
      expect(alertsIn(dialog)).toStrictEqual([
        'This demo copy has reached its limit of changes. Start over to get a fresh copy.',
      ]),
    );

    // Pressed again: each refusal takes the place of the one before it.
    await userEvent.click(startOver());
    await waitFor(() =>
      expect(alertsIn(dialog)).toStrictEqual([
        'An unexpected error occurred. Please try again later.',
      ]),
    );

    await userEvent.click(startOver());
    await waitFor(() => expect(alertsIn(dialog)).toStrictEqual([WORDS.fallback]));
    expect(claims.sent).toBe(3);
  });

  it('an answer the app will not accept: the fallback, and the copy kept is still the first', async () => {
    // Redux Toolkit reports an answer it could not transform on console.error, and this suite
    // fails any test that writes there (src/test/setup.ts). Here the refused answer is the
    // subject, so the report is stubbed, and counted.
    const report = vi.spyOn(console, 'error').mockImplementation(() => {});
    try {
      const { dialog, onClose, startOver } = renderDialog(() =>
        // A claim's answer with no copy in it: a sign-in's answer, which the claim's check refuses.
        HttpResponse.json({
          data: {
            user: {
              id: 'u2',
              email: SECOND_COPY,
              firstName: 'John',
              lastName: 'Smith',
              azureTag: 'john_p3x8',
              hasPin: true,
            },
            expiresAt: '2026-10-04T09:15:00.000Z',
          },
          message: 'Demo copy claimed',
        }),
      );

      await userEvent.click(startOver());

      await waitFor(() => expect(alertsIn(dialog)).toStrictEqual([WORDS.fallback]));
      expect({
        reports: report.mock.calls.length,
        closed: onClose.mock.calls.length,
        kept: keptAddress(),
        saysNewCopy: saysNewCopy(),
      }).toStrictEqual({ reports: 1, closed: 0, kept: FIRST_COPY, saysNewCopy: 0 });
    } finally {
      report.mockRestore();
    }
  });

  it('a slow claim says it is still waiting, outside the alert and the description', async () => {
    const { dialog, description, startOver } = renderDialog(never);
    installFakeClock();
    await fakeClockUser().click(startOver());
    const shownAt = await hintShownAt(dialog);
    const wordsBefore5s = dialog.querySelector('[data-wait-hint]')?.textContent;

    await advanceUntil(shownAt, 5_000);

    // In a status region of its own that holds those words and nothing else, and in no alert.
    const region = hintRegion(COPY.slow, dialog);
    expect({
      wordsBefore5s,
      insideTheDescription: description?.contains(region),
      description: description?.textContent,
      alerts: alertsIn(dialog),
      // Under the message, above the buttons.
      afterTheMessage:
        description !== null &&
        (description.compareDocumentPosition(region) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0,
      beforeTheButtons:
        (region.compareDocumentPosition(
          within(dialog).getByRole('button', { name: WORDS.cancel }),
        ) &
          Node.DOCUMENT_POSITION_FOLLOWING) !==
        0,
      // While it waits nothing in the dialog can be pressed, the confirm least of all: a second
      // press would be a second claim.
      buttons: buttonsIn(dialog),
      buttonsThatCanBePressed: within(dialog)
        .getAllByRole('button')
        .filter((button) => !(button as HTMLButtonElement).disabled).length,
    }).toStrictEqual({
      wordsBefore5s: '',
      insideTheDescription: false,
      description: WORDS.message,
      alerts: [],
      afterTheMessage: true,
      beforeTheButtons: true,
      buttons: ['Close', WORDS.cancel, 'Loading...'],
      buttonsThatCanBePressed: 0,
    });
    expect(dialog).toHaveAccessibleDescription(WORDS.message);
    expectNoNestedLiveRegions();

    // From 20 s, the words of a write that is not a money send, and no way to stop waiting: a
    // claim the server may already be making cannot be taken back by the page.
    await advanceUntil(shownAt, 20_000);
    hintRegion(COPY.stillTrying, dialog);
    expect(screen.queryByRole('button', { name: COPY.stopWaiting })).not.toBeInTheDocument();
  });

  it('no live region sits inside another', async () => {
    const { dialog, startOver } = renderDialog(poolEmpty);

    await userEvent.click(startOver());
    // With an error showing. The dialog's other live region, the wait's hint, is looked at where
    // it speaks: in the slow claim's test above.
    await waitFor(() => expect(alertsIn(dialog)).toStrictEqual([WORDS.poolEmpty]));

    expectNoNestedLiveRegions();
  });

  it('a refusal is not shown again when the dialog is opened again', async () => {
    const { dialog, onClose, startOver, keepThisCopy, openAgain } = renderDialog(poolEmpty);

    await userEvent.click(startOver());
    await waitFor(() => expect(alertsIn(dialog)).toStrictEqual([WORDS.poolEmpty]));
    const whileItWasRefused = alertsIn(dialog);
    await userEvent.click(keepThisCopy());
    openAgain();

    expect({
      whileItWasRefused,
      closed: onClose.mock.calls.length,
      afterItWasOpenedAgain: alertsIn(dialog),
    }).toStrictEqual({
      whileItWasRefused: [WORDS.poolEmpty],
      closed: 1,
      afterItWasOpenedAgain: [],
    });
  });
});
