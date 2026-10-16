import { createRef } from 'react';
import { act, cleanup, fireEvent, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { renderWithProviders } from '../../test/renderWithProviders';
import { COPY, installFakeClock } from '../../test/outage';
import { settle } from '../../test/settle';
import { expectNoNestedLiveRegions } from '../../test/liveRegions';
import { offersFor, type MovementCheck, type MovementVerdict } from '../../hooks/useMovementCheck';
import { ResultUnknownView } from './ResultUnknownView';

/**
 * `transfer-behaviour.test.tsx` already pins this screen through BOTH transfer flows. This pins the
 * component itself, which is a different obligation: the flows prove the screen appears with no way
 * out, and these prove the component cannot grow one.
 *
 * The distinction matters because the absence of exits here is the safety property. A future
 * `onClose` prop, or a `PageHeader` that starts defaulting to a Back button, would pass every
 * flow-level test that asserts the two forward actions exist while quietly letting a user walk away
 * from a transfer that may or may not have taken their money.
 */
describe('ResultUnknownView', () => {
  const render = (overrides: Partial<Parameters<typeof ResultUnknownView>[0]> = {}) =>
    renderWithProviders(
      <ResultUnknownView
        title="Send Money"
        repeatWarning="send twice"
        onCheckTransactions={() => {}}
        onStartOver={() => {}}
        {...overrides}
      />,
    );

  it('offers exactly two actions and no way to leave', () => {
    render();

    const buttons = screen.getAllByRole('button');
    expect(buttons.map((b) => b.textContent)).toEqual([
      'Check recent transactions',
      "It didn't go through — start over",
    ]);
    // Named individually too: the array above would still pass if a Back button were added with an
    // icon and no text content.
    expect(screen.queryByRole('button', { name: 'Back' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Close' })).not.toBeInTheDocument();
  });

  it('never navigates or resets on its own — both actions are the caller’s', async () => {
    // The invariant PageHeader and MoneyDialogShell also state. `onStartOver` has to re-arm the
    // idempotency intent, which only the owning flow's wizard can do; a component that called
    // navigate() here would strand the flow with a latched verifyRequired.
    const onCheckTransactions = vi.fn();
    const onStartOver = vi.fn();
    render({ onCheckTransactions, onStartOver });

    await settle();
    await userEvent.click(screen.getByRole('button', { name: 'Check recent transactions' }));
    expect(onCheckTransactions).toHaveBeenCalledTimes(1);

    await userEvent.click(screen.getByRole('button', { name: /didn't go through/ }));
    expect(onStartOver).toHaveBeenCalledTimes(1);
  });

  it('says the caller’s words, so neither flow inherits the other’s copy', () => {
    // Passed as words rather than a flow discriminator: the sentence is assembled once and neither
    // flow can grow a branch the other cannot see.
    render({ title: 'Move Money', repeatWarning: 'move the money twice' });

    expect(screen.getByText(/retrying blindly could move the money twice/)).toBeInTheDocument();
    expect(screen.queryByText(/could send twice/)).not.toBeInTheDocument();
  });
});

/**
 * The view once it asks the listing by key (ADR-0022): which actions it draws for each verdict,
 * which one leads, and what its status region says. The actions follow `offersFor`, the rule the
 * hook hands every view, so a check is built here as the hook would build it.
 */
describe('ResultUnknownView, with the listing by key', () => {
  function check(
    verdict: MovementVerdict,
    {
      canCheckAgain = true,
      pastLive = false,
    }: { canCheckAgain?: boolean; pastLive?: boolean } = {},
  ): MovementCheck {
    return {
      verdict,
      movement: null,
      canCheckAgain,
      pastLive,
      checking: verdict === 'looking',
      requestId: '1',
      checkAgain: vi.fn(),
      landingRef: createRef<HTMLButtonElement>(),
      ...offersFor(verdict, canCheckAgain, pastLive),
    };
  }

  const renderWith = (movementCheck: MovementCheck) =>
    renderWithProviders(
      <ResultUnknownView
        title="Send Money"
        repeatWarning="send twice"
        onCheckTransactions={() => {}}
        onStartOver={() => {}}
        check={movementCheck}
      />,
    );

  const START_OVER = "It didn't go through — start over";
  const CHECK = 'Check recent transactions';
  const AGAIN = 'Check again';
  const BODY = /may or may not have gone through/;

  /** The view's own status region: the one whose words are the verdict, not the wait's hint. */
  const verdictRegion = () => {
    const regions = Array.from(document.querySelectorAll<HTMLElement>('[role="status"]')).filter(
      (region) => region.closest('[data-wait-hint]') === null,
    );
    expect(regions).toHaveLength(1);
    return regions[0];
  };

  it.each<[string, MovementCheck, string[], string, boolean]>([
    ['while it asks', check('looking'), [AGAIN, CHECK], '', true],
    ['not in the listing yet', check('notYet'), [AGAIN, CHECK], COPY.movementNotYet, true],
    [
      'not in the listing, once nothing can commit',
      check('none', { pastLive: true }),
      [CHECK, START_OVER],
      COPY.movementNone,
      false,
    ],
    [
      'the listing could not answer, before the bound',
      check('unchecked'),
      [AGAIN, CHECK],
      COPY.movementUncheckedNotYet,
      true,
    ],
    [
      'the account is closed, before the bound',
      check('unchecked', { canCheckAgain: false }),
      [CHECK],
      COPY.movementUncheckedNotYet,
      true,
    ],
    [
      'the listing could not answer, after the bound',
      check('unchecked', { pastLive: true }),
      [CHECK, AGAIN, START_OVER],
      COPY.movementUnchecked,
      true,
    ],
    [
      'the account is closed, after the bound',
      check('unchecked', { canCheckAgain: false, pastLive: true }),
      [CHECK, START_OVER],
      COPY.movementUnchecked,
      true,
    ],
  ])(
    '%s: draws its actions, the primary first, and says the verdict in its own region',
    (_name, movementCheck, buttons, words, body) => {
      renderWith(movementCheck);

      const drawn = screen.getAllByRole('button');
      expect(drawn.map((button) => button.textContent)).toEqual(buttons);
      // The first is the primary, and where a "Check again" lands: the hook's landing is on it.
      expect(movementCheck.landingRef.current).toBe(drawn[0]);
      expect(screen.queryByRole('button', { name: 'Back' })).not.toBeInTheDocument();
      expect(screen.queryByRole('button', { name: 'Close' })).not.toBeInTheDocument();

      expect(verdictRegion()).toHaveTextContent(words === '' ? /^$/ : words);
      expect(verdictRegion()).toHaveAttribute('aria-atomic', 'true');
      expect(verdictRegion().closest('[role="alert"]')).toBeNull();
      expect(screen.queryByText(BODY) !== null).toBe(body);
      expectNoNestedLiveRegions();
    },
  );

  it('is on screen and empty while it asks, with the hint of the read beside it and "Check again" disabled', () => {
    renderWith(check('looking'));

    expect(verdictRegion()).toBeEmptyDOMElement();
    expect(document.querySelector('[data-wait-hint]')).not.toBeNull();
    expect(screen.getByRole('button', { name: AGAIN })).toBeDisabled();
    expect(screen.queryByRole('button', { name: /didn't go through/ })).not.toBeInTheDocument();
  });

  it('"Check again" asks again, and nothing else', async () => {
    const notYet = check('notYet');
    renderWith(notYet);

    await settle();
    await userEvent.click(screen.getByRole('button', { name: AGAIN }));

    expect(notYet.checkAgain).toHaveBeenCalledTimes(1);
  });

  it('with nothing to ask about it is the view it always was, with no region', () => {
    renderWith(check('off'));

    expect(screen.getAllByRole('button').map((button) => button.textContent)).toEqual([
      CHECK,
      START_OVER,
    ]);
    expect(screen.queryByRole('status')).not.toBeInTheDocument();
  });
});

/**
 * A press aimed at what the view drew before (`useSettledPress`): the second press of a double
 * click, or one made as the verdict changes the buttons under it. The view's actions drop a press
 * for 500 ms after the view appears and after each change of verdict, and stay enabled and named
 * while they do.
 */
describe('ResultUnknownView, pressed before it has settled', () => {
  // Unmount first, then restore the clock: withdraw.test.tsx has the reason.
  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  const check = (verdict: MovementVerdict, pastLive: boolean): MovementCheck => ({
    verdict,
    movement: null,
    canCheckAgain: true,
    pastLive,
    checking: verdict === 'looking',
    requestId: '1',
    checkAgain: vi.fn(),
    landingRef: createRef<HTMLButtonElement>(),
    ...offersFor(verdict, true, pastLive),
  });

  const advance = (ms: number) => act(() => void vi.advanceTimersByTime(ms));

  it('drops a press for 500 ms after the view appears and after its verdict changes, and acts on one after', () => {
    installFakeClock();
    const onCheckTransactions = vi.fn();
    const onStartOver = vi.fn();
    const view = (movementCheck: MovementCheck) => (
      <ResultUnknownView
        title="Send Money"
        repeatWarning="send twice"
        onCheckTransactions={onCheckTransactions}
        onStartOver={onStartOver}
        check={movementCheck}
      />
    );
    const { rerender } = renderWithProviders(view(check('notYet', false)));
    const checkTransactions = () =>
      screen.getByRole('button', { name: 'Check recent transactions' });

    fireEvent.click(checkTransactions());
    advance(400);
    fireEvent.click(checkTransactions());
    expect(onCheckTransactions).not.toHaveBeenCalled();
    // Neither disabled nor renamed while it waits: a screen reader hears the same button.
    expect(checkTransactions()).toBeEnabled();
    expect(checkTransactions()).not.toHaveAttribute('aria-disabled');

    advance(100);
    fireEvent.click(checkTransactions());
    expect(onCheckTransactions).toHaveBeenCalledTimes(1);

    // At the bound the verdict changes and the new-key action appears: the wait starts again.
    rerender(view(check('none', true)));
    const startOver = screen.getByRole('button', { name: "It didn't go through — start over" });
    fireEvent.click(startOver);
    advance(400);
    fireEvent.click(startOver);
    fireEvent.click(checkTransactions());
    expect(onStartOver).not.toHaveBeenCalled();
    expect(onCheckTransactions).toHaveBeenCalledTimes(1);

    advance(100);
    fireEvent.click(startOver);
    expect(onStartOver).toHaveBeenCalledTimes(1);
  });

  it('starts the wait again when only `canCheckAgain` changes, which takes "Check again" away', () => {
    /*
      "Check recent transactions" then moves up to where "Check again" was. Today the hook draws
      "looking" between two verdicts, which restarts the wait as well; the view does not rely on
      that.
    */
    installFakeClock();
    const onCheckTransactions = vi.fn();
    const unchecked = (canCheckAgain: boolean): MovementCheck => ({
      ...check('unchecked', false),
      canCheckAgain,
      ...offersFor('unchecked', canCheckAgain, false),
    });
    const view = (movementCheck: MovementCheck) => (
      <ResultUnknownView
        title="Send Money"
        repeatWarning="send twice"
        onCheckTransactions={onCheckTransactions}
        onStartOver={() => {}}
        check={movementCheck}
      />
    );
    const names = () => screen.getAllByRole('button').map((button) => button.textContent);
    const { rerender } = renderWithProviders(view(unchecked(true)));
    advance(500);
    expect(names()).toEqual(['Check again', 'Check recent transactions']);

    rerender(view(unchecked(false)));
    expect(names()).toEqual(['Check recent transactions']);
    const checkTransactions = screen.getByRole('button', { name: 'Check recent transactions' });
    fireEvent.click(checkTransactions);
    advance(499);
    fireEvent.click(checkTransactions);
    expect(onCheckTransactions).not.toHaveBeenCalled();

    advance(1);
    fireEvent.click(checkTransactions);
    expect(onCheckTransactions).toHaveBeenCalledTimes(1);
  });
});
