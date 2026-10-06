import { AxeBuilder } from '@axe-core/playwright';
import { expect, test, type Locator, type Page } from '@playwright/test';
import { scan } from './axeScan';
import { fieldContrast, textContrast } from './contrast';

/**
 * The accessibility sweep: measured, reported, and gated on serious and critical findings.
 *
 * axe-core runs over the five navigation places, the Transfer wizard and the internal-transfer
 * page it links to (not the transaction detail or PIN setup), the login and register pages without
 * a session (/about is public too, but is scanned here only inside the signed-in shell), the
 * deposit dialog, the Change PIN dialog, and /accounts while its read is slow (light, dark and
 * 375 px wide): fourteen scans, with the WCAG 2.0 A/AA, 2.1 AA and 2.2 AA tags. The dialog scans
 * are scoped to the dialog, so what the page behind it fails is not reported as the dialog's.
 * A fifteenth scan of this run is not in this file: the app's one hand-rolled dialog, open, in
 * `confirmDialog.spec.ts`, where the run has it open.
 * Each scan writes its findings to `test-results/axe/<scan>.json` (a CI artifact)
 * and attaches them to the Playwright report, then FAILS on any serious or critical violation
 * outside `REPORT_ONLY_RULES`. Until 2026-09-17 nothing here failed on a violation: the sweep was
 * the first measurement, and a gate over findings nobody had read would have been a gate nobody
 * could pass. The two rules it found were colour contrast, which is U8's, and Tabster's focus
 * sentinels, which are excluded with the reason beside the gate. The gate is `scan`, in
 * `./axeScan.ts`: `REPORT_ONLY_RULES` and the exclusion are there with it.
 *
 * What ALSO fails, so an empty or misdirected report cannot read as a clean one:
 * - a page that never became ready (each scan first waits for the page's h1 or form field);
 * - a scan that landed somewhere else. An expired session is redirected to /login, which has an h1
 *   and passing rules of its own, so neither the ready wait nor the zero-passes check would notice;
 *   the path is asserted before axe runs;
 * - a page whose title is not its own (WCAG 2.4.2, which axe cannot judge);
 * - a scoped scan whose selector does not name exactly one element;
 * - a scan axe reports with zero passed rules, which inspected nothing;
 * - an exclusion that hides anything but a Tabster sentinel, or a rule the gate names that did not
 *   run at all.
 * Each scan also waits for the page's animations to finish first, so a fade is not measured.
 *
 * Six blocks further down are not scans. Each holds, by measuring it, one thing the sweep does
 * not: that what is typed in a field can be read, that the amount field shows where focus is,
 * that a dialog gives focus back when it closes, that the two money tiles share a row at phone
 * width, that a button of Settings keeps its label on one line there, and that certain words
 * which were too faint stay readable. Each has its own note above it. Two more of the kind need
 * an entry in the ledger, which this suite's user is not seeded with, so they are in
 * `deposit.spec.ts`, after the deposit that leaves one.
 */
type Scan = { name: string; path: string; title: string; ready: (page: Page) => Promise<void> };

const SIGNED_IN: Scan[] = [
  { name: 'dashboard', path: '/dashboard', title: 'Home · AzureBank', ready: heading(1) },
  { name: 'accounts', path: '/accounts', title: 'Accounts · AzureBank', ready: heading(1) },
  { name: 'history', path: '/history', title: 'History · AzureBank', ready: heading(1) },
  { name: 'transfer', path: '/transfer', title: 'Send Money · AzureBank', ready: heading(1) },
  {
    name: 'transfer-internal',
    path: '/transfer/internal',
    title: 'Move Money · AzureBank',
    ready: heading(1),
  },
  { name: 'settings', path: '/settings', title: 'Settings · AzureBank', ready: heading(1) },
  { name: 'about', path: '/about', title: 'About this project · AzureBank', ready: heading(1) },
];

const PUBLIC: Scan[] = [
  { name: 'login', path: '/login', title: 'Sign in · AzureBank', ready: labelled('Email address') },
  {
    name: 'register',
    path: '/register',
    title: 'Create Account · AzureBank',
    ready: labelled('Email'),
  },
];

function heading(level: 1 | 2 | 3) {
  return async (page: Page) => {
    await expect(page.getByRole('heading', { level }).first()).toBeVisible();
  };
}

