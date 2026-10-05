import { readFileSync, readdirSync } from 'node:fs';
import { useState } from 'react';
import { fireEvent, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { getDemoCopySnapshot } from '../features/demo/demoCopyStorage';
import { enableDemoMode, rememberDemoCopy, resetDemoMode } from '../test/demoMode';
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
function Boxes(props: Pick<PinInputProps, 'purpose' | 'ariaDescribedBy' | 'error' | 'disabled'>) {
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

/**
 * The column with no line in it: the boxes, then the button that shows the digits. Read as well as
 * the count of hints, which finds the line by its words: an element with no words, left in the
 * line's place, is counted by nothing and is one more entry here.
 */
const BARE = ['boxes: PIN', 'button: Show PIN'];

/**
 * Draws the boxes, reads how many hints the page has, what stands in the boxes' column and what
 * the boxes point at, and unmounts.
 */
function look(props: Pick<PinInputProps, 'purpose' | 'ariaDescribedBy'> = {}) {
  const view = renderWithProviders(<Boxes {...props} />);
  const seen = { hints: hints().length, column: column(), describedBy: describedByAttribute() };
  view.unmount();
  return seen;
}

/** Every `.tsx` file under `dir` that is not a test, with forward slashes on any machine. */
function componentFiles(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = `${dir}/${entry.name}`;
    if (entry.isDirectory()) return componentFiles(path);
    return entry.name.endsWith('.tsx') && !entry.name.includes('.test.') ? [path] : [];
  });
}

/**
 * The `<PinInput … />` elements in a file, comments left out, each from its `<` to the `/>` that
 * closes it. That is the first `/>` outside every pair of braces: a property's value has arrow
 * functions and braces of its own.
 */
