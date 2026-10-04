import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { renderWithProviders } from '../../test/renderWithProviders';
import { DemoEntry } from './DemoEntry';

/*
  The demo's block of the sign-in page, by its properties alone: no store, no request. What the
  page does with a press is the page's test (src/pages/LoginPage.test.tsx).

  The words are typed out here and not imported from the product. The apostrophes are ASCII.
*/
const WORDS = {
  tryTheDemo: 'Try the demo',
  noticeFirst: 'You get a private copy of a demo bank account with invented money.',
  noticeRest:
    "It has two accounts, two months of history and two contacts you can pay. Don't enter real personal data. The copy works for 24 hours, then it is closed and deleted. This browser remembers the copy's sign-in details so you can come back to it. To limit abuse, a one-way code of your network address is kept with the copy and removed when the copy is deleted.",
} as const;

const NOTICE = `${WORDS.noticeFirst} ${WORDS.noticeRest}`;

function renderBlock(props: { pending: 'claim' | null; disabled: boolean }) {
  const onTry = vi.fn();
  renderWithProviders(
    <DemoEntry pending={props.pending} disabled={props.disabled} onTry={onTry} />,
  );
  // The block's one button, whatever it is called: its name is each test's to assert.
  const button = screen.getByRole('button') as HTMLButtonElement;
  return {
    onTry,
    button,
    /** What the block holds, child by child, in the order it holds them. */
    parts: () =>
      Array.from(button.parentElement?.children ?? []).map((part) => {
        if (part === button) return 'the button';
        if (part.matches('[data-wait-hint]')) return 'the hint';
        return part.textContent;
      }),
    /** The text of each element the button names as its description. */
    describedBy: () =>
      (button.getAttribute('aria-describedby') ?? '')
        .split(/\s+/)
        .filter(Boolean)
        .map((id) => document.getElementById(id)?.textContent),
    spinners: () => button.querySelectorAll('[role="progressbar"]').length,
  };
}

describe('the demo block of the sign-in page, for a browser that keeps no copy', () => {
  it("with no copy: the button, the hint's place, the notice, in that order", () => {
    const { button, parts, describedBy } = renderBlock({ pending: 'claim', disabled: true });
    const hint = document.querySelector('[data-wait-hint]');
    const described = document.getElementById(button.getAttribute('aria-describedby') ?? '');

    expect({
      button: button.textContent,
      parts: parts(),
      describedBy: describedBy(),
      // The hint's words must not be read as part of the button's description, nor as part of
      // the notice: it is beside both.
      hintInsideTheDescription: described?.contains(hint),
      hintInsideTheNotice: described?.parentElement?.contains(hint),
    }).toStrictEqual({
      button: WORDS.tryTheDemo,
      parts: ['the button', 'the hint', NOTICE],
      describedBy: [WORDS.noticeFirst],
      hintInsideTheDescription: false,
      hintInsideTheNotice: false,
    });
    expect(button).toHaveAccessibleDescription(WORDS.noticeFirst);
  });

  it('a waiting button keeps its text beside its spinner', () => {
    const { button, spinners } = renderBlock({ pending: 'claim', disabled: true });

    // Its words are still its text and its name while it waits: the spinner is beside them, not
    // in their place.
    expect({
      text: button.textContent,
      spinners: spinners(),
      disabled: button.disabled,
    }).toStrictEqual({ text: WORDS.tryTheDemo, spinners: 1, disabled: true });
    expect(button).toHaveAccessibleName(WORDS.tryTheDemo);
  });

  it('a button that waits for another control is disabled, and neither spins nor hints', async () => {
    const { button, onTry, parts, spinners } = renderBlock({ pending: null, disabled: true });

    await userEvent.click(button);

    expect({
      button: button.textContent,
      disabled: button.disabled,
      spinners: spinners(),
      parts: parts(),
      told: onTry.mock.calls.length,
    }).toStrictEqual({
      button: WORDS.tryTheDemo,
      disabled: true,
      spinners: 0,
      parts: ['the button', NOTICE],
      told: 0,
    });
  });

  it('at rest: the button and the notice, no hint, and a press tells the page once', async () => {
    const { button, onTry, parts, describedBy, spinners } = renderBlock({
      pending: null,
      disabled: false,
    });

    await userEvent.click(button);

    expect({
      button: button.textContent,
      disabled: button.disabled,
      spinners: spinners(),
      parts: parts(),
      describedBy: describedBy(),
      told: onTry.mock.calls.length,
    }).toStrictEqual({
      button: WORDS.tryTheDemo,
      disabled: false,
      spinners: 0,
      parts: ['the button', NOTICE],
      describedBy: [WORDS.noticeFirst],
      told: 1,
    });
  });
});