/** The outline the browser computes for an element, as the three values the ring is made of. */
function focusRing(el: Element) {
  const style = getComputedStyle(el);
  return { style: style.outlineStyle, width: style.outlineWidth, offset: style.outlineOffset };
}

function labelled(label: string) {
  return async (page: Page) => {
    await expect(page.getByLabel(label, { exact: true })).toBeVisible();
  };
}

test.describe('accessibility, signed in', () => {
  for (const { name, path, title, ready } of SIGNED_IN) {
    test(`${name} has no serious or critical finding`, async ({ page }) => {
      await page.goto(path);
      await ready(page);
      await expect(page).toHaveTitle(title);
      await scan(page, name, path);
    });
  }

  test('the deposit dialog has no serious or critical finding', async ({ page }) => {
    await page.goto('/dashboard');
    await heading(1)(page);
    await page.getByRole('button', { name: 'Deposit', exact: true }).click();
    await expect(page.getByRole('dialog', { name: /deposit money/i })).toBeVisible();
    await scan(page, 'dashboard-deposit-dialog', '/dashboard', '[role="dialog"]');
  });

  // PinInput under the gate: its group, six digit boxes and reveal toggle, three times over, with
  // no money moved and no PIN attempt spent.
  test('the Change PIN dialog has no serious or critical finding', async ({ page }) => {
    await page.goto('/settings');
    await heading(1)(page);
    await page.getByRole('button', { name: 'Change PIN', exact: true }).click();
    await expect(page.getByRole('dialog', { name: /change your pin/i })).toBeVisible();
    await scan(page, 'settings-change-pin-dialog', '/settings', '[role="dialog"]');
  });

  test('a route change is titled, announced and focused', async ({ page }) => {
    await page.goto('/dashboard');
    await heading(1)(page);
    await expect(page).toHaveTitle('Home · AzureBank');
    // A load is not a change: nothing is announced, and focus is left where the browser put it.
    const announcer = page.locator('[data-route-announcer]');
    await expect(announcer).toBeEmpty();

    const nav = page.getByRole('navigation', { name: 'Main navigation' });
    await nav.getByRole('link', { name: 'Accounts' }).press('Enter');
    await expect(page).toHaveTitle('Accounts · AzureBank');
    await expect(announcer).toHaveText('Accounts page loaded');
    const main = page.getByRole('main');
    await expect(main).toBeFocused();
    // The keyboard got here, so the ring is this app's, not the browser's default (`auto`).
    expect(await main.evaluate(focusRing)).toEqual({
      style: 'solid',
      width: '2px',
      offset: '-2px',
    });

    // The wizard has no shell and no <main>: its heading takes focus instead.
    await nav.getByRole('button', { name: 'Transfer' }).press('Enter');
    await expect(page).toHaveTitle('Send Money · AzureBank');
    await expect(announcer).toHaveText('Send Money page loaded');
    const wizardHeading = page.getByRole('heading', { level: 1, name: 'Send Money' });
    await expect(wizardHeading).toBeFocused();
    expect(await wizardHeading.evaluate(focusRing)).toEqual({
      style: 'solid',
      width: '2px',
      offset: '2px',
    });

    // A mouse user sees no ring: the browser does not match :focus-visible after a click.
    await page.goto('/dashboard');
    await heading(1)(page);
    await nav.getByRole('link', { name: 'Accounts' }).click();
    await expect(page).toHaveTitle('Accounts · AzureBank');
    await expect(main).toBeFocused();
    expect((await main.evaluate(focusRing)).style).toBe('none');
  });
});

/*
  A LOADING STATE, SCANNED. Every scan above waits for a page's content, so nothing here had ever
  looked at the page while it waits — the one state the "Taking longer than usual…" hint lives in.
  The read is parked with `page.route` and the page's clock moved to 20 s, so the scan sees the
  hint's last words and its "Stop waiting" button. Three variants, because the hint's grey is the
  kind of colour that passes on white and fails on the dark canvas, and its row must wrap at phone
  width. The second, narrower run looks only at the hint and only at contrast, and must have
  checked at least one node of it — a hint axe never saw would pass that check too.
*/
const SLOW_READ_VARIANTS: { name: string; prepare: (page: Page) => Promise<void> }[] = [
  { name: 'accounts-slow-read', prepare: async () => {} },
  {
    name: 'accounts-slow-read-dark',
    prepare: (page) => page.emulateMedia({ colorScheme: 'dark' }),
  },
  {
    name: 'accounts-slow-read-375',
    prepare: (page) => page.setViewportSize({ width: 375, height: 812 }),
  },
];

