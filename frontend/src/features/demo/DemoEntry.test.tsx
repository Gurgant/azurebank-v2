import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { renderWithProviders } from '../../test/renderWithProviders';
import { DemoEntry } from './DemoEntry';

/*
  The demo's block of the sign-in page, by its properties alone: no store, no request, no storage.
  What the page does with a press, and where the copy comes from, is the page's test
  (src/pages/LoginPage.test.tsx).

  The words are typed out here and not imported from the product. The apostrophes are ASCII.
*/
const WORDS = {
  tryTheDemo: 'Try the demo',
  noticeFirst: 'You get a private copy of a demo bank account with invented money.',
  noticeRest:
    "It has two accounts, two months of history and two contacts you can pay. Don't enter real personal data. The copy works for 24 hours, then it is closed and deleted. This browser remembers the copy's sign-in details so you can come back to it. To limit abuse, a one-way code of your network address is kept with the copy and removed when the copy is deleted.",
  continue: 'Continue with my copy',
  getNew: 'Get a new copy',
  forget: 'Forget this copy',
  forgotten: 'This browser no longer remembers the copy.',
} as const;

const NOTICE = `${WORDS.noticeFirst} ${WORDS.noticeRest}`;

/** "This browser remembers a demo copy. It works until January 5, 2026 · 9:15 AM." */
const REMEMBERED =
  /^This browser remembers a demo copy\. It works until ([A-Z][a-z]+ \d{1,2}, \d{4}) · (\d{1,2}:\d{2} (?:AM|PM))\.$/;

/**
 * A copy as the browser keeps one: a fixture no server knows.
 *
 * Its end is a whole minute in the middle of July, far from any day a zone changes its clocks, so
 * the day and the hour it is printed as name one instant in whichever zone the suite runs.
 */
const KEPT = {
  v: 1 as const,
  email: 'demo-k7m2x9q4w8e1r5t3@azurebank.example',
  password: 'Xk7p-Rm3w-Hn8d-Tq5v',
  pin: '123456',
  contacts: ['jane_k7m2', 'mike_k7m2'],
  expiresAt: '2031-07-15T12:30:00.000Z',
};

interface BlockState {
  copy?: typeof KEPT | null;
  pending?: 'claim' | 'continue' | null;
  disabled?: boolean;
  continueLocked?: boolean;
  forgotten?: boolean;
}

function renderBlock(state: BlockState) {
  const told = { onTry: vi.fn(), onContinue: vi.fn(), onGetNew: vi.fn(), onForget: vi.fn() };
  const block = (now: BlockState) => (
    <DemoEntry
      copy={now.copy ?? null}
      pending={now.pending ?? null}
      disabled={now.disabled ?? false}
      continueLocked={now.continueLocked ?? false}
      forgotten={now.forgotten ?? false}
      onTry={told.onTry}
      onContinue={told.onContinue}
      onGetNew={told.onGetNew}
      onForget={told.onForget}
    />
  );
  const view = renderWithProviders(block(state));
  // The block is what holds its first button: every control of it is a child of the block.
  const root = () => screen.getAllByRole('button')[0].parentElement as HTMLElement;
  const buttons = () => Array.from(root().querySelectorAll('button'));
  return {
    told,
    /** How many times the page was told of each press. */
    toldOf: () => ({
      onTry: told.onTry.mock.calls.length,
      onContinue: told.onContinue.mock.calls.length,
      onGetNew: told.onGetNew.mock.calls.length,
      onForget: told.onForget.mock.calls.length,
    }),
    /** The same block, drawn again with other properties. */
    drawAgain: (now: BlockState) => view.rerender(block(now)),
    /** The one control of that name. The link is a button by its role. */
    control: (name: string) => screen.getByRole('button', { name }) as HTMLButtonElement,
    /** What the block holds, child by child, in the order it holds them. */
    parts: () =>
      Array.from(root().children).map((part) => {
        if (part.matches('[data-wait-hint]')) return 'the hint';
        if (part.matches('[role="status"]')) return `status: ${part.textContent}`;
        if (part.matches('button.fui-Link')) return `link: ${part.textContent}`;
        if (part.matches('button')) return `button: ${part.textContent}`;
        return part.textContent;
      }),
    /** The block's own status region: not the wait's, which lives inside the hint. */
    statuses: () =>
      Array.from(root().querySelectorAll<HTMLElement>('[role="status"]')).filter(
        (region) => region.closest('[data-wait-hint]') === null,
      ),
    /** Which of the block's controls cannot be pressed, by name. */
    disabled: () =>
      Object.fromEntries(buttons().map((button) => [button.textContent, button.disabled])),
    /** Which of the block's controls name something as their description, by name. */
    described: () =>
      buttons()
        .filter((button) => button.hasAttribute('aria-describedby'))
        .map((button) => button.textContent),
    spinners: (name: string) =>
      screen.getByRole('button', { name }).querySelectorAll('[role="progressbar"]').length,
  };
}

