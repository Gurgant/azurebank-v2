import { mkdir, readFile } from 'node:fs/promises';
import {
  expect,
  test,
  type Browser,
  type BrowserContextOptions,
  type Page,
} from '@playwright/test';
import { socialCard } from './card.ts';

/**
 * Every picture of the app, taken the way the e2e suite drives it: a real Chromium, the built SPA
 * served by the BFF under its production CSP, the real API and SQL Server — signed in as John, the
 * seeded user with a ledger (the e2e suite's admin account has none).
 *
 * It asserts only what it needs to know a screen has finished loading: the seeded rows are on it,
 * no spinner is. Locators come from the accessibility tree, as the e2e suite's do (ADR-0031), so the
 * script should outlive the UI/UX phase's restyling; a red run after it means a picture moved, and
 * the list below is what to check against, not just the exit code.
 *
 * Output: screenshots/output/<screen>-<device>-<theme>.png, a WebM of one transfer, and the two
 * social cards. `npm run capture:publish` picks the ones the README uses.
 */

const OUT = 'screenshots/output';
const AUTH = 'screenshots/.auth/john.json';
const JOHN = { email: 'john@example.com', password: 'Test123!', pin: '123456' };

type Theme = 'light' | 'dark';
const THEMES: readonly Theme[] = ['light', 'dark'];

// Captured at 2x (3x on the phone): GitHub shows a README image about 800px wide, and a retina
// screen wants twice that. `capture:publish` resizes from these.
const DESKTOP: BrowserContextOptions = {
  viewport: { width: 1440, height: 900 },
  deviceScaleFactor: 2,
};
const MOBILE: BrowserContextOptions = {
  viewport: { width: 390, height: 844 },
  deviceScaleFactor: 3,
  isMobile: true,
  hasTouch: true,
};

test.describe.configure({ mode: 'serial' });

async function open(
  browser: Browser,
  device: BrowserContextOptions,
  theme: Theme,
  signedIn = true,
) {
  const context = await browser.newContext({
    ...device,
    colorScheme: theme,
    ...(signedIn ? { storageState: AUTH } : {}),
  });
  return { context, page: await context.newPage() };
}

async function shot(page: Page, name: string) {
  await page.evaluate(() => document.fonts.ready);
  await page.screenshot({ path: `${OUT}/${name}.png`, animations: 'disabled', caret: 'hide' });
}

// ---- What "finished loading" means, per screen ------------------------------------------------

// The recent-activity rows render as empty skeletons first; the first one holding text is the list.
async function dashboardReady(page: Page) {
  await expect(page.getByText(/Across 2 accounts/)).toBeVisible();
  await expect(page.getByRole('main').getByRole('row').nth(1)).toHaveText(/\S/);
}

async function historyReady(page: Page) {
  await expect(page.getByRole('heading', { level: 1, name: 'History' })).toBeVisible();
  await expect(page.getByRole('progressbar', { name: 'Loading transactions' })).toBeHidden();
  await expect(page.getByText('Monthly rent')).toBeVisible();
}

async function accountsReady(page: Page) {
  await expect(page.getByRole('heading', { level: 1, name: 'Accounts' })).toBeVisible();
  await expect(page.getByText('Main Savings').first()).toBeVisible();
  await expect(page.getByText(/AB-•+-•+-90/).first()).toBeVisible();
}

// ---- The screens ---------------------------------------------------------------------------------

test('sign-in page, then sign in once for everything after it', async ({ browser, page }) => {
  await mkdir(OUT, { recursive: true });
  for (const theme of THEMES) {
    const { context, page: login } = await open(browser, DESKTOP, theme, false);
    await login.goto('/login');
    await expect(login.getByLabel('Email address', { exact: true })).toBeVisible();
    await shot(login, `login-desktop-${theme}`);
    await context.close();
  }

  // One sign-in, reused through storageState: the BFF limits sign-ins per IP, and a session is
  // what the e2e suite reuses the same way (auth.setup.ts).
  await page.goto('/login');
  await page.getByLabel('Email address', { exact: true }).fill(JOHN.email);
  await page.getByLabel('Password', { exact: true }).fill(JOHN.password);
  await page.getByRole('button', { name: /sign in|log ?in/i }).click();
  await dashboardReady(page);
  await mkdir('screenshots/.auth', { recursive: true });
  await page.context().storageState({ path: AUTH });
});

test('dashboard, history and accounts, on a desktop and a phone', async ({ browser }) => {
  for (const theme of THEMES) {
    const desktop = await open(browser, DESKTOP, theme);
    await desktop.page.goto('/dashboard');
    await dashboardReady(desktop.page);
    // The seeded ledger and nothing else yet: its newest row is today's refund.
    await expect(desktop.page.getByText('Refund - Return item')).toBeVisible();
    await shot(desktop.page, `dashboard-desktop-${theme}`);

    await desktop.page.goto('/history');
    await historyReady(desktop.page);
    await shot(desktop.page, `history-desktop-${theme}`);

    await desktop.page.goto('/accounts');
    await accountsReady(desktop.page);
    await shot(desktop.page, `accounts-desktop-${theme}`);
    await desktop.context.close();

    const phone = await open(browser, MOBILE, theme);
    await phone.page.goto('/dashboard');
    await dashboardReady(phone.page);
    await shot(phone.page, `dashboard-mobile-${theme}`);
    await phone.context.close();
  }
});

