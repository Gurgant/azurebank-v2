import { useState } from 'react';
import { makeStyles, tokens } from '@fluentui/react-components';
import { cleanup, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { emulateFocusFixup } from '../../test/outage';
import { renderWithProviders } from '../../test/renderWithProviders';
import { ConfirmDialog } from './ConfirmDialog';

/**
 * The first test this component has ever had, which is most of why the gap below survived.
 *
 * `ConfirmDialog` is the only hand-rolled modal in the app — every other dialog is a Fluent
 * `Dialog` and gets containment from tabster for free. This one declares `role="alertdialog"` and
 * `aria-modal="true"`, i.e. it PROMISES the rest of the page is unreachable, and then had an effect
 * labelled "Focus trap" that moved focus in once and never looked at Tab again. Focus walked
 * straight out into the page behind it.
 *
 * It is opened by the two transfer pages, as their leave prompt, and by the demo's "Start over"
 * (src/features/demo/StartOverDialog.tsx), so the page behind has live controls on it, and on a
 * transfer page it is a money surface. (Until 2026-10-05 this said it is reached from the
 * delete-account confirmation and from both transfer confirmations.)
 */

/** Renders the dialog with a focusable sibling, which is what focus escapes TO when it escapes. */
function renderOpen(props: Partial<Parameters<typeof ConfirmDialog>[0]> = {}) {
  const onClose = vi.fn();
  const onConfirm = vi.fn();
  const ui = (overrides: Partial<Parameters<typeof ConfirmDialog>[0]>) => (
    <>
      <button>outside before</button>
      <ConfirmDialog
        isOpen
        onClose={onClose}
        onConfirm={onConfirm}
        title="Delete account?"
        message="This can't be undone."
        confirmText="Delete"
        variant="danger"
        {...props}
        {...overrides}
      />
      <button>outside after</button>
    </>
  );
  const { rerender } = renderWithProviders(ui({}));
  return {
    onClose,
    onConfirm,
    /** Re-render with a changed prop, e.g. to end a loading state mid-test. */
    update: (overrides: Partial<Parameters<typeof ConfirmDialog>[0]>) => rerender(ui(overrides)),
  };
}

const dialog = () => screen.getByRole('alertdialog');
const closeButton = () => screen.getByRole('button', { name: 'Close' });
const cancelButton = () => screen.getByRole('button', { name: 'Cancel' });
const confirmButton = () => screen.getByRole('button', { name: 'Delete' });

/**
 * Where focus is, in words: inside the dialog, on the page (`body`, where a browser leaves focus
 * that nothing holds), or on the control outside the dialog that has it, by its text.
 *
 * The dialog is found in the DOM and not by its role: closed, it is hidden from the accessibility
 * tree, and a query by role would not find it.
 */
function focusIs(): string {
  const active = document.activeElement;
  if (document.querySelector('[role="alertdialog"]')?.contains(active)) return 'inside the dialog';
  if (active === null || active === document.body) return 'on the page';
  return `on "${active.textContent}"`;
}

/**
 * The properties an element's transitions run on, as jsdom hands the declarations back.
 *
 * jsdom keeps the `transition` shorthand and its longhands apart. The shorthand comes back as it
 * was written, empty when it was not. A longhand comes back as written, or at its initial value
 * (`all`, `0s`) when it was not, whatever the shorthand said. So both are read:
 *
 *   - the shorthand: the parts between its commas, each with its property first. A part that
 *     starts with anything but a name (a time, a token for one) names no property and runs on
 *     `all`;
 *   - the longhands: what `transition-property` names, when something gives it time to run in:
 *     a duration or a delay of its own, or the shorthand. A time with no property named anywhere
 *     runs on `all`. With no time from either, nothing runs, whatever is named.
 *
 * A time is any duration or delay but a zero: jsdom hands a token back as the `var()` it is, with
 * no number to read. An `all` that was declared is told from the initial one by the names jsdom
 * lists for the element, which are those that were declared.
 */
function transitionsOn(element: Element): string[] {
  const style = getComputedStyle(element);
  const whole = style.transition;
  const byTheShorthand =
    whole === '' || whole === 'none'
      ? []
      : whole
          // Not the commas inside a timing function's brackets.
          .split(/,(?![^(]*\))/)
          .map((part) => part.trim().split(/\s+/)[0])
          .map((first) => (/^-?[a-z][a-z-]*$/i.test(first) ? first : 'all'));
  const givesTime = (times: string) => times.split(',').some((time) => parseFloat(time) !== 0);
  const timed =
    byTheShorthand.length > 0 ||
    givesTime(style.transitionDuration) ||
    givesTime(style.transitionDelay);
  if (!timed) return [];
  if (Array.from(style).includes('transition-property')) {
    const named = style.transitionProperty.split(',').map((property) => property.trim());
    return [...byTheShorthand, ...named];
  }
  return byTheShorthand.length > 0 ? byTheShorthand : ['all'];
}

/** A transition on `visibility`, by its name or as one of `all`. */
const delaysVisibility = (element: Element) =>
  transitionsOn(element).some((property) => property === 'visibility' || property === 'all');

/**
 * The ways a component can declare a transition, each through the styling the dialog itself uses:
 * what `transitionsOn` has to be able to read.
 */
const useDeclared = makeStyles({
  nothing: {},
  whole: { transition: 'opacity 200ms ease, visibility 200ms ease' },
  wholeWithNoProperty: { transition: '150ms ease' },
  wholeWithATokenFirst: { transition: `${tokens.durationNormal} ease` },
  none: { transition: 'none' },
  byParts: { transitionProperty: 'all', transitionDuration: '150ms' },
  byPartsNamed: { transitionProperty: 'opacity, visibility', transitionDuration: '200ms' },
  aDelayAlone: { transitionDelay: '150ms' },
  aTimeFromAToken: { transitionProperty: 'visibility', transitionDuration: tokens.durationNormal },
  aPropertyAlone: { transitionProperty: 'visibility' },
  wholeAndAProperty: {
    transition: 'background-color 150ms ease',
    transitionProperty: 'visibility',
  },
  wholeAndAll: { transition: 'background-color 150ms ease', transitionProperty: 'all' },
});

/** One element for each of those ways, named by it. */
function Declared() {
  const styles = useDeclared();
  return (
    <>
      {(Object.keys(styles) as (keyof typeof styles)[]).map((way) => (
        <div key={way} data-declared={way} className={styles[way]} />
      ))}
    </>
  );
}

/**
 * The dialog as a page keeps it: mounted and closed, opened by a button of the page, closed by its
 * own cancel or by Escape, and made to wait by its confirm. `answered` is the answer arriving
 * while it waits, which closes it as a success does: nobody presses anything.
 * `closedStillWaiting` is a caller that closes it and has not yet put its own waiting flag down.
 */
function OpenedFromAButton({
  answered = false,
  closedStillWaiting = false,
}: {
  answered?: boolean;
  closedStillWaiting?: boolean;
}) {
  const [open, setOpen] = useState(false);
  const [waiting, setWaiting] = useState(false);
  return (
    <>
      <button onClick={() => setOpen(true)}>open the dialog</button>
      <ConfirmDialog
        isOpen={open && !answered && !closedStillWaiting}
        isLoading={waiting && !answered}
        onClose={() => setOpen(false)}
        onConfirm={() => setWaiting(true)}
        title="Delete account?"
        message="This can't be undone."
        confirmText="Delete"
      />
    </>
  );
}

describe('ConfirmDialog', () => {
  it('moves focus into the dialog when it opens', () => {
    renderOpen();
    // The close button is first in DOM order, so it is where focus lands.
    expect(closeButton()).toHaveFocus();
  });

  it('open, nothing from the control that takes focus up to the overlay delays its visibility', () => {
    /*
      The test above passes in jsdom whatever the styles say: jsdom gives focus to an element
      whatever its `visibility`. A browser refuses one whose computed `visibility` is `hidden`,
      which is how this dialog is hidden while it is closed. A transition that covers `visibility`,
      on the control or on an element it inherits the value from, leaves the control `hidden` at
      the instant the dialog opens and asks for focus, and focus then stays on the page behind.
      That focus is asserted in a browser by e2e/confirmDialog.spec.ts. This holds the cause, for
      each element between the one that took focus and the overlay, in both ways of opening.

      The closed overlay does delay it, and has to: that transition is what keeps the dialog on
      screen while it fades out.
    */
    // The overlay is the dialog's outermost element: the one that is hidden while it is closed.
    const overlayNow = () => document.querySelector('[role="alertdialog"]')?.parentElement ?? null;
    const nameOf = (element: Element) =>
      element === overlayNow()
        ? 'the overlay'
        : (element.getAttribute('aria-label') ??
          element.getAttribute('role') ??
          element.tagName.toLowerCase());
    // Every element from the one that has focus up to the overlay, and which of them delay it.
    const wayToTheOverlay = () => {
      const way: Element[] = [];
      for (let at = document.activeElement; at !== null; at = at.parentElement) {
        way.push(at);
        if (at === overlayNow()) break;
      }
      return {
        looked: way.map(nameOf),
        delayed: way
          .filter(delaysVisibility)
          .map((element) => `${nameOf(element)}: ${transitionsOn(element).join(', ')}`),
      };
    };

    const opened = renderOpen();
    const open = wayToTheOverlay();
    opened.update({ isOpen: false });
    const closedOverlay = overlayNow();
    const closedOverlayDelays = closedOverlay !== null && delaysVisibility(closedOverlay);
    cleanup();

    // Opened already waiting: every control is disabled, and the dialog itself takes focus.
    renderOpen({ isLoading: true });
    const openWaiting = wayToTheOverlay();

    expect({ open, closedOverlayDelays, openWaiting }).toStrictEqual({
      open: { looked: ['Close', 'div', 'alertdialog', 'the overlay'], delayed: [] },
      closedOverlayDelays: true,
      openWaiting: { looked: ['alertdialog', 'the overlay'], delayed: [] },
    });
  });

  it('reads what a transition runs on, declared whole, by its parts, or both ways', () => {
    /*
      The test above asserts that nothing is found, and is worth what its reading can see. Here
      the reading is given each way of declaring a transition, through the styling the dialog
      uses, and has to name the properties.

      What it takes as running is a property that something gives time to. A property named with
      no time is not one. A time with no property named runs on `all`; a delay counts as a time,
      and so does a time given by a token. Declared both ways, a property named by either counts:
      which of the two a browser lets win is not jsdom's to say.
    */
    renderWithProviders(<Declared />);
    const read = Object.fromEntries(
      Array.from(document.querySelectorAll('[data-declared]'), (element) => [
        element.getAttribute('data-declared'),
        transitionsOn(element),
      ]),
    );

    expect(read).toStrictEqual({
      nothing: [],
      whole: ['opacity', 'visibility'],
      wholeWithNoProperty: ['all'],
      wholeWithATokenFirst: ['all'],
      none: [],
      byParts: ['all'],
      byPartsNamed: ['opacity', 'visibility'],
      aDelayAlone: ['all'],
      aTimeFromAToken: ['visibility'],
      aPropertyAlone: [],
      wholeAndAProperty: ['background-color', 'visibility'],
      wholeAndAll: ['background-color', 'all'],
    });
  });

  it('wraps Tab from the last control back to the first', async () => {
    const user = userEvent.setup();
    renderOpen();

    await user.tab(); // Close -> Cancel
    expect(cancelButton()).toHaveFocus();
    await user.tab(); // Cancel -> Delete (the last one)
    expect(confirmButton()).toHaveFocus();

    await user.tab();

    expect(closeButton()).toHaveFocus();
    expect(dialog()).toContainElement(document.activeElement as HTMLElement);
  });

  it('wraps Shift+Tab from the first control back to the last', async () => {
    const user = userEvent.setup();
    renderOpen();
    expect(closeButton()).toHaveFocus();

    await user.tab({ shift: true });

    expect(confirmButton()).toHaveFocus();
  });

  it('never lets focus reach the page behind it', async () => {
    const user = userEvent.setup();
    renderOpen();

    // More presses than there are controls, in both directions: if containment is missing, one of
    // these lands on "outside after" or "outside before" and the dialog no longer contains focus.
    for (let i = 0; i < 8; i += 1) {
      await user.tab();
      expect(dialog()).toContainElement(document.activeElement as HTMLElement);
    }
    for (let i = 0; i < 8; i += 1) {
      await user.tab({ shift: true });
      expect(dialog()).toContainElement(document.activeElement as HTMLElement);
    }
  });

  it('wraps Tab by where it was pressed, also when a listener before the dialog has moved focus', async () => {
    /*
      Fluent's tabster hears Tab on the window, before React does. When the element that has focus
      is the last Tab stop of the whole document (backward: the first), it moves focus to an
      element of its own and leaves the rest to the browser's default. A trap that asks where
      focus is by then sees neither of its ends, prevents nothing, and focus leaves the dialog. A
      browser meets this on the transfer page, whose last control is this dialog's, and the wrap
      is asserted there by e2e/confirmDialog.spec.ts. Here a listener stands in for tabster: on
      Tab it moves focus to a control outside the dialog before the dialog hears the key.
    */
    const user = userEvent.setup();
    renderOpen();
    // The control that has focus, by its name: the X has a label, the others their words.
    const focusOn = () =>
      document.activeElement?.getAttribute('aria-label') ?? document.activeElement?.textContent;
    const movedTo: unknown[] = [];
    const moveFocusAway = (event: KeyboardEvent) => {
      if (event.key !== 'Tab') return;
      screen.getByText('outside after').focus();
      movedTo.push(focusOn());
    };
    window.addEventListener('keydown', moveFocusAway, true);
    try {
      confirmButton().focus();
      await user.tab();
      const forward = focusOn();

      closeButton().focus();
      await user.tab({ shift: true });
      const backward = focusOn();

      expect({ movedTo, forward, backward }).toStrictEqual({
        // The listener did move focus out, both times: the wrap is not a focus that never left.
        movedTo: ['outside after', 'outside after'],
        forward: 'Close',
        backward: 'Delete',
      });
    } finally {
      window.removeEventListener('keydown', moveFocusAway, true);
    }
  });

  it('keeps focus inside even while every control is disabled by isLoading', async () => {
    // isLoading disables the close, cancel and confirm buttons at once, so the dialog holds NO
    // focusable element. Tab must still not escape — the trap has to survive having nothing to
    // cycle to, which is the case a naive first/last implementation crashes or leaks on.
    const user = userEvent.setup();
    renderOpen({ isLoading: true });

    await user.tab();

    expect(screen.queryByText('outside after')).not.toHaveFocus();
    expect(screen.queryByText('outside before')).not.toHaveFocus();
  });

  /*
    THE CONTAINER PATH WITH ENABLED CONTROLS, which is the branch a first/last-only trap gets wrong
    and which nothing else here reaches.

    Opening while `isLoading` puts focus on the dialog container, because every control is disabled
    and there is nothing else to focus. When loading ends the controls come back — and focus is
    still on the container, which is neither the first nor the last of them. Forward, the browser
    would happen to do the right thing; BACKWARD it steps out of the subtree entirely, which is the
    leak.

    Mutation-checked, and the result says which of the two is load-bearing: deleting `|| onContainer`
    from both branches leaves the other eight tests AND the forward one green — only the Shift+Tab
    case turns red. That is the honest split. Forward, the browser reaches the first control on its
    own, so that test documents intent rather than catching regressions; backward is the one where
    removing the guard actually loses focus to the page behind, so it is the one holding the line.
  */
  it('sends Tab to the first control when focus sits on the container', async () => {
    const user = userEvent.setup();
    const { update } = renderOpen({ isLoading: true });
    expect(dialog()).toHaveFocus();

    update({ isLoading: false });
    await user.tab();

    expect(closeButton()).toHaveFocus();
  });

  it('sends Shift+Tab to the last control when focus sits on the container', async () => {
    const user = userEvent.setup();
    const { update } = renderOpen({ isLoading: true });
    expect(dialog()).toHaveFocus();

    update({ isLoading: false });
    await user.tab({ shift: true });

    expect(confirmButton()).toHaveFocus();
  });

  it('still closes on Escape', async () => {
    const user = userEvent.setup();
    const { onClose } = renderOpen();

    await user.keyboard('{Escape}');

    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('does not close on Escape while loading', async () => {
    const user = userEvent.setup();
    const { onClose } = renderOpen({ isLoading: true });

    await user.keyboard('{Escape}');

    expect(onClose).not.toHaveBeenCalled();
  });

  it('confirms and cancels through the buttons', async () => {
    const user = userEvent.setup();
    const { onClose, onConfirm } = renderOpen();

    await user.click(confirmButton());
    expect(onConfirm).toHaveBeenCalledTimes(1);

    await user.click(cancelButton());
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('closed, it takes no press: not on its buttons, not on its scrim, not from the keyboard', async () => {
    /*
      Closed, the dialog is still in the page, and still drawn for as long as it fades: the closed
      overlay keeps `visibility` in its transition (the test of the transitions above holds
      that). A press that lands on it then is a press on a dialog that has already answered. The
      second press of a double click on its confirm is one: the answer to the first has closed
      the dialog, and the button is under the pointer, enabled, until the fade ends.

      jsdom runs no transition and gives a press to a hidden element, so here the closed dialog
      can be pressed for good, which is the fade made as long as the test needs.
    */
    const user = userEvent.setup();
    const { onConfirm, onClose, update } = renderOpen();
    const presses = () => ({
      confirmed: onConfirm.mock.calls.length,
      closed: onClose.mock.calls.length,
    });
    // Closed, it is hidden from the accessibility tree, where its controls have no name to be
    // asked by: they are found in the DOM, by the label or the words each carries.
    const closed = (name: string) =>
      Array.from(document.querySelectorAll('[role="alertdialog"] button')).find(
        (button) => (button.getAttribute('aria-label') ?? button.textContent) === name,
      ) as HTMLButtonElement;
    const scrim = () => document.querySelector('[role="alertdialog"]')?.parentElement as Element;

    update({ isOpen: false });
    await user.click(closed('Delete'));
    await user.click(closed('Cancel'));
    await user.click(closed('Close'));
    await user.click(scrim());
    // And the key that presses a button: the confirm can still hold focus while it fades.
    closed('Delete').focus();
    const focusWasOnTheConfirm = document.activeElement === closed('Delete');
    await user.keyboard('{Enter}');
    const whileClosed = presses();

    // The same presses on the same elements once it is open again, so the zeros above are not
    // presses that never arrived.
    update({ isOpen: true });
    await user.click(confirmButton());
    await user.click(cancelButton());
    await user.click(closeButton());
    await user.click(scrim());
    confirmButton().focus();
    await user.keyboard('{Enter}');

    expect({ focusWasOnTheConfirm, whileClosed, openAgain: presses() }).toStrictEqual({
      focusWasOnTheConfirm: true,
      whileClosed: { confirmed: 0, closed: 0 },
      openAgain: { confirmed: 2, closed: 3 },
    });
  });

  it('shows what it is handed under its message, outside the alert and the description', () => {
    renderOpen({
      errorText: 'That could not be done.',
      children: <p>handed to the dialog</p>,
    });
    const handed = screen.queryByText('handed to the dialog');
    const alert = screen.getByRole('alert');
    // The description is whatever the dialog says describes it, not an id typed out here.
    const description = document.getElementById(dialog().getAttribute('aria-describedby') ?? '');
    // `later` comes after `earlier` in the document. True as well for an element INSIDE
    // `earlier`, which is why "inside" is asked separately below.
    const follows = (earlier: Element | null, later: Element | null) =>
      earlier !== null &&
      later !== null &&
      (earlier.compareDocumentPosition(later) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0;

    expect({
      inTheDialog: dialog().contains(handed),
      insideTheAlert: alert.contains(handed),
      insideTheDescription: description?.contains(handed),
      // What a screen reader is given as the description: the message, and no word more.
      saidAsTheDescription: description?.textContent,
      saidAsTheAlert: alert.textContent,
      afterTheMessage: follows(description, handed),
      afterTheAlert: follows(alert, handed),
      beforeTheButtons: follows(handed, cancelButton()),
      // In the box that holds the message, not in the row of buttons under it: the first child of
      // that row is also after the alert and before the buttons.
      besideTheMessage: handed !== null && handed.parentElement === description?.parentElement,
      inTheButtonsRow: cancelButton().parentElement?.contains(handed),
    }).toStrictEqual({
      inTheDialog: true,
      insideTheAlert: false,
      insideTheDescription: false,
      saidAsTheDescription: "This can't be undone.",
      saidAsTheAlert: 'That could not be done.',
      afterTheMessage: true,
      afterTheAlert: true,
      beforeTheButtons: true,
      besideTheMessage: true,
      inTheButtonsRow: false,
    });
  });

  it('gives its title a line of its own, with the message under it', () => {
    /*
      Fluent's `Text` draws inline whatever element it is asked for, a heading too. A title left
      inline shares its line with the message that follows it in the document, with nothing
      between the two ("Delete account?This can't be undone."), and the margin under it does
      nothing. jsdom lays nothing out, so no line can be measured here. This holds the cause: what
      the title's box is declared as, the space declared under it, and that the message is the
      element right after it.
    */
    renderOpen();
    const title = document.getElementById(dialog().getAttribute('aria-labelledby') ?? '');
    const description = document.getElementById(dialog().getAttribute('aria-describedby') ?? '');

    expect({
      title: title?.textContent,
      drawnAs: title ? getComputedStyle(title).display : null,
      spaceUnderIt: title ? getComputedStyle(title).marginBottom : null,
      thenTheMessage: title !== null && title.nextElementSibling === description,
    }).toStrictEqual({
      title: 'Delete account?',
      drawnAs: 'block',
      spaceUnderIt: '8px',
      thenTheMessage: true,
    });
  });

  /*
    A WAIT THAT STARTS UNDER THE VISITOR'S HANDS, which is not the wait the tests above open the
    dialog in.

    Opened already waiting, the dialog puts focus on itself, because it has no control to give it
    to. A wait that starts while the dialog is open starts on the confirm button that was just
    pressed: that button is disabled with the two others, and a browser hands a disabled control's
    focus to the page (src/test/outage.ts: measured in Chromium). From the page, Tab does not pass
    through the dialog's key handler at all, and the next stop is whatever the page behind has.

    jsdom leaves focus on the disabled button, which is not where a browser leaves it. Each test
    below that presses the confirm calls `emulateFocusFixup()`, the emulation of the browser's
    part, so the wait it describes starts where a visitor's does: with focus on the page.
  */
  it('keeps Tab inside when it starts waiting under the button that was pressed', async () => {
    const stopFixup = emulateFocusFixup();
    try {
      const user = userEvent.setup();
      const { onConfirm, update } = renderOpen();
      const pressed = confirmButton() as HTMLButtonElement;
      await user.click(pressed);
      const whenItWasPressed = focusIs();

      update({ isLoading: true });
      // The emulation hears of the disabled button a moment after the render that disabled it.
      await waitFor(() => expect(pressed).not.toHaveFocus());
      const whenItStartedWaiting = focusIs();
      await user.tab();

      expect({
        pressed: onConfirm.mock.calls.length,
        thePressedButtonIsDisabled: pressed.disabled,
        whenItWasPressed,
        whenItStartedWaiting,
        afterTab: focusIs(),
      }).toStrictEqual({
        pressed: 1,
        thePressedButtonIsDisabled: true,
        whenItWasPressed: 'inside the dialog',
        whenItStartedWaiting: 'inside the dialog',
        afterTab: 'inside the dialog',
      });
    } finally {
      stopFixup();
    }
  });

  it('gives focus back to the control that opened it, also after it waited', async () => {
    // CONTROL: green before this change
    const stopFixup = emulateFocusFixup();
    try {
      const user = userEvent.setup();
      /** Opens the dialog from the page's button, and says where focus went. */
      const open = async () => {
        await user.click(screen.getByRole('button', { name: 'open the dialog' }));
        return focusIs();
      };

      const closedByItsCancel = renderWithProviders(<OpenedFromAButton />);
      const whileOpen = await open();
      await user.click(cancelButton());
      const afterItsCancel = focusIs();
      closedByItsCancel.unmount();

      const closedByEscape = renderWithProviders(<OpenedFromAButton />);
      await open();
      await user.keyboard('{Escape}');
      const afterEscape = focusIs();
      closedByEscape.unmount();

      // A close that follows a wait: the confirm is pressed, the dialog waits, and the answer
      // closes it. The pressed button lost focus when the wait disabled it, so what the dialog
      // gives back is not focus it still holds.
      const closedByTheAnswer = renderWithProviders(<OpenedFromAButton />);
      await open();
      const pressed = confirmButton() as HTMLButtonElement;
      await user.click(pressed);
      await waitFor(() => expect(pressed).not.toHaveFocus());
      const thePressedButtonWasDisabled = pressed.disabled;
      closedByTheAnswer.rerender(<OpenedFromAButton answered />);
      const afterTheAnswer = focusIs();

      expect({
        whileOpen,
        afterItsCancel,
        afterEscape,
        thePressedButtonWasDisabled,
        afterTheAnswer,
      }).toStrictEqual({
        whileOpen: 'inside the dialog',
        afterItsCancel: 'on "open the dialog"',
        afterEscape: 'on "open the dialog"',
        thePressedButtonWasDisabled: true,
        afterTheAnswer: 'on "open the dialog"',
      });
    } finally {
      stopFixup();
    }
  });

  it('closed while its caller still says it waits, it does not take focus back', async () => {
    // Not a control. Without the effect that takes focus for a wait
    // (src/components/shared/ConfirmDialog.tsx) this fails on `whileItWaited`: focus is on the
    // page. Without the `isOpen &&` of that effect's condition it fails on `afterItWasClosed`:
    // focus is inside the closed dialog.
    //
    // The dialog takes focus when a wait starts while it is open. Closed, it is hidden and still
    // in the page: a wait that is still said to run must not pull focus into it, away from the
    // control that focus was just given back to.
    const stopFixup = emulateFocusFixup();
    try {
      const user = userEvent.setup();
      const { rerender } = renderWithProviders(<OpenedFromAButton />);
      await user.click(screen.getByRole('button', { name: 'open the dialog' }));
      const pressed = confirmButton() as HTMLButtonElement;
      await user.click(pressed);
      await waitFor(() => expect(pressed).not.toHaveFocus());
      const whileItWaited = focusIs();

      rerender(<OpenedFromAButton closedStillWaiting />);

      expect({
        whileItWaited,
        stillSaidToWait: pressed.disabled,
        afterItWasClosed: focusIs(),
      }).toStrictEqual({
        whileItWaited: 'inside the dialog',
        stillSaidToWait: true,
        afterItWasClosed: 'on "open the dialog"',
      });
    } finally {
      stopFixup();
    }
  });
});
