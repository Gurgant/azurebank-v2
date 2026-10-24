import { isInaccessible, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { mockState } from '../mocks/state';
import { renderWithProviders } from '../test/renderWithProviders';
import { AccountNumberField } from './AccountNumberField';

const MAIN = 'Main Account';
const MASKED = 'AB-••••-••••-90';
const FULL = 'AB-1234-5678-90';
const LABEL = 'Account ending in 90';

/** Main Account as the list hands it over: masked by the API, ending in 90. */
function mainAccount() {
  const account = mockState.accounts.find((a) => a.name === MAIN);
  if (!account) throw new Error(`the mock has no ${MAIN}`);
  return account;
}

/** The field's row: the box that holds the number, and beside it the box of its buttons. */
function row(buttonName: string) {
  return screen.getByRole('button', { name: buttonName }).parentElement
    ?.parentElement as HTMLElement;
}

/**
 * The text of `root` that a screen reader goes through: its text nodes, less those inside an
 * element taken out of the accessibility tree. A name given with `aria-label` is not in it, and on
 * an element with no role, as this number is, ARIA allows none: the text under it is what is read.
 */
function spoken(root: Element): string {
  const words: string[] = [];
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
  for (let node = walker.nextNode(); node; node = walker.nextNode()) {
    if (node.parentElement && !isInaccessible(node.parentElement)) {
      words.push(node.textContent ?? '');
    }
  }
  return words.join('');
}

/** Reveals Main Account's number on a session that is already elevated, so no PIN is asked. */
async function revealed() {
  mockState.authLevel = 2;
  const user = userEvent.setup();
  renderWithProviders(<AccountNumberField account={mainAccount()} />);
  await user.click(screen.getByRole('button', { name: `Reveal full account number for ${MAIN}` }));
  return screen.findByText(FULL);
}

describe('AccountNumberField', () => {
  it(`a masked number is read as "${LABEL}", and its bullets are not read`, () => {
    renderWithProviders(<AccountNumberField account={mainAccount()} />);

    const bullets = screen.getByText(MASKED);
    const label = screen.queryByText(LABEL);

    expect({
      spoken: spoken(row(`Reveal full account number for ${MAIN}`)),
      bulletsRead: !isInaccessible(bullets),
      labelRead: label !== null && !isInaccessible(label),
      // Out of the flow and clipped to nothing: it takes no room beside the bullets and draws
      // nothing, and stays in the accessibility tree, as `display: none` would not.
      labelDrawn: label && {
        position: getComputedStyle(label).position,
        width: getComputedStyle(label).width,
        height: getComputedStyle(label).height,
        overflow: getComputedStyle(label).overflow,
      },
    }).toStrictEqual({
      spoken: LABEL,
      bulletsRead: false,
      labelRead: true,
      labelDrawn: { position: 'absolute', width: '1px', height: '1px', overflow: 'hidden' },
    });
  });

  it('a revealed number is read as its digits, with no label over them', async () => {
    const digits = await revealed();

    expect({
      spoken: spoken(row(`Hide account number for ${MAIN}`)),
      digitsRead: !isInaccessible(digits),
      named: digits.getAttribute('aria-label'),
      label: screen.queryByText(LABEL),
    }).toStrictEqual({ spoken: FULL, digitsRead: true, named: null, label: null });
  });

  /*
    jsdom lays nothing out, so this holds the rules and not what they draw. Measured in
    Chromium on 2026-10-24, before them: the revealed number broke at its hyphens, onto two lines at
    320 px, and with "Copied" beside it onto two at 375 px and four at 320 px. With them it keeps
    to one line, and the buttons move under it when the card has no room for both. The eye and
    copy share a box that does not wrap, so they move together, never one beside the number and
    the other under it.
  */
  it('the number keeps to one line, and its two buttons may move under it together', async () => {
    const digits = await revealed();
    const eye = screen.getByRole('button', { name: `Hide account number for ${MAIN}` });
    const copy = screen.getByRole('button', { name: `Copy account number for ${MAIN}` });
    const buttons = eye.parentElement as HTMLElement;
    const fieldRow = row(`Hide account number for ${MAIN}`);

    expect({
      number: getComputedStyle(digits).whiteSpace,
      row: getComputedStyle(fieldRow).flexWrap,
      rowHoldsNumber: fieldRow.contains(digits),
      copyBesideEye: copy.parentElement === buttons,
      buttons: getComputedStyle(buttons).flexWrap,
    }).toStrictEqual({
      number: 'nowrap',
      row: 'wrap',
      rowHoldsNumber: true,
      copyBesideEye: true,
      buttons: 'nowrap',
    });
  });
});