test.describe('accessibility while a read is slow', () => {
  for (const { name, prepare } of SLOW_READ_VARIANTS) {
    test(`/accounts while a read is slow (${name}) has no serious or critical finding`, async ({
      page,
    }) => {
      await prepare(page);
      let parked = 0;
      await page.clock.install();
      await page.route('**/api/accounts*', () => {
        // Never answered: the page waits until the test ends.
        parked += 1;
      });
      await page.goto('/accounts');
      await expect.poll(() => parked).toBe(1);
      await expect(page.getByLabel('Loading accounts')).toBeVisible();

      await page.clock.fastForward('00:20');
      await expect(page.getByRole('status').filter({ hasText: 'Still trying…' })).toBeVisible();
      await expect(page.getByRole('button', { name: 'Stop waiting' })).toBeVisible();
      await expect(page).toHaveTitle('Accounts · AzureBank');

      await scan(page, name, '/accounts');

      const contrast = await new AxeBuilder({ page })
        .include('[data-wait-hint]')
        .withRules(['color-contrast'])
        .analyze();
      const checked = contrast.passes.find((rule) => rule.id === 'color-contrast')?.nodes ?? [];
      expect(checked.length, `${name}: axe checked no node of the hint`).toBeGreaterThan(0);
      expect(
        contrast.violations.flatMap((rule) => rule.nodes.map((node) => node.target.join(' '))),
        `${name}: the hint fails colour contrast`,
      ).toEqual([]);
    });
  }
});

/*
  WHAT IS TYPED CAN BE READ. Three text fields are plain `<input>`s that the app styles itself, not
  Fluent's: the recipient's handle on the transfer page and the description in the deposit and the
  withdrawal dialogs. Each took its text colour from the theme and no background, so the browser
  painted it its own white: in the dark theme the typed words were the theme's near-white on a
  white field, 1.19 to 1, in headless Chromium on 2026-10-06, and only the placeholder showed.

  Each row types into the field and holds the two texts a visitor reads in it, against the field's
  own ground, at 4.5 to 1 or more: what was typed, and the placeholder, which has to be read
  before anything is typed and sits on the same ground. Both themes, because a pair that suits one
  is how this got in. The sweep above does not hold it: there colour contrast is reported and
  never gated, and it scans these fields empty.

  The theme is asserted first. A row that asked for dark and was drawn light would measure the
  light pair and pass.
*/
const TYPED_FIELDS: { name: string; reach: (page: Page) => Promise<Locator> }[] = [
  {
    name: "the transfer's recipient handle",
    reach: async (page) => {
      await page.goto('/transfer');
      return page.getByRole('textbox', { name: 'Recipient handle' });
    },
  },
  {
    name: "the deposit dialog's description",
    reach: async (page) => {
      await page.goto('/dashboard');
      await heading(1)(page);
      await page.getByRole('button', { name: 'Deposit', exact: true }).click();
      return page
        .getByRole('dialog', { name: /deposit money/i })
        .getByRole('textbox', { name: 'Description' });
    },
  },
  {
    name: "the withdrawal dialog's description",
    reach: async (page) => {
      await page.goto('/dashboard');
      await heading(1)(page);
      await page.getByRole('button', { name: 'Withdraw', exact: true }).click();
      return page
        .getByRole('dialog', { name: /withdraw money/i })
        .getByRole('textbox', { name: 'Description' });
    },
  },
];