describe('the demo block of the sign-in page, for a browser that keeps no copy', () => {
  it("with no copy: the button, the hint's place, the status, the notice, in that order", () => {
    const { control, parts } = renderBlock({ pending: 'claim', disabled: true });
    const button = control(WORDS.tryTheDemo);
    const hint = document.querySelector('[data-wait-hint]');
    const described = document.getElementById(button.getAttribute('aria-describedby') ?? '');

    expect({
      parts: parts(),
      describedBy: described?.textContent,
      // The hint's words must not be read as part of the button's description, nor as part of
      // the notice: it is beside both.
      hintInsideTheDescription: described?.contains(hint),
      hintInsideTheNotice: described?.parentElement?.contains(hint),
    }).toStrictEqual({
      parts: [`button: ${WORDS.tryTheDemo}`, 'the hint', 'status: ', NOTICE],
      describedBy: WORDS.noticeFirst,
      hintInsideTheDescription: false,
      hintInsideTheNotice: false,
    });
    expect(button).toHaveAccessibleDescription(WORDS.noticeFirst);
  });

  it('a waiting button keeps its text beside its spinner', () => {
    const { control, spinners } = renderBlock({ pending: 'claim', disabled: true });
    const button = control(WORDS.tryTheDemo);

    // Its words are still its text and its name while it waits: the spinner is beside them, not
    // in their place.
    expect({
      text: button.textContent,
      spinners: spinners(WORDS.tryTheDemo),
      disabled: button.disabled,
    }).toStrictEqual({ text: WORDS.tryTheDemo, spinners: 1, disabled: true });
    expect(button).toHaveAccessibleName(WORDS.tryTheDemo);
  });

  it('a button that waits for another control is disabled, and neither spins nor hints', async () => {
    const { control, parts, spinners, toldOf } = renderBlock({ pending: null, disabled: true });

    await userEvent.click(control(WORDS.tryTheDemo));

    expect({
      disabled: control(WORDS.tryTheDemo).disabled,
      spinners: spinners(WORDS.tryTheDemo),
      parts: parts(),
      told: toldOf(),
    }).toStrictEqual({
      disabled: true,
      spinners: 0,
      parts: [`button: ${WORDS.tryTheDemo}`, 'status: ', NOTICE],
      told: { onTry: 0, onContinue: 0, onGetNew: 0, onForget: 0 },
    });
  });

  it('at rest: the button and the notice, no hint, and a press tells the page once', async () => {
    const { control, parts, described, spinners, statuses, toldOf } = renderBlock({});
    const button = control(WORDS.tryTheDemo);
    // Nothing was forgotten, so the block takes no focus when it is drawn.
    const focusWhenDrawn = document.activeElement === button;

    await userEvent.click(button);

    expect({
      disabled: button.disabled,
      spinners: spinners(WORDS.tryTheDemo),
      parts: parts(),
      described: described(),
      describedBy: document.getElementById(button.getAttribute('aria-describedby') ?? '')
        ?.textContent,
      // On the page with nothing to say: out of the page's flow, so it moves nothing.
      silentStatus: statuses().map((region) => getComputedStyle(region).position),
      focusWhenDrawn,
      told: toldOf(),
    }).toStrictEqual({
      disabled: false,
      spinners: 0,
      parts: [`button: ${WORDS.tryTheDemo}`, 'status: ', NOTICE],
      described: [WORDS.tryTheDemo],
      describedBy: WORDS.noticeFirst,
      silentStatus: ['absolute'],
      focusWhenDrawn: false,
      told: { onTry: 1, onContinue: 0, onGetNew: 0, onForget: 0 },
    });
  });
});

