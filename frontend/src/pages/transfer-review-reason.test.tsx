import { Route, Routes } from 'react-router-dom';
import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '../mocks/server';
import { makeTestStore, renderWithProviders } from '../test/renderWithProviders';
import { apiSlice } from '../features/api/apiSlice';
import { seedMockSession } from '../mocks/state';
import { TransferPage } from './TransferPage';

/*
  "Review Transfer" cannot be pressed until the page has an account to send from, a handle that
  "Verify" has checked, and an amount that can be sent. While it cannot, the page says the one
  thing it is still waiting for, in one line under the button, and the button is described by
  that line: a screen reader that reaches the button reads why it is off.

  Until 2026-10-06 the page said nothing: a handle and an amount typed, and a button that stayed
  grey with no word about "Verify".

  The line is read here through the button, by its accessible description, and looked for in the
  page by its words. The words are typed out and not imported from the product, so a test fails
  the day the words on screen are no longer these. The apostrophe is ASCII; the last character
  of `checking` is U+2026.

  What jsdom cannot show is held nowhere in this file: that the line is as tall with no words as
  with them, so nothing under it moves. That was measured in a browser.
*/
const WORDS = {
  noAccount: 'You have no account to send from.',
  noHandle: "Enter the recipient's @handle to continue.",
  notVerified: 'Press Verify to check the handle.',
  checking: 'Checking the handle…',
  nobodyHasIt: 'Change the handle to continue.',
  noAmount: 'Enter an amount to continue.',
  amountCannotBeSent: 'Change the amount to continue.',
} as const;

function renderTransfer(store = makeTestStore()) {
  return renderWithProviders(
    <Routes>
      <Route path="/" element={<TransferPage />} />
      <Route path="/transfer/internal" element={<div>INTERNAL</div>} />
    </Routes>,
    { routerEntries: ['/'], store },
  );
}

const review = () => screen.getByRole('button', { name: 'Review Transfer' }) as HTMLButtonElement;
const handleField = () => screen.getByLabelText('Recipient handle');
const amountField = () => screen.getByLabelText('Transfer amount');
const verify = () => screen.getByRole('button', { name: 'Verify' });

/** What each element "Review Transfer" names as its description says: `[]` for no description. */
const describedBy = () =>
  (review().getAttribute('aria-describedby') ?? '')
    .split(/\s+/)
    .filter(Boolean)
    .map((id) => document.getElementById(id)?.textContent ?? `no element with the id ${id}`);

/** The button and its reason, as the page has them now. */
const now = () => ({ disabled: review().disabled, describedBy: describedBy() });