test.describe('what is typed in a field can be read', () => {
  for (const theme of ['light', 'dark'] as const) {
    for (const { name, reach } of TYPED_FIELDS) {
      test(`${name}, in the ${theme} theme`, async ({ page }) => {
        await page.emulateMedia({ colorScheme: theme });
        const field = await reach(page);
        await expect(page.locator('html')).toHaveAttribute('data-theme', theme);

        const empty = await fieldContrast(field);
        expect(
          empty.placeholder,
          `${name} (${theme}): the placeholder ${empty.colours.placeholder} on ${empty.colours.ground}`,
        ).toBeGreaterThanOrEqual(4.5);

        await field.fill('Can this be read?');
        const typed = await fieldContrast(field);
        expect(
          typed.typed,
          `${name} (${theme}): the typed text ${typed.colours.text} on ${typed.colours.ground}`,
        ).toBeGreaterThanOrEqual(4.5);
      });
    }
  }
});

/*
  THE AMOUNT FIELD SHOWS WHERE FOCUS IS. The figure a visitor types is a borderless `<input>` on a
  card, styled `outline: none` so that the browser's default ring does not box it. The two
  transfer pages put a ring of their own in its place; the two money dialogs put nothing, and
  there the caret was the only sign of focus (WCAG 2.4.7). Each row gives the field focus and
  holds that an outline of 2 px or more is then drawn around it. A browser matches
  `:focus-visible` on a text field however focus got there, so the row has no keys to press.
*/
const AMOUNT_FIELDS: { name: string; reach: (page: Page) => Promise<Locator> }[] = [
  {
    name: 'the transfer page',
    reach: async (page) => {
      await page.goto('/transfer');
      return page.getByRole('textbox', { name: 'Transfer amount' });
    },
  },
  {
    name: 'the transfer between own accounts',
    reach: async (page) => {
      await page.goto('/transfer/internal');
      return page.getByRole('textbox', { name: 'Transfer amount' });
    },
  },
  {
    name: 'the deposit dialog',
    reach: async (page) => {
      await page.goto('/dashboard');
      await heading(1)(page);
      await page.getByRole('button', { name: 'Deposit', exact: true }).click();
      return page
        .getByRole('dialog', { name: /deposit money/i })
        .getByRole('textbox', { name: 'Deposit amount' });
    },
  },
  {
    name: 'the withdrawal dialog',
    reach: async (page) => {
      await page.goto('/dashboard');
      await heading(1)(page);
      await page.getByRole('button', { name: 'Withdraw', exact: true }).click();
      return page
        .getByRole('dialog', { name: /withdraw money/i })
        .getByRole('textbox', { name: 'Withdraw amount' });
    },
  },
];

test.describe('the amount field shows where focus is', () => {
  for (const { name, reach } of AMOUNT_FIELDS) {
    test(`on ${name}`, async ({ page }) => {
      const amount = await reach(page);
      await amount.focus();
      await expect(amount).toBeFocused();
      const ring = await amount.evaluate(focusRing);
      expect(ring.style, `${name}: no outline is drawn around the focused amount`).not.toBe('none');
      expect(
        parseFloat(ring.width),
        `${name}: the outline is ${ring.width} wide`,
      ).toBeGreaterThanOrEqual(2);
    });
  }
});

/*
  A DIALOG GIVES FOCUS BACK. Every dialog here is opened by a control outside it and closed from
  inside it. While it is open Fluent keeps focus in it; when it closed, focus was left on the page
  body, so a keyboard visitor's next Tab started again from the top of the page (WCAG 2.4.3).
  Each row opens a dialog with the keyboard, closes it with Escape, and holds that focus is back
  on the control that opened it. A dialog chosen from an account's menu goes back to the button
  of that menu, because the menu's item is gone by then. Nothing is sent: each dialog is opened
  and closed.
*/
const DIALOG_OPENERS: {
  name: string;
  path: string;
  opener: (page: Page) => Locator;
  item?: string;
  dialog: RegExp;
}[] = [
  {
    name: 'the deposit dialog',
    path: '/dashboard',
    opener: (page) => page.getByRole('button', { name: 'Deposit', exact: true }),
    dialog: /deposit money/i,
  },
  {
    name: 'the withdrawal dialog',
    path: '/dashboard',
    opener: (page) => page.getByRole('button', { name: 'Withdraw', exact: true }),
    dialog: /withdraw money/i,
  },
  {
    name: 'the new account dialog',
    path: '/accounts',
    opener: (page) => page.getByRole('button', { name: 'Add account', exact: true }),
    dialog: /add new account/i,
  },
  {
    name: 'the rename dialog, from the menu',
    path: '/accounts',
    opener: (page) => page.getByRole('button', { name: /^Account actions for / }).first(),
    item: 'Rename',
    dialog: /rename account/i,
  },
  {
    name: 'the delete dialog, from the menu',
    path: '/accounts',
    opener: (page) => page.getByRole('button', { name: /^Account actions for / }).first(),
    item: 'Delete',
    dialog: /delete account/i,
  },
  {
    name: 'the handle dialog',
    path: '/settings',
    opener: (page) => page.getByRole('button', { name: 'Change', exact: true }),
    dialog: /change your handle/i,
  },
  {
    name: 'the Change PIN dialog',
    path: '/settings',
    opener: (page) => page.getByRole('button', { name: 'Change PIN', exact: true }),
    dialog: /change your pin/i,
  },
];

