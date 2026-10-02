import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { renderWithProviders } from '../../test/renderWithProviders';
import { COPY } from '../../test/outage';
import { WentThroughView } from './WentThroughView';

/**
 * The screen a transfer shows when the server has said the payment went through and could not
 * return its receipt (409 `IDEMPOTENCY_RESULT_UNKNOWN` with `applied: true`, ADR-0009).
 *
 * The two transfer pages are pinned through the wire in `pages/transfer-went-through.test.tsx`.
 * These pin the component, which is a different obligation: what it can never grow. It has no
 * "start over" to wire by mistake, because a new key over a committed payment is a second payment;
 * and it has no Back and no Close, because there is no form behind it to go back to.
 */
describe('WentThroughView', () => {
  const render = (overrides: Partial<Parameters<typeof WentThroughView>[0]> = {}) =>
    renderWithProviders(
      <WentThroughView
        title="Transfer Complete"
        sentence={COPY.transferWentThrough}
        onViewHistory={() => {}}
        onDone={() => {}}
        {...overrides}
      />,
    );

  const sentence = () => screen.getByText(COPY.transferWentThrough);

  let outside: HTMLButtonElement | null = null;
  afterEach(() => {
    outside?.remove();
    outside = null;
  });

  it("carries the flow's success title as the page's one heading, and the sentence as a paragraph", () => {
    render();

    expect(screen.getAllByRole('heading')).toHaveLength(1);
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Transfer Complete');
    // A paragraph: not a second heading, and not text that only looks like one.
    expect(sentence().tagName).toBe('P');
  });

  it("offers the receipt's two ways on, and nothing that could send again", () => {
    render();

    expect(screen.getAllByRole('button').map((button) => button.textContent)).toEqual([
      'View History',
      'Done',
    ]);
    // Named individually too, so that a failure says which control appeared. The array above
    // already fails for any third button: one with an icon and no text is in it as ''.
    expect(screen.queryByRole('button', { name: 'Back' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Close' })).not.toBeInTheDocument();
    expect(screen.queryByText(/didn't go through/)).not.toBeInTheDocument();
    expect(screen.queryByText(/start over|try again/i)).not.toBeInTheDocument();
  });

  it('never navigates on its own — both actions are the caller’s', async () => {
    const onViewHistory = vi.fn();
    const onDone = vi.fn();
    render({ onViewHistory, onDone });

    await userEvent.click(screen.getByRole('button', { name: 'View History' }));
    expect(onViewHistory).toHaveBeenCalledTimes(1);
    expect(onDone).not.toHaveBeenCalled();

    await userEvent.click(screen.getByRole('button', { name: 'Done' }));
    expect(onDone).toHaveBeenCalledTimes(1);
    expect(onViewHistory).toHaveBeenCalledTimes(1);
  });

  it('lands lost focus on the sentence, so it is read before the actions are reached', () => {
    // The control that sent was disabled during the send, and the browser handed its focus to
    // `body`. Focus on the sentence is how a screen reader comes to read it.
    expect(document.body).toHaveFocus();

    render();

    expect(sentence()).toHaveFocus();
    expect(sentence()).toHaveAttribute('tabindex', '-1');
  });

  it('leaves focus where the visitor put it', () => {
    outside = document.createElement('button');
    outside.textContent = 'Somewhere else';
    document.body.append(outside);
    outside.focus();
    expect(outside).toHaveFocus();

    render();

    expect(outside).toHaveFocus();
  });

  it('leaves the reading to that focus alone: no live region around the sentence, no description on the buttons', () => {
    render();

    // A region that mounts already filled is not read; one that was, plus the focus, reads twice.
    expect(sentence().closest('[role="alert"], [role="status"], [aria-live]')).toBeNull();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.queryByRole('status')).not.toBeInTheDocument();
    // And the sentence is not a button's description: it would be read again at the first Tab.
    for (const button of screen.getAllByRole('button')) {
      expect(button).not.toHaveAttribute('aria-describedby');
    }
  });

  it("says the caller's sentence", () => {
    render({ sentence: 'Any other sentence the caller passes.' });

    expect(screen.getByText('Any other sentence the caller passes.')).toBeInTheDocument();
    expect(screen.queryByText(COPY.transferWentThrough)).not.toBeInTheDocument();
  });
});