/** Whether each element is in the page and comes after the one before it, in document order. */
function inOrder(...elements: (Element | null | undefined)[]): boolean {
  return elements.every((element, index) => {
    if (!element) return false;
    const before = elements[index - 1];
    return (
      index === 0 ||
      (!!before &&
        (before.compareDocumentPosition(element) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0)
    );
  });
}

/**
 * Every text `line` held from before `act` to after it, in order: the text it started with, then
 * its text after each change. Read from the changes themselves and not from the line when they
 * are reported: two changes inside one task are reported together, and the line then shows only
 * the last. React changes the words of an element whose one child is text by changing that text
 * node, which is the change that carries the value it replaced.
 */
async function textsDuring(line: Element, act: () => Promise<void>): Promise<string[]> {
  const before: string[] = [];
  const keep = (records: MutationRecord[]) => {
    for (const record of records) {
      before.push(
        record.type === 'characterData'
          ? (record.oldValue ?? '')
          : Array.from(record.removedNodes)
              .map((node) => node.textContent ?? '')
              .join(''),
      );
    }
  };
  const observer = new MutationObserver(keep);
  observer.observe(line, {
    subtree: true,
    childList: true,
    characterData: true,
    characterDataOldValue: true,
  });
  try {
    await act();
  } finally {
    keep(observer.takeRecords());
    observer.disconnect();
  }
  return [...before, line.textContent ?? ''];
}

/** Too many lookups, as the API's limiter turns a check away (`TransferPage.test.tsx`). */
const tooManyLookups = () =>
  HttpResponse.json(
    { type: 'https://httpstatuses.com/429', title: 'Too Many Requests', status: 429 },
    { status: 429, headers: { 'Retry-After': '90' } },
  );

/** The page with its accounts loaded and nothing typed. */
async function openTheForm() {
  const view = renderTransfer();
  await screen.findByText('Main Account');
  return view;
}

/** The form as `openTheForm` has it, for a visitor the page knows: the mock's `demo_user`. */
async function openTheFormSignedIn() {
  seedMockSession();
  const store = makeTestStore();
  await store.dispatch(apiSlice.endpoints.getMe.initiate()).unwrap();
  const view = renderTransfer(store);
  await screen.findByText('Main Account');
  return view;
}

const OWN_HANDLE =
  'That\'s your own handle. To move money between your own accounts, use "Between your own accounts" below.';

describe('what "Review Transfer" is still waiting for', () => {
  it('nothing typed: the handle', async () => {
    await openTheForm();

    expect(now()).toStrictEqual({ disabled: true, describedBy: [WORDS.noHandle] });
    expect(review()).toHaveAccessibleDescription(WORDS.noHandle);
  });

  it('an "@" alone is no handle', async () => {
    await openTheForm();
    await userEvent.type(handleField(), '@');

    expect(now()).toStrictEqual({ disabled: true, describedBy: [WORDS.noHandle] });
  });

  it('a handle typed and not checked: "Verify", whatever the amount', async () => {
    await openTheForm();
    await userEvent.type(handleField(), 'friend');
    const beforeTheAmount = now();
    await userEvent.type(amountField(), '50');

    // The case the page was silent about: a handle and an amount, and a button that stays off.
    expect({ beforeTheAmount, withTheAmount: now() }).toStrictEqual({
      beforeTheAmount: { disabled: true, describedBy: [WORDS.notVerified] },
      withTheAmount: { disabled: true, describedBy: [WORDS.notVerified] },
    });
  });

  it('while the check runs it says so, and not to press a button that is busy', async () => {
    let answer = () => {};
    const held = new Promise<void>((resolve) => {
      answer = resolve;
    });
    // Held, then left to the mock's own handler: a handler that returns nothing passes on.
    server.use(
      http.get('*/api/users/:azureTag', async () => {
        await held;
      }),
    );
    await openTheForm();
    await userEvent.type(handleField(), 'friend');
    await userEvent.click(verify());
    const whileItRuns = now();
    answer();
    await screen.findByText('A. Friend');

    expect({ whileItRuns, after: now() }).toStrictEqual({
      whileItRuns: { disabled: true, describedBy: [WORDS.checking] },
      after: { disabled: true, describedBy: [WORDS.noAmount] },
    });
  });

  it('a handle nobody has: to change it, and once it is changed, to check it', async () => {
    /*
      Until a second change of 2026-10-06 this test was "a handle nobody has is still a handle
      to check": under the check's own refusal the line went on asking for "Verify", the press
      the visitor had just made, which gets the same answer again.
    */
    await openTheForm();
    await userEvent.type(handleField(), 'nobody');
    await userEvent.click(verify());
    await screen.findByText(/We couldn't find @nobody/);
    const refused = now();
    // One more letter, and it is a handle nobody has checked.
    await userEvent.type(handleField(), 'x');

    expect({ refused, typedSince: now() }).toStrictEqual({
      refused: { disabled: true, describedBy: [WORDS.nobodyHasIt] },
      typedSince: { disabled: true, describedBy: [WORDS.notVerified] },
    });
  });

  it.each(['demo_user', '@demo_user', 'DEMO_USER', '@DeMo_UsEr'])(
    "the visitor's own handle %s is refused locally, then an edited handle can be verified",
    async (ownHandle) => {
      let lookupRequests = 0;
      server.use(
        http.get('*/api/users/:azureTag', () => {
          lookupRequests += 1;
        }),
      );
      await openTheFormSignedIn();
      fireEvent.change(amountField(), { target: { value: '50' } });
      fireEvent.change(handleField(), { target: { value: ownHandle } });
      await userEvent.click(verify());

      expect(lookupRequests).toBe(0);
      await screen.findByText(OWN_HANDLE);
      expect(screen.getByText(OWN_HANDLE).closest('[role="alert"]')).not.toBeNull();
      expect(lookupRequests).toBe(0);
      expect(now()).toStrictEqual({ disabled: true, describedBy: [WORDS.nobodyHasIt] });
      expect(review()).toHaveAccessibleDescription('Change the handle to continue.');

      fireEvent.change(handleField(), { target: { value: 'friend' } });
      expect(screen.queryByText(OWN_HANDLE)).not.toBeInTheDocument();
      expect(now()).toStrictEqual({ disabled: true, describedBy: [WORDS.notVerified] });
      await userEvent.click(verify());
      await screen.findByText('A. Friend');
      expect(lookupRequests).toBe(1);
      await waitFor(() => expect(review()).toBeEnabled());
    },
  );

  it("the visitor's own handle verified twice: the alert goes through empty, so the second is said", async () => {
    // An alert whose words do not change is not read out again. Verify empties it first, as it
    // does before a lookup, so the same sentence a second time is a change.
    await openTheFormSignedIn();
    fireEvent.change(handleField(), { target: { value: 'demo_user' } });
    await userEvent.click(verify());
    const alert = (await screen.findByText(OWN_HANDLE)).closest('[role="alert"]') as Element;

    const texts = await textsDuring(alert, async () => {
      await userEvent.click(verify());
      await screen.findByText(OWN_HANDLE);
    });

    expect(texts).toStrictEqual([OWN_HANDLE, '', OWN_HANDLE]);
  });

  it.each([
    { nextHandle: 'demo_user', pressEnter: true },
    { nextHandle: 'another_friend', pressEnter: false },
  ])('ignores a held lookup after editing to $nextHandle', async ({ nextHandle, pressEnter }) => {
    let answer = () => {};
    const held = new Promise<void>((resolve) => {
      answer = resolve;
    });
    let started = () => {};
    const requested = new Promise<void>((resolve) => {
      started = resolve;
    });
    server.use(
      http.get('*/api/users/:azureTag', async () => {
        started();
        await held;
        return HttpResponse.json({
          data: { exists: true, azureTag: 'friend', displayName: 'A. Friend' },
          message: null,
        });
      }),
    );
    const { store } = await openTheFormSignedIn();
    fireEvent.change(amountField(), { target: { value: '50' } });
    fireEvent.change(handleField(), { target: { value: 'friend' } });
    await userEvent.click(verify());
    await requested;
    const pending = store.dispatch(apiSlice.util.getRunningQueryThunk('lookupRecipient', 'friend'));
    expect(pending).toBeDefined();

    fireEvent.change(handleField(), { target: { value: nextHandle } });
    if (pressEnter) {
      fireEvent.keyDown(handleField(), { key: 'Enter' });
      await screen.findByText(OWN_HANDLE);
    }
    await act(async () => {
      answer();
      await pending;
    });

    expect(screen.queryByText('A. Friend')).not.toBeInTheDocument();
    expect(review()).toBeDisabled();
    if (pressEnter) expect(screen.getByText(OWN_HANDLE)).toBeInTheDocument();
  });

  it('a check the bank turned away is still a check to make: "Verify", not another handle', async () => {
    // CONTROL: green before the line for a handle nobody has
    server.use(http.get('*/api/users/:azureTag', tooManyLookups));
    await openTheForm();
    await userEvent.type(handleField(), 'friend');
    await userEvent.click(verify());
    await screen.findByText(/Too many lookups/);

    expect(now()).toStrictEqual({ disabled: true, describedBy: [WORDS.notVerified] });
  });

  it('a handle nobody had, checked again and turned away: "Verify" again', async () => {
    await openTheForm();
    await userEvent.type(handleField(), 'nobody');
    await userEvent.click(verify());
    await screen.findByText(/We couldn't find @nobody/);
    const refused = now();
    // The same handle, and this time the check gets no answer about it: the answer before is
    // not the last word, and the handle may be a good one.
    server.use(http.get('*/api/users/:azureTag', tooManyLookups));
    await userEvent.click(verify());
    await screen.findByText(/Too many lookups/);

    expect({ refused, turnedAway: now() }).toStrictEqual({
      refused: { disabled: true, describedBy: [WORDS.nobodyHasIt] },
      turnedAway: { disabled: true, describedBy: [WORDS.notVerified] },
    });
  });

  it('a handle checked: the amount', async () => {
    await openTheForm();
    await userEvent.type(handleField(), 'friend');
    await userEvent.click(verify());
    await screen.findByText('A. Friend');
    const empty = now();
    await userEvent.type(amountField(), '0');

    expect({ empty, zero: now() }).toStrictEqual({
      empty: { disabled: true, describedBy: [WORDS.noAmount] },
      zero: { disabled: true, describedBy: [WORDS.amountCannotBeSent] },
    });
  });

  it.each(['0.00', '.'])('a handle checked and %s typed: to change the amount', async (amount) => {
    await openTheForm();
    await userEvent.type(handleField(), 'friend');
    await userEvent.click(verify());
    await screen.findByText('A. Friend');
    await userEvent.type(amountField(), amount);

    expect(now()).toStrictEqual({ disabled: true, describedBy: [WORDS.amountCannotBeSent] });
  });

  it('a handle checked and zero cleared: to enter an amount again', async () => {
    await openTheForm();
    await userEvent.type(handleField(), 'friend');
    await userEvent.click(verify());
    await screen.findByText('A. Friend');
    await userEvent.type(amountField(), '0');
    expect(now()).toStrictEqual({ disabled: true, describedBy: [WORDS.amountCannotBeSent] });

    await userEvent.clear(amountField());

    expect(now()).toStrictEqual({ disabled: true, describedBy: [WORDS.noAmount] });
  });

  it('an amount that cannot be sent: to change it, under the field saying why', async () => {
    await openTheForm();
    await userEvent.type(handleField(), 'friend');
    await userEvent.click(verify());
    await screen.findByText('A. Friend');
    // Main Account holds €1,250.50.
    await userEvent.type(amountField(), '99999');

    expect({
      ...now(),
      // The field's own sentence has the figure, and it is said once.
      underTheField: screen.queryAllByText('Exceeds available balance of €1,250.50.').length,
    }).toStrictEqual({
      disabled: true,
      describedBy: [WORDS.amountCannotBeSent],
      underTheField: 1,
    });
  });

  it('everything there: the button can be pressed and is described by nothing', async () => {
    await openTheForm();
    await userEvent.type(handleField(), 'friend');
    await userEvent.click(verify());
    await screen.findByText('A. Friend');
    await userEvent.type(amountField(), '50');

    expect({
      ...now(),
      hasADescription: review().hasAttribute('aria-describedby'),
      // None of the seven sentences is left on the page (six, until the one for a handle nobody
      // has).
      sentencesOnThePage: Object.values(WORDS).filter((words) => screen.queryByText(words)),
    }).toStrictEqual({
      disabled: false,
      describedBy: [],
      hasADescription: false,
      sentencesOnThePage: [],
    });
  });

  it('a handle changed after it was checked has to be checked again', async () => {
    await openTheForm();
    await userEvent.type(handleField(), 'friend');
    await userEvent.click(verify());
    await screen.findByText('A. Friend');
    await userEvent.type(amountField(), '50');
    const ready = now();
    await userEvent.type(handleField(), 's');

    expect({ ready, changed: now() }).toStrictEqual({
      ready: { disabled: false, describedBy: [] },
      changed: { disabled: true, describedBy: [WORDS.notVerified] },
    });
  });

  it('no account at all: that, before anything about a handle', async () => {
    server.use(http.get('*/api/accounts', () => HttpResponse.json({ data: [], message: null })));
    renderTransfer();
    await screen.findByLabelText('Recipient handle');
    await userEvent.type(handleField(), 'friend');

    expect(now()).toStrictEqual({ disabled: true, describedBy: [WORDS.noAccount] });
  });

  it('the line is words on the page, once, under the button and above the way out', async () => {
    await openTheForm();
    const lines = screen.queryAllByText(WORDS.noHandle);

    expect({
      lines: lines.map((line) => line.tagName),
      // The line is what the button names, and not a second copy of the words for a screen reader.
      isTheDescription: lines[0]?.id === review().getAttribute('aria-describedby'),
      // Words to read where they stand: a region that speaks by itself would say each change of
      // the line over the field's own alert, which changes at the same key press.
      speaksByItself: lines.map(
        (line) => line.closest('[role="alert"], [role="status"], [aria-live]') !== null,
      ),
      order: inOrder(
        review(),
        lines[0],
        screen.getByRole('button', { name: 'Between your own accounts' }),
      ),
    }).toStrictEqual({
      lines: ['P'],
      isTheDescription: true,
      speaksByItself: [false],
      order: true,
    });
  });

  it('a good amount typed into the empty field never passes through the sentence for a bad one', async () => {
    /*
      The form's own verdict arrives a render after the key press. A line that took "the form
      is not valid" for "the amount is wrong" said "Change the amount to continue." for that
      render, on the way from asking for an amount to saying nothing.
    */
    await openTheForm();
    await userEvent.type(handleField(), 'friend');
    await userEvent.click(verify());
    await screen.findByText('A. Friend');
    const line = screen.getByText(WORDS.noAmount);

    const texts = await textsDuring(line, async () => {
      await userEvent.type(amountField(), '5');
      await waitFor(() => expect(review()).toBeEnabled());
    });

    expect(texts).toStrictEqual([WORDS.noAmount, '']);
  });

  it('the line stays where it is, with words or without: one element from first to last', async () => {
    await openTheForm();
    const line = screen.getByText(WORDS.noHandle);
    const seen = [line.textContent];
    await userEvent.type(handleField(), 'friend');
    seen.push(line.textContent);
    await userEvent.click(verify());
    await screen.findByText('A. Friend');
    seen.push(line.textContent);
    await userEvent.type(amountField(), '50');
    seen.push(line.textContent);

    expect({ seen, stillInThePage: line.isConnected }).toStrictEqual({
      seen: [WORDS.noHandle, WORDS.notVerified, WORDS.noAmount, ''],
      stillInThePage: true,
    });
  });
});
