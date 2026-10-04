import { useState } from 'react';
import { fireEvent, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { enableDemoMode, resetDemoMode } from '../test/demoMode';
import { renderWithProviders } from '../test/renderWithProviders';
import { PinInput, type PinInputProps } from './PinInput';

/**
 * PR-10 — the reusable 6-box PIN entry. Pins the controlled compact-value contract:
 * per-box typing, paste distribution, backspace splice, and the onComplete latch.
 */

function Harness({ length = 6 }: { length?: number }) {
  const [value, setValue] = useState('');
  const [completed, setCompleted] = useState('');
  return (
    <div>
      <PinInput
        value={value}
        onChange={setValue}
        onComplete={setCompleted}
        length={length}
        ariaLabel="PIN"
      />
      <div data-testid="value">{value}</div>
      <div data-testid="completed">{completed}</div>
    </div>
  );
}

describe('PinInput (PR-10)', () => {
  it('renders one box per digit', () => {
    renderWithProviders(<Harness />);
    expect(screen.getAllByLabelText(/Digit \d of 6/)).toHaveLength(6);
  });

  it('distributes a pasted code across the boxes and fires onComplete', async () => {
    renderWithProviders(<Harness />);
    await userEvent.click(screen.getByLabelText('Digit 1 of 6'));
    await userEvent.paste('123456');

    expect(screen.getByLabelText('Digit 1 of 6')).toHaveValue('1');
    expect(screen.getByLabelText('Digit 6 of 6')).toHaveValue('6');
    expect(screen.getByTestId('value')).toHaveTextContent('123456');
    expect(screen.getByTestId('completed')).toHaveTextContent('123456');
  });

  it('accepts per-box typing and auto-advances', async () => {
    renderWithProviders(<Harness />);
    for (let i = 1; i <= 6; i++) {
      await userEvent.type(screen.getByLabelText(`Digit ${i} of 6`), String(i));
    }
    expect(screen.getByTestId('value')).toHaveTextContent('123456');
  });

  it('strips non-digits from a paste', async () => {
    renderWithProviders(<Harness />);
    await userEvent.click(screen.getByLabelText('Digit 1 of 6'));
    await userEvent.paste('12ab34');
    expect(screen.getByTestId('value')).toHaveTextContent('1234');
    expect(screen.getByTestId('completed')).toHaveTextContent('');
  });

  it('backspace removes the last digit', async () => {
    renderWithProviders(<Harness />);
    await userEvent.click(screen.getByLabelText('Digit 1 of 6'));
    await userEvent.paste('123');
    await userEvent.type(screen.getByLabelText('Digit 3 of 6'), '{backspace}');
    expect(screen.getByTestId('value')).toHaveTextContent('12');
  });

  it('never drops a keystroke typed into an empty out-of-range box (append, not ignore)', async () => {
    // Regression: with the value empty, typing into box 4 must register (append to the
    // first empty slot) rather than being silently discarded — the wrong-PIN retry path.
    renderWithProviders(<Harness />);
    await userEvent.type(screen.getByLabelText('Digit 4 of 6'), '7');
    expect(screen.getByTestId('value')).toHaveTextContent('7');
  });

  it('distributes a multi-digit value from a single change event (OTP / password-manager autofill)', () => {
    // Autofill delivers the whole code via one input event (not a paste) — it must not be
    // truncated to the last digit.
    renderWithProviders(<Harness />);
    fireEvent.change(screen.getByLabelText('Digit 1 of 6'), { target: { value: '123456' } });
    expect(screen.getByTestId('value')).toHaveTextContent('123456');
    expect(screen.getByTestId('completed')).toHaveTextContent('123456');
  });

  it('masks digits by default and reveals them on the toggle', async () => {
    renderWithProviders(<Harness />);
    expect(screen.getByLabelText('Digit 1 of 6')).toHaveAttribute('type', 'password');
    /*
      The name says the state, so nothing else may. With aria-pressed as well, the button was
      announced "Hide PIN, pressed" once revealed: a toggle that is pressed to hide, which reads as
      the PIN being hidden when it is the opposite.
    */
    const show = screen.getByRole('button', { name: 'Show PIN' });
    expect(show).not.toHaveAttribute('aria-pressed');
    await userEvent.click(show);
    expect(screen.getByLabelText('Digit 1 of 6')).toHaveAttribute('type', 'text');
    expect(screen.getByRole('button', { name: 'Hide PIN' })).not.toHaveAttribute('aria-pressed');
  });

  /*
    A whole PIN arriving at once is a whole PIN, whichever box has focus and whatever is already
    there. Spliced in at the box, a paste after a mistyped start kept the start: on main, "12" then
    "123456" pasted at box 3 read 121234, and a PIN manager's autofill into box 2 of a full value
    read 198765. A shorter paste still goes in where the caret is.
  */
  it('replaces a partial entry with a whole pasted PIN', async () => {
    renderWithProviders(<Harness />);
    await userEvent.click(screen.getByLabelText('Digit 1 of 6'));
    await userEvent.paste('12');

    await userEvent.click(screen.getByLabelText('Digit 3 of 6'));
    await userEvent.paste('123456');

    expect(screen.getByTestId('value')).toHaveTextContent('123456');
    expect(screen.getByTestId('completed')).toHaveTextContent('123456');
  });

  it('replaces a complete entry with a whole PIN pasted into any box', async () => {
    renderWithProviders(<Harness />);
    await userEvent.click(screen.getByLabelText('Digit 1 of 6'));
    await userEvent.paste('123456');

    await userEvent.click(screen.getByLabelText('Digit 4 of 6'));
    await userEvent.paste('654321');

    expect(screen.getByTestId('value')).toHaveTextContent('654321');
  });

  it('replaces what is there with a whole autofilled PIN', async () => {
    renderWithProviders(<Harness />);
    await userEvent.click(screen.getByLabelText('Digit 1 of 6'));
    await userEvent.paste('123456');

    fireEvent.change(screen.getByLabelText('Digit 2 of 6'), { target: { value: '987654' } });

    expect(screen.getByTestId('value')).toHaveTextContent('987654');
  });
});

/*
  On the demo, boxes that ask for a PIN the visitor already has are told apart from boxes where a
  PIN is being chosen: under the first kind the page prints the PIN every demo copy starts with,
  and the boxes say they are described by it.

  The sentence is typed out here and not imported from the product, so a test fails the day the
  words on screen are no longer these. It ends on a full stop.
*/
const DEMO_PIN_HINT = 'Demo PIN: 123456, unless you changed it.';

/** What a caller's own description reads, here: the element the boxes are told to point at. */
const REFUSED = 'Incorrect PIN. Please try again.';
const WHAT_TO_DO = 'Enter your 6-digit PIN.';

/**
 * Boxes named "PIN", beside the two elements a caller's description may name. Nothing typed into
 * them is kept: these tests read what is drawn around the boxes.
 */
function Boxes(props: Pick<PinInputProps, 'purpose' | 'ariaDescribedBy'>) {
  return (
    <>
      <p id="what-to-do">{WHAT_TO_DO}</p>
      <PinInput value="" onChange={() => {}} ariaLabel="PIN" {...props} />
      <p id="why-it-was-refused">{REFUSED}</p>
    </>
  );
}

const boxes = () => screen.getByRole('group', { name: 'PIN' });

/** Every hint on the page. None is `[]`, not an error. */
const hints = () => screen.queryAllByText(DEMO_PIN_HINT);

/** The boxes' `aria-describedby` as it is written; `null` when they carry none. */
const describedByAttribute = () => boxes().getAttribute('aria-describedby');

/**
 * What the boxes are described by, id by id: the words of the element each id names, and `null`
 * for an id that names nothing on the page. `[]` when the boxes carry no description.
 */
function described(): (string | null)[] {
  const attribute = describedByAttribute();
  if (attribute === null) return [];
  return attribute.split(' ').map((id) => document.getElementById(id)?.textContent ?? null);
}

/**
 * The column the boxes stand in, top to bottom: the boxes and a button by their names, anything
 * else by its words.
 */
function column(): string[] {
  return Array.from(boxes().parentElement?.children ?? []).map((child) => {
    if (child.getAttribute('role') === 'group') return `boxes: ${child.getAttribute('aria-label')}`;
    if (child.tagName === 'BUTTON') return `button: ${child.getAttribute('aria-label')}`;
    return child.textContent ?? '';
  });
}

/** Draws the boxes, reads how many hints the page has and what the boxes point at, and unmounts. */
function look(props: Pick<PinInputProps, 'purpose' | 'ariaDescribedBy'> = {}) {
  const view = renderWithProviders(<Boxes {...props} />);
  const seen = { hints: hints().length, describedBy: describedByAttribute() };
  view.unmount();
  return seen;
}

describe('PinInput on the demo', () => {
  it('in the demo the boxes of an existing PIN are described by the demo PIN', () => {
    enableDemoMode();
    renderWithProviders(<Boxes />);

    expect({ hints: hints().length, column: column(), described: described() }).toEqual({
      hints: 1,
      // Between the boxes and the button that shows the digits.
      column: ['boxes: PIN', DEMO_PIN_HINT, 'button: Show PIN'],
      described: [DEMO_PIN_HINT],
    });
  });

  it("the caller's description is kept beside the hint", () => {
    enableDemoMode();

    const one = renderWithProviders(<Boxes ariaDescribedBy="why-it-was-refused" />);
    const withOne = { attribute: describedByAttribute(), described: described() };
    one.unmount();

    // A caller may pass a list of ids as well as one (src/pages/TransferPage.tsx does).
    renderWithProviders(<Boxes ariaDescribedBy="what-to-do why-it-was-refused" />);
    const withAList = { attribute: describedByAttribute(), described: described() };

    // The caller's ids as the caller wrote them, then one more; and every id names an element.
    expect({ withOne, withAList }).toEqual({
      withOne: {
        attribute: expect.stringMatching(/^why-it-was-refused \S+$/),
        described: [REFUSED, DEMO_PIN_HINT],
      },
      withAList: {
        attribute: expect.stringMatching(/^what-to-do why-it-was-refused \S+$/),
        described: [WHAT_TO_DO, REFUSED, DEMO_PIN_HINT],
      },
    });
  });

  it("no hint without the tag, and the description is the caller's alone", () => {
    const withoutTheTag = {
      passedOne: look({ ariaDescribedBy: 'why-it-was-refused' }),
      passedNone: look(),
    };

    // A tag that is there and does not say `true` is not the demo.
    document.head.insertAdjacentHTML('beforeend', '<meta name="azurebank-demo" content="false">');
    const withATagThatSaysFalse = look();
    resetDemoMode();

    // The same boxes on the demo, so that the zeros above are counts of something that is drawn.
    enableDemoMode();
    const withTheTag = look().hints;

    expect({ withoutTheTag, withATagThatSaysFalse, withTheTag }).toEqual({
      withoutTheTag: {
        passedOne: { hints: 0, describedBy: 'why-it-was-refused' },
        passedNone: { hints: 0, describedBy: null },
      },
      withATagThatSaysFalse: { hints: 0, describedBy: null },
      withTheTag: 1,
    });
  });

  it('no hint where a new PIN is chosen', () => {
    enableDemoMode();

    expect({
      chosen: look({ purpose: 'new' }),
      chosenWithADescription: look({ purpose: 'new', ariaDescribedBy: 'why-it-was-refused' }),
      // The same boxes asking for a PIN the visitor has, so that the zeros are counts of something.
      asked: look({ purpose: 'existing' }).hints,
    }).toEqual({
      chosen: { hints: 0, describedBy: null },
      chosenWithADescription: { hints: 0, describedBy: 'why-it-was-refused' },
      asked: 1,
    });
  });
});