describe('the demo block of the sign-in page, for a browser that keeps a copy', () => {
  it("with a copy: the text, the two buttons, the hint's place, the link, the status, the notice, in that order", () => {
    const { parts, described } = renderBlock({ copy: KEPT, pending: 'continue', disabled: true });
    const [text, ...rest] = parts();
    const printed = REMEMBERED.exec(text ?? '');

    expect({
      text: printed !== null,
      // The instant printed is the copy's end, in the zone the suite runs in: read back from the
      // words, and never typed out as an hour.
      until: printed && new Date(`${printed[1]} ${printed[2]}`).toISOString(),
      rest,
      // The notice is words to read here. "Try the demo" is the one button it describes.
      described: described(),
      tryTheDemo: screen.queryAllByRole('button', { name: WORDS.tryTheDemo }).length,
    }).toStrictEqual({
      text: true,
      until: KEPT.expiresAt,
      rest: [
        `button: ${WORDS.continue}`,
        `button: ${WORDS.getNew}`,
        'the hint',
        `link: ${WORDS.forget}`,
        'status: ',
        NOTICE,
      ],
      described: [],
      tryTheDemo: 0,
    });
  });

  it('at rest with a copy: no hint, and each control tells the page of its own press, once', async () => {
    const { control, parts, disabled, toldOf } = renderBlock({ copy: KEPT });
    const partsAtRest = parts().slice(1);

    await userEvent.click(control(WORDS.continue));
    const afterContinue = toldOf();
    await userEvent.click(control(WORDS.getNew));
    const afterGetNew = toldOf();
    await userEvent.click(control(WORDS.forget));

    expect({
      partsAtRest,
      disabled: disabled(),
      afterContinue,
      afterGetNew,
      afterForget: toldOf(),
    }).toStrictEqual({
      partsAtRest: [
        `button: ${WORDS.continue}`,
        `button: ${WORDS.getNew}`,
        `link: ${WORDS.forget}`,
        'status: ',
        NOTICE,
      ],
      disabled: { [WORDS.continue]: false, [WORDS.getNew]: false, [WORDS.forget]: false },
      afterContinue: { onTry: 0, onContinue: 1, onGetNew: 0, onForget: 0 },
      afterGetNew: { onTry: 0, onContinue: 1, onGetNew: 1, onForget: 0 },
      afterForget: { onTry: 0, onContinue: 1, onGetNew: 1, onForget: 1 },
    });
  });

  it('disabled stops every control of the block, the link included', async () => {
    // With no copy: the one button.
    const { control, disabled, drawAgain, toldOf } = renderBlock({ disabled: true });
    const withNoCopy = disabled();
    await userEvent.click(control(WORDS.tryTheDemo));

    // With a copy: the two buttons, and the link, which is a button by its role.
    drawAgain({ copy: KEPT, disabled: true });
    const withACopy = disabled();
    await userEvent.click(control(WORDS.continue));
    await userEvent.click(control(WORDS.getNew));
    await userEvent.click(control(WORDS.forget));

    expect({ withNoCopy, withACopy, told: toldOf() }).toStrictEqual({
      withNoCopy: { [WORDS.tryTheDemo]: true },
      withACopy: { [WORDS.continue]: true, [WORDS.getNew]: true, [WORDS.forget]: true },
      told: { onTry: 0, onContinue: 0, onGetNew: 0, onForget: 0 },
    });
  });

  it('a waiting "Continue with my copy" keeps its text beside its spinner, and nothing else spins', () => {
    const { control, spinners } = renderBlock({ copy: KEPT, pending: 'continue', disabled: true });

    expect({
      text: control(WORDS.continue).textContent,
      spinners: [spinners(WORDS.continue), spinners(WORDS.getNew), spinners(WORDS.forget)],
      hints: document.querySelectorAll('[data-wait-hint]').length,
    }).toStrictEqual({ text: WORDS.continue, spinners: [1, 0, 0], hints: 1 });
    expect(control(WORDS.continue)).toHaveAccessibleName(WORDS.continue);
  });

  it('controls that wait for another control are disabled, and none spins or hints', () => {
    const { disabled, parts, spinners } = renderBlock({
      copy: KEPT,
      pending: null,
      disabled: true,
    });

    expect({
      disabled: disabled(),
      spinners: [spinners(WORDS.continue), spinners(WORDS.getNew)],
      hinted: parts().includes('the hint'),
    }).toStrictEqual({
      disabled: { [WORDS.continue]: true, [WORDS.getNew]: true, [WORDS.forget]: true },
      spinners: [0, 0],
      hinted: false,
    });
  });

  it('a locked copy stops "Continue with my copy", and nothing else of the block', async () => {
    const { control, disabled, spinners, toldOf } = renderBlock({
      copy: KEPT,
      continueLocked: true,
    });

    await userEvent.click(control(WORDS.continue));

    expect({
      disabled: disabled(),
      spinners: spinners(WORDS.continue),
      told: toldOf(),
    }).toStrictEqual({
      disabled: { [WORDS.continue]: true, [WORDS.getNew]: false, [WORDS.forget]: false },
      spinners: 0,
      told: { onTry: 0, onContinue: 0, onGetNew: 0, onForget: 0 },
    });
  });

  it('once the copy is forgotten: the status that was there says so, and focus is on "Try the demo"', () => {
    const { control, drawAgain, statuses } = renderBlock({ copy: KEPT });
    // On the page, empty, before there is anything to say: a polite region has to exist before
    // its words change, or the change is not read.
    const before = statuses();
    const outOfTheFlow = (regions: HTMLElement[]) =>
      regions.map((region) => getComputedStyle(region).position === 'absolute');
    const saidBefore = before.map((region) => region.textContent);
    const outOfTheFlowBefore = outOfTheFlow(before);

    drawAgain({ copy: null, forgotten: true });

    const after = statuses();
    expect({
      saidBefore,
      outOfTheFlowBefore,
      said: after.map((region) => region.textContent),
      // The region that was on the page, not a new one in its place.
      sameRegion: after.length === 1 && after[0] === before[0],
      // With words in it, it is where it stands, to be seen as well as read.
      outOfTheFlow: outOfTheFlow(after),
      focusOnTryTheDemo: document.activeElement === control(WORDS.tryTheDemo),
    }).toStrictEqual({
      saidBefore: [''],
      outOfTheFlowBefore: [true],
      said: [WORDS.forgotten],
      sameRegion: true,
      outOfTheFlow: [false],
      focusOnTryTheDemo: true,
    });
  });

  it('the focus is put on "Try the demo" once, when the block first says the copy was forgotten', () => {
    const { control, drawAgain } = renderBlock({ copy: KEPT });
    drawAgain({ copy: null, forgotten: true });
    const atTheForgetting = document.activeElement === control(WORDS.tryTheDemo);

    // The visitor moves on, and the page draws the block again for a reason of its own. The block
    // still says the copy was forgotten: that it says so is not a reason to take the focus back.
    control(WORDS.tryTheDemo).blur();
    const movedOn = document.activeElement === document.body;
    drawAgain({ copy: null, forgotten: true });

    expect({
      atTheForgetting,
      movedOn,
      afterALaterDraw: document.activeElement === control(WORDS.tryTheDemo),
    }).toStrictEqual({ atTheForgetting: true, movedOn: true, afterALaterDraw: false });
  });

  it('a copy the browser keeps again is not said to be forgotten, and takes no focus', () => {
    // "Forget this copy" was pressed on this page, and the browser has since been handed a copy
    // again: by a claim from this page, or by another tab.
    const { control, drawAgain, statuses } = renderBlock({ copy: null, forgotten: true });
    const saidWithNoCopy = statuses().map((region) => region.textContent);
    control(WORDS.tryTheDemo).blur();

    drawAgain({ copy: KEPT, forgotten: true });

    expect({
      saidWithNoCopy,
      said: statuses().map((region) => region.textContent),
      focusInTheBlock: control(WORDS.continue).parentElement?.contains(document.activeElement),
    }).toStrictEqual({ saidWithNoCopy: [WORDS.forgotten], said: [''], focusInTheBlock: false });
  });
});