test.describe('a dialog gives focus back to the control that opened it', () => {
  for (const { name, path, opener, item, dialog } of DIALOG_OPENERS) {
    test(name, async ({ page }) => {
      await page.goto(path);
      await heading(1)(page);
      const control = opener(page);
      await control.press('Enter');
      if (item) await page.getByRole('menuitem', { name: item, exact: true }).press('Enter');

      const open = page.getByRole('dialog', { name: dialog });
      await expect(open).toBeVisible();
      // Focus is in the dialog before the key that closes it: Escape reaches no dialog from `body`.
      await expect
        .poll(() => open.evaluate((element) => element.contains(document.activeElement)))
        .toBe(true);
      await page.keyboard.press('Escape');
      await expect(open).toBeHidden();

      await expect(control, `${name}: focus did not come back to its opener`).toBeFocused();
    });
  }
});

/*
  AT PHONE WIDTH THE TWO MONEY TILES SHARE A ROW. Their grid counted its columns by the tiles'
  200 px cap, and a phone's row holds one such column: the tiles stacked, 200 px wide each, with
  the rest of the row empty (143 of 343 px at 375 px). The row reads where the two are drawn.
*/
test('at phone width the Deposit and Withdraw tiles share a row', async ({ page }) => {
  await page.setViewportSize({ width: 375, height: 812 });
  await page.goto('/dashboard');
  await heading(1)(page);
  const deposit = await page.getByRole('button', { name: 'Deposit', exact: true }).boundingBox();
  const withdraw = await page.getByRole('button', { name: 'Withdraw', exact: true }).boundingBox();
  if (deposit === null || withdraw === null) throw new Error('a tile is not drawn');

  expect(withdraw.y, 'the Withdraw tile is not level with the Deposit tile').toBe(deposit.y);
  expect(withdraw.x, 'the Withdraw tile does not start after the Deposit tile').toBeGreaterThan(
    deposit.x + deposit.width,
  );
});

/*
  AT PHONE WIDTH A BUTTON OF SETTINGS KEEPS ITS LABEL ON ONE LINE. The button of a row with a
  sentence beside it could shrink, and at 375 px the sentence won: "Change PIN" and the sign-out
  button were squeezed to their 96 px minimum and broke their labels in two. Each is held as
  tall as "Change", the page's button with one word, which cannot break.
*/
test('at phone width the buttons of Settings keep their label on one line', async ({ page }) => {
  await page.setViewportSize({ width: 375, height: 812 });
  await page.goto('/settings');
  await heading(1)(page);
  const main = page.getByRole('main');
  const oneLine = await main.getByRole('button', { name: 'Change', exact: true }).boundingBox();
  if (oneLine === null) throw new Error('the Change button is not drawn');

  for (const name of [/^Change PIN$/, /^(Log|Sign) out$/]) {
    const box = await main.getByRole('button', { name }).boundingBox();
    expect(box?.height, `${name}: the button is not one line tall`).toBe(oneLine.height);
  }
});