test('the PIN that reveals an account number', async ({ browser }) => {
  for (const theme of THEMES) {
    const { context, page } = await open(browser, DESKTOP, theme);
    await page.goto('/accounts');
    await accountsReady(page);
    await page
      .getByRole('button', { name: /Reveal full account number/i })
      .first()
      .click();
    const modal = page.getByRole('alertdialog', { name: /verify it's you/i });
    await expect(modal).toBeVisible();
    await shot(page, `step-up-desktop-${theme}`);
    // Cancelled, not answered: an answered PIN elevates the session every later picture shares.
    await modal.getByRole('button', { name: 'Cancel' }).click();
    await expect(modal).toBeHidden();
    await context.close();
  }
});

// From here on, money moves. Everything above is already taken.

async function sendToJane(page: Page, amount: string, onStep?: (step: string) => Promise<unknown>) {
  await page.getByRole('textbox', { name: 'Recipient handle' }).fill('janesmith');
  await page.getByRole('button', { name: 'Verify' }).click();
  await expect(page.getByText('@janesmith').first()).toBeVisible();
  await page.getByRole('textbox', { name: 'Transfer amount' }).fill(amount);
  await onStep?.('form');
  await page.getByRole('button', { name: 'Review Transfer' }).click();
  await expect(page.getByRole('heading', { level: 1, name: 'Review Transfer' })).toBeVisible();
  await onStep?.('review');
  await page.getByRole('button', { name: 'Continue' }).click();
  await expect(page.getByRole('heading', { level: 1, name: 'Confirm with PIN' })).toBeVisible();
  await onStep?.('pin');
  // The transfer sends as soon as the sixth digit is in.
  for (const [index, digit] of [...JOHN.pin].entries()) {
    await page.getByRole('textbox', { name: `Digit ${index + 1} of 6` }).fill(digit);
  }
  await expect(page.getByRole('heading', { level: 1, name: 'Transfer Complete' })).toBeVisible({
    timeout: 20_000,
  });
  await onStep?.('done');
}

test('one transfer, recorded', async ({ browser }) => {
  /*
    Recorded at 1x in a light theme: a README animation is watched at its own size, and a GIF's
    palette holds a light UI far better than a dark one. The pauses are for a person watching;
    nothing waits on them. FIRST of the money-moving pictures, so the dashboard it opens on is the
    seeded one and the only new row in it is the transfer it records.
  */
  const context = await browser.newContext({
    viewport: DESKTOP.viewport,
    colorScheme: 'light',
    storageState: AUTH,
    recordVideo: { dir: `${OUT}/video`, size: DESKTOP.viewport! },
  });
  const page = await context.newPage();
  const pause = () => page.waitForTimeout(900);
  await page.goto('/dashboard');
  await dashboardReady(page);
  await pause();
  // In the page's own quick actions: the navigation has a "Transfer" button too.
  await page
    .getByRole('main')
    .getByRole('button', { name: /^Transfer/ })
    .click();
  await expect(page.getByRole('heading', { level: 1, name: 'Send Money' })).toBeVisible();
  await expect(page.getByText(/Available: €[1-9]/)).toBeVisible();
  await pause();
  await sendToJane(page, '25', pause);
  await pause();
  await page.getByRole('button', { name: 'Done' }).click();
  await dashboardReady(page);
  await pause();
  await context.close();
  await page.video()!.saveAs(`${OUT}/transfer-flow.webm`);
});

test('a transfer: the form, the review, the PIN, the receipt', async ({ browser }) => {
  for (const theme of THEMES) {
    const { context, page } = await open(browser, DESKTOP, theme);
    await page.goto('/transfer');
    await expect(page.getByRole('heading', { level: 1, name: 'Send Money' })).toBeVisible();
    await expect(page.getByText(/Available: €[1-9]/)).toBeVisible();
    await sendToJane(page, '40', (step) => shot(page, `transfer-${step}-desktop-${theme}`));
    await context.close();
  }
});

test('a deposit and its receipt', async ({ browser }) => {
  for (const theme of THEMES) {
    const { context, page } = await open(browser, DESKTOP, theme);
    await page.goto('/dashboard');
    await dashboardReady(page);
    await page.getByRole('button', { name: 'Deposit', exact: true }).click();
    const dialog = page.getByRole('dialog', { name: /deposit money/i });
    await expect(dialog).toBeVisible();
    await dialog.getByRole('textbox', { name: 'Deposit amount' }).fill('250');
    await dialog.getByRole('textbox', { name: 'Description' }).fill('Birthday gift');
    await dialog.getByRole('button', { name: /^Deposit(\s|$)/ }).click();
    await expect(page.getByRole('dialog', { name: /deposit complete/i })).toBeVisible({
      timeout: 20_000,
    });
    await shot(page, `deposit-receipt-desktop-${theme}`);
    await context.close();
  }
});

test('the social cards: GitHub preview and LinkedIn', async ({ page }) => {
  /*
    Composed in HTML and photographed, so the card is rebuilt from the same dashboard picture
    whenever the pictures are. GitHub's social preview is 1280x640 (under 1 MB); LinkedIn's link card
    is 1.91:1, 1200x627. The template keeps its text away from the edges both crop.
  */
  const hero = (await readFile(`${OUT}/dashboard-desktop-light.png`)).toString('base64');
  const logo = (await readFile('public/logo.svg')).toString('base64');
  const html = socialCard(`data:image/png;base64,${hero}`, `data:image/svg+xml;base64,${logo}`);

  for (const [name, width, height] of [
    ['social-preview', 1280, 640],
    ['linkedin-card', 1200, 627],
  ] as const) {
    await page.setViewportSize({ width, height });
    await page.setContent(html, { waitUntil: 'load' });
    await page.evaluate(() => document.fonts.ready);
    await page.screenshot({ path: `${OUT}/${name}.png` });
  }
});