function boxesDrawnIn(file: string): string[] {
  const code = readFileSync(file, 'utf8')
    .replace(/\/\*[\s\S]*?\*\//g, '')
    .replace(/^\s*\/\/.*$/gm, '');
  return [...code.matchAll(/<PinInput\s/g)].map(({ index }) => {
    let depth = 0;
    let end = index;
    while (end < code.length && !(depth === 0 && code.startsWith('/>', end))) {
      if (code[end] === '{') depth += 1;
      if (code[end] === '}') depth -= 1;
      end += 1;
    }
    return code.slice(index, end + 2);
  });
}

describe('PinInput on the demo', () => {
  it('in the demo the boxes of an existing PIN are described by the demo PIN', () => {
    enableDemoMode();
    renderWithProviders(<Boxes />);
    const [line] = hints();

    expect({
      hints: hints().length,
      column: column(),
      described: described(),
      // Words to read and nothing more: the line speaks only when the boxes are asked about, it
      // is no stop for the Tab key, and it is hidden from nobody. Were it an alert, it would
      // speak beside the caller's own refusal after a wrong PIN.
      line: {
        role: line?.getAttribute('role') ?? null,
        speaksByItself:
          (line?.closest('[aria-live], [role="alert"], [role="status"]') ?? null) !== null,
        hidden: line?.getAttribute('aria-hidden') ?? null,
        tabIndex: line?.tabIndex ?? null,
      },
    }).toEqual({
      hints: 1,
      // Between the boxes and the button that shows the digits.
      column: ['boxes: PIN', DEMO_PIN_HINT, 'button: Show PIN'],
      described: [DEMO_PIN_HINT],
      line: { role: null, speaksByItself: false, hidden: null, tabIndex: -1 },
    });
  });

  /*
    The digits are one constant (src/features/demo/demoPin.ts), never the PIN inside the copy this
    browser keeps: a visitor who signed in to a copy on another browser has no kept copy to read
    them from, and two sources for one line could disagree.
  */
  it('prints the demo PIN, not the PIN of a copy this browser keeps', () => {
    enableDemoMode();
    // A kept copy whose PIN is not the demo's: the line is no reading of the key.
    rememberDemoCopy({
      email: 'demo-k7m2x9q4w8e1r5t3@azurebank.example',
      password: 'Xk7p-Rm3w-Hn8d-Tq5v',
      pin: '987654',
      contacts: ['jane_k7m2', 'mike_k7m2'],
      expiresAt: '2031-07-15T12:30:00.000Z',
    });
    renderWithProviders(<Boxes />);

    expect({
      // The product reads this copy when it is asked for one, so its PIN was there to be printed.
      keptPin: getDemoCopySnapshot()?.pin ?? null,
      column: column(),
      keptPinOnThePage: document.documentElement.outerHTML.includes('987654'),
    }).toEqual({
      keptPin: '987654',
      column: ['boxes: PIN', DEMO_PIN_HINT, 'button: Show PIN'],
      keptPinOnThePage: false,
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

  /*
    The visitor who has just typed a wrong PIN is the one the line is for, and that is when a
    caller turns the boxes red; while its request runs, or while the PIN is locked, it switches
    them off (src/features/auth/StepUpModal.tsx does both). The line is there in both states.
  */
  it('the line stays under boxes that show a refusal and under boxes that wait', () => {
    enableDemoMode();

    // As a caller draws them after a wrong PIN: red, and described by the refusal.
    const refused = renderWithProviders(<Boxes ariaDescribedBy="why-it-was-refused" error />);
    const afterARefusal = { column: column(), described: described() };
    refused.unmount();

    // As a caller draws them while its request runs, or while the PIN is locked.
    renderWithProviders(<Boxes disabled />);
    const whileTheyWait = { column: column(), described: described() };

    expect({ afterARefusal, whileTheyWait }).toEqual({
      afterARefusal: {
        column: ['boxes: PIN', DEMO_PIN_HINT, 'button: Show PIN'],
        described: [REFUSED, DEMO_PIN_HINT],
      },
      whileTheyWait: {
        column: ['boxes: PIN', DEMO_PIN_HINT, 'button: Show PIN'],
        described: [DEMO_PIN_HINT],
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
        passedOne: { hints: 0, column: BARE, describedBy: 'why-it-was-refused' },
        passedNone: { hints: 0, column: BARE, describedBy: null },
      },
      withATagThatSaysFalse: { hints: 0, column: BARE, describedBy: null },
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
      chosen: { hints: 0, column: BARE, describedBy: null },
      chosenWithADescription: { hints: 0, column: BARE, describedBy: 'why-it-was-refused' },
      asked: 1,
    });
  });

  it('two groups on one page are each described by the line under them', () => {
    enableDemoMode();
    renderWithProviders(
      <>
        <PinInput value="" onChange={() => {}} ariaLabel="One PIN" />
        <PinInput value="" onChange={() => {}} ariaLabel="Another PIN" />
      </>,
    );
    const groups = ['One PIN', 'Another PIN'].map((name) => screen.getByRole('group', { name }));
    const ids = groups.map((group) => group.getAttribute('aria-describedby') ?? '');

    expect({
      hints: hints().length,
      differentIds: new Set(ids).size,
      // An id is one element's. Carried by two, it finds the first, whichever group asks.
      elementsWithEachId: ids.map((id) => document.querySelectorAll(`[id="${id}"]`).length),
      // What a group's id names is the line right under that group, not the other group's.
      namesTheLineUnderIt: groups.map(
        (group, index) => document.getElementById(ids[index]) === group.nextElementSibling,
      ),
    }).toEqual({
      hints: 2,
      differentIds: 2,
      elementsWithEachId: [1, 1],
      namesTheLineUnderIt: [true, true],
    });
  });

  /*
    Where the boxes are drawn, read from the source of every file that draws them.

    The tests above hold what `purpose` does: on the demo, boxes that say `new` get no line and
    boxes that say nothing get one. This holds where it is said. The line belongs where a visitor
    is asked for a PIN they were handed and never chose: the step-up prompt, a withdrawal, the two
    transfers, a deleted account and "Current PIN". It must stay away from the four groups where a
    PIN is being chosen, where the starting digits would read as the PIN to choose.

    One list for all ten, and not a render of each page: a place that draws the boxes later is one
    more line here before this is green again, and that line says which of the two it is.
  */
  it('says, place by place, which boxes choose a PIN and which ask for one', () => {
    // A plain relative folder, like the other tests that read source: Vitest runs from the
    // frontend root (src/theme/brandFillUsage.test.ts has the reason).
    const places = componentFiles('src')
      .flatMap((file) =>
        boxesDrawnIn(file).map((element) => {
          const name = /\sariaLabel="([^"]*)"/.exec(element)?.[1] ?? 'no name';
          const purpose = /\spurpose=(?:"([^"]*)"|\{([^}]*)\})/.exec(element);
          return `${file} | ${name} | ${purpose ? (purpose[1] ?? purpose[2]) : 'says nothing'}`;
        }),
      )
      .sort();

    expect(places).toEqual([
      'src/components/dialogs/ChangePinDialog.tsx | Confirm new PIN | new',
      'src/components/dialogs/ChangePinDialog.tsx | Current PIN | says nothing',
      'src/components/dialogs/ChangePinDialog.tsx | New PIN | new',
      'src/components/dialogs/DeleteAccountDialog.tsx | Enter your PIN | says nothing',
      'src/components/dialogs/WithdrawDialog.tsx | Enter your PIN | says nothing',
      'src/features/auth/StepUpModal.tsx | Enter your PIN | says nothing',
      'src/pages/InternalTransferPage.tsx | Enter your PIN | says nothing',
      'src/pages/PinSetupPage.tsx | Confirm your PIN | new',
      'src/pages/PinSetupPage.tsx | Create your PIN | new',
      'src/pages/TransferPage.tsx | Enter your PIN | says nothing',
    ]);
  });
});