/*
  WORDS THAT WERE TOO FAINT TO READ, HELD AT 4.5 TO 1. The sweep reports colour contrast and
  never gates it: most of what it finds is the palette on the grey canvas, which waits for the
  UI/UX phase. The words below are not that. Each was far under the threshold for a reason of
  its own, a colour meant for something else, and was corrected by itself; a row holds each in
  both themes, so that the correction stays. The theme is asserted first, as above.
*/
const FAINT_WORDS: { name: string; reach: (page: Page) => Promise<Locator> }[] = [
  {
    // It wore the grey of the "Coming soon" rows, which are disabled: 2.54 to 1 on the card.
    name: 'the sentence under the theme choice on Settings',
    reach: async (page) => {
      await page.goto('/settings');
      return page.getByText(/^(Following your device|This device will stay)/);
    },
  },
  {
    // The same grey, on the canvas: 2.31 to 1.
    name: 'the version line on Settings',
    reach: async (page) => {
      await page.goto('/settings');
      return page.getByText(/^AzureBank v/);
    },
  },
  /*
    The red of an error icon, worn by the words that say what went wrong: 3.56 to 1 on the canvas
    and 3.92 on a card, in the light theme. The five below are those words where a visitor meets
    them without sending anything: an amount over every limit, on the transfer page and in the
    two money dialogs; a handle nobody has, which costs one lookup; and the label of the button
    that signs out, which was the same red.
  */
  {
    name: 'the message under an amount that is too large, on the transfer page',
    reach: async (page) => {
      await page.goto('/transfer');
      return amountMessage(page.getByRole('textbox', { name: 'Transfer amount' }));
    },
  },
  {
    name: 'the message under an amount that is too large, in the deposit dialog',
    reach: async (page) => {
      await page.goto('/dashboard');
      await heading(1)(page);
      await page.getByRole('button', { name: 'Deposit', exact: true }).click();
      return amountMessage(
        page
          .getByRole('dialog', { name: /deposit money/i })
          .getByRole('textbox', { name: 'Deposit amount' }),
      );
    },
  },
  {
    name: 'the message under an amount that is too large, in the withdrawal dialog',
    reach: async (page) => {
      await page.goto('/dashboard');
      await heading(1)(page);
      await page.getByRole('button', { name: 'Withdraw', exact: true }).click();
      return amountMessage(
        page
          .getByRole('dialog', { name: /withdraw money/i })
          .getByRole('textbox', { name: 'Withdraw amount' }),
      );
    },
  },
  {
    name: 'the message under a handle nobody has, on the transfer page',
    reach: async (page) => {
      await page.goto('/transfer');
      const handle = page.getByRole('textbox', { name: 'Recipient handle' });
      // A handle the API takes as one and nobody has. Longer than 20 characters and the API
      // refuses it as malformed, which the page words with another sentence.
      await handle.fill('@nobody_zz9_qx7');
      await handle.press('Enter');
      return page.getByText(/^We couldn't find /);
    },
  },
  {
    name: 'the label of the button that signs out, on Settings',
    reach: async (page) => {
      await page.goto('/settings');
      return page.getByRole('main').getByRole('button', { name: /^(Log|Sign) out$/ });
    },
  },
];

/**
 * Types an amount over every limit into an amount field and returns the message the field then
 * points at (`aria-describedby`), which is how a screen reader finds it too.
 */
async function amountMessage(amount: Locator) {
  await amount.fill('99999999');
  await expect(amount).toHaveAttribute('aria-invalid', 'true');
  const id = await amount.getAttribute('aria-describedby');
  if (id === null) throw new Error('the amount field points at no message');
  return amount.page().locator(`[id="${id}"]`);
}

test.describe('words that were too faint to read', () => {
  for (const theme of ['light', 'dark'] as const) {
    for (const { name, reach } of FAINT_WORDS) {
      test(`${name}, in the ${theme} theme`, async ({ page }) => {
        await page.emulateMedia({ colorScheme: theme });
        const words = await reach(page);
        await expect(page.locator('html')).toHaveAttribute('data-theme', theme);
        await expect(words).toBeVisible();

        const seen = await textContrast(words);
        expect(
          seen.ratio,
          `${name} (${theme}): ${seen.colours.text} on ${seen.colours.ground}`,
        ).toBeGreaterThanOrEqual(4.5);
      });
    }
  }
});

test.describe('accessibility, signed out', () => {
  // No session: login and register, reached as a visitor reaches them.
  test.use({ storageState: { cookies: [], origins: [] } });

  for (const { name, path, title, ready } of PUBLIC) {
    test(`${name} has no serious or critical finding`, async ({ page }) => {
      await page.goto(path);
      await ready(page);
      await expect(page).toHaveTitle(title);
      await scan(page, name, path);
    });
  }
});
