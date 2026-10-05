import { appendFile, mkdir, readFile } from 'node:fs/promises';
import { dirname } from 'node:path';
import { expect, test, type BrowserContext, type Page } from '@playwright/test';
import { scan } from '../e2e/axeScan';
import { focusOf } from '../e2e/focusOf';
import { DEMO_STATE, SCAN_VALUES } from '../playwright.demo.config';
import {
  KEPT_COPY_KEY,
  POOL_ADDRESS,
  answerTo,
  keptCopy,
  leaveNoSignInDetails,
  note,
  panelOf,
  shownSignInDetails,
  signOut,
  whileSignInDetailsAreShown,
} from './demoRun';

/**
 * The demo from the first click, in a real browser, on a stack with the demo on.
 *
 * One visitor, one browser context, in order: the page says it is the demo; the sign-in page
 * leads with "Try the demo"; /register is closed; a click claims a private copy and lands on its
 * dashboard; the dashboard says whose the copy is and what signs in to it; a transfer goes
 * through with the PIN the page gives; "Start over" brings another copy; after a sign-out the
 * browser offers the copy it keeps, and "Continue with my copy" signs in to it. The state is then
 * saved, signed in, for the two projects after this one (`playwright.demo.config.ts`).
 *
 * The default suite holds the other side on its own stack: with the demo off the sign-in page
 * offers "Create account" and no "Try the demo" (`e2e/auth.spec.ts`).
 *
 * EVERY TEST STANDS ON THE ONE BEFORE, so the file is serial and a red test skips the rest. The
 * context is made once and shared: each test of a file would otherwise get a new one, and a copy
 * is claimed once.
 *
 * WHAT A STEP OBSERVED IS WRITTEN DOWN (`note`): each status with a wait armed before the press
 * that sends the request, and with it the path and the headers the step is about. Never a
 * claim's answer.
 *
 * THE PASSWORD. Each claim's answer holds one. After each claim it is read from the browser's
 * storage and appended to `e2e-demo/.auth/scan-values.txt`, a file git ignores, so that whoever
 * ran this can search everything the run wrote for it and find nothing. It is compared, never
 * printed (`e2e-demo/demoRun.ts`). The sign-in details are on the page only while a helper reads
 * them or the scan runs, and the hook below takes them off after every test, however it ended.
 *
 * NAMES ARE EXACT. Playwright matches a role's name as a substring unless told otherwise, and
 * "Start over" is the first words of the dialog's title, the name of the panel's button and the
 * name of the dialog's confirm.
 *
 * WHAT HAS BEEN SEEN WHERE, as of 2026-10-05. This file has not run against the stack. Every
 * name in it was read from the components and then met in Chromium on this tree's dev server
 * with the mock and the demo's tag, where the flow below ran from the first test to the last
 * with one line changed for what only the stack does: the name of the session's cookie. That
 * line, and each other value that is the stack's to say and the mock's only to imitate, is
 * marked READ where it stands.
 */

// The words a visitor reads, typed out and not imported from the product: a test fails the day
// the page no longer says what the test was written for.
const TRY_THE_DEMO = 'Try the demo';
const CONTINUE = 'Continue with my copy';
const GET_A_NEW_COPY = 'Get a new copy';
const FORGET = 'Forget this copy';
const START_OVER = 'Start over';
const START_OVER_TITLE = 'Start over with a new copy?';
const KEEP_THIS_COPY = 'Keep this copy';
const NEW_COPY = 'You have a new copy.';

/** The tag a deployment with the demo on puts in the page's head. */
const TAG = '<meta name="azurebank-demo" content="true">';

/**
 * What a copy's two accounts hold when it is claimed, and what the dashboard adds them up to.
 * READ, from the pool's builder (`backend/tools/AzureBank.Seeder/Pool/DemoCopyBuilder.cs`) and
 * from the mock, which was given the same sums: not yet seen on the stack.
 */
const STARTING_SUM = '€14,750.00';
const STARTING_SUMS = ['€12,450.00', '€14,750.00', '€2,300.00'];
/** One euro is sent below. */
const SUM_AFTER_THE_TRANSFER = '€14,749.00';

/**
 * A page of this file's own, answered here and by no server: one paragraph whose grey on grey
 * fails colour contrast, the rule the gate reports and never fails on, so a report has markup to
 * quote. Named colours, as a fixture may: the page is no part of the app. The paragraph's words
 * stand in for a copy's sign-in details.
 */
const CANARY = 'canary-7f3a-not-a-password';
const CANARY_PATH = '/scan-canary';
const CANARY_PAGE = `<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8">
    <title>The scan's canary</title>
  </head>
  <body>
    <main>
      <h1>The scan's canary</h1>
      <p style="color: gray; background-color: silver">Password: ${CANARY}</p>
    </main>
  </body>
</html>`;

test.describe.configure({ mode: 'serial' });

test.describe('the demo, from the first click', () => {
  let context: BrowserContext;
  let page: Page;
  /** The address of the copy the first claim handed out, and of the one "Start over" did. */
  let firstAddress = '';
  let secondAddress = '';

  test.beforeAll(async ({ browser }) => {
    // A context made by hand takes the config's `use` as a test's own does: the base URL too.
    context = await browser.newContext();
    page = await context.newPage();
  });

  test.afterAll(async () => {
    await context.close();
  });

  // However a test ended, the sign-in details are off the page before Playwright looks at it.
  test.afterEach(() => leaveNoSignInDetails(page));

  /** The dashboard's one figure: what the copy's accounts add up to. */
  const sum = () => page.getByRole('heading', { level: 1 }).first();

  const buttons = (name: string) => page.getByRole('button', { name, exact: true });

  /**
   * After a claim: the password the browser now keeps goes into the file the run's output is
   * searched with. It is read from the browser, where the claim's answer put it, and goes
   * nowhere else: not into an assertion, a log or an attachment.
   */
  async function keepForTheSearch() {
    const kept = await keptCopy(page);
    expect(
      kept !== null && kept.password.length > 0,
      'after a claim the browser keeps a copy with a password',
    ).toBe(true);
    await mkdir(dirname(SCAN_VALUES), { recursive: true });
    await appendFile(SCAN_VALUES, `${kept?.password}\n`);
  }

  test("the scan leaves a node's markup out of its report when asked, and quotes it when not", async ({
    page: canaryPage,
  }) => {
    /*
      FIRST, before anything is claimed. Further down the dashboard is scanned with a copy's
      email and password on it, and that scan asks for a report with no markup. If the scan
      stopped honouring that, this is where the run stops: on words that are nobody's password,
      in a browser context of this test's own, with every later test skipped.

      It also holds the other half, which is what makes the first half an answer: the same page
      scanned with nothing asked IS quoted, so the page had markup a report would have kept.
    */
    await canaryPage.route(`**${CANARY_PATH}`, (route) =>
      route.fulfill({ contentType: 'text/html', body: CANARY_PAGE }),
    );
    await canaryPage.goto(CANARY_PATH);
    await expect(canaryPage.getByRole('heading', { level: 1 })).toBeVisible();

    const quoted = await scan(canaryPage, 'demo-scan-canary-quoted', CANARY_PATH);
    const bare = await scan(canaryPage, 'demo-scan-canary', CANARY_PATH, undefined, {
      nodeMarkup: false,
    });

    const onDisk = (name: string) => readFile(`test-results/axe/${name}.json`, 'utf8');
    const attached = (name: string) =>
      test
        .info()
        .attachments.find((attachment) => attachment.name === `axe-${name}`)
        ?.body?.toString('utf8') ?? 'no such attachment';
    const targets = (report: typeof quoted) => report.violations.flatMap((v) => v.targets);

    expect({
      quoted: {
        findings: quoted.violations.map((v) => `${v.id}: ${v.nodes}`),
        targets: targets(quoted).length,
        targetsWithMarkup: targets(quoted).filter((target) => 'html' in target).length,
        returnedHoldsTheWords: JSON.stringify(quoted).includes(CANARY),
        onDiskHoldsTheWords: (await onDisk('demo-scan-canary-quoted')).includes(CANARY),
        attachedHoldsTheWords: attached('demo-scan-canary-quoted').includes(CANARY),
      },
      bare: {
        findings: bare.violations.map((v) => `${v.id}: ${v.nodes}`),
        targets: targets(bare).length,
        targetsWithMarkup: targets(bare).filter((target) => 'html' in target).length,
        sameSelectors:
          JSON.stringify(targets(bare).map((target) => target.target)) ===
          JSON.stringify(targets(quoted).map((target) => target.target)),
        returnedHoldsTheWords: JSON.stringify(bare).includes(CANARY),
        onDiskHoldsTheWords: (await onDisk('demo-scan-canary')).includes(CANARY),
        attachedHoldsTheWords: attached('demo-scan-canary').includes(CANARY),
      },
    }).toEqual({
      quoted: {
        findings: ['color-contrast: 1'],
        targets: 1,
        targetsWithMarkup: 1,
        returnedHoldsTheWords: true,
        onDiskHoldsTheWords: true,
        attachedHoldsTheWords: true,
      },
      bare: {
        findings: ['color-contrast: 1'],
        targets: 1,
        targetsWithMarkup: 0,
        sameSelectors: true,
        returnedHoldsTheWords: false,
        onDiskHoldsTheWords: false,
        attachedHoldsTheWords: false,
      },
    });
  });

  test("the page carries the demo's tag, once, before the end of its head", async () => {
    // Asked as a request, not through the browser: these are the bytes the BFF serves, before
    // any script of the page has run.
    const answer = await context.request.get('/');
    const html = await answer.text();
    const at = html.indexOf(TAG);
    await note('the page', {
      path: '/',
      status: answer.status(),
      'content-type': answer.headers()['content-type'],
    });

    expect({
      status: answer.status(),
      tags: html.split(TAG).length - 1,
      beforeTheHeadEnds: at !== -1 && at < html.indexOf('</head>'),
    }).toEqual({ status: 200, tags: 1, beforeTheHeadEnds: true });
  });

  test('the sign-in page leads with "Try the demo", and has no finding the gate fails on', async () => {
    await page.goto('/login');
    await expect(page.getByLabel('Email address', { exact: true })).toBeVisible();
    await expect(page).toHaveTitle('Sign in · AzureBank');

    expect({
      // The tag as the app reads it (src/features/demo/demoMode.ts): in the page, saying true.
      tagsThePageReads: await page.locator('meta[name="azurebank-demo"][content="true"]').count(),
      tagsOfThatName: await page.locator('meta[name="azurebank-demo"]').count(),
      tryTheDemo: await buttons(TRY_THE_DEMO).count(),
      continueWithMyCopy: await buttons(CONTINUE).count(),
      createAccount: await page.getByRole('link', { name: 'Create account', exact: true }).count(),
      // A context nobody has claimed in keeps nothing.
      keepsNoCopy: (await keptCopy(page)) === null,
    }).toEqual({
      tagsThePageReads: 1,
      tagsOfThatName: 1,
      tryTheDemo: 1,
      continueWithMyCopy: 0,
      createAccount: 0,
      keepsNoCopy: true,
    });

    await scan(page, 'demo-login', '/login');
  });

  test('/register is closed: its address leads to the sign-in page', async () => {
    // With a query and a fragment, so that "nothing is carried along" below is about something.
    await page.goto('/register?next=%2Fdashboard#frag');

    await expect.poll(() => new URL(page.url()).pathname).toBe('/login');
    await expect(buttons(TRY_THE_DEMO)).toBeVisible();
    const landed = new URL(page.url());
    expect({ path: landed.pathname, query: landed.search, fragment: landed.hash }).toEqual({
      path: '/login',
      query: '',
      fragment: '',
    });
  });

  test('"Try the demo" claims a copy, signs the visitor in and lands on the dashboard', async () => {
    const claimed = answerTo(page, 'POST', '/bff/auth/demo/claim');
    await buttons(TRY_THE_DEMO).click();
    const answer = await claimed;

    // Read here to be measured, and never written down whole: it holds the copy's password.
    const body = (await answer.json()) as {
      message?: unknown;
      data?: {
        copy?: { password?: unknown; pin?: unknown; contacts?: unknown; expiresAt?: unknown };
      };
    };
    const copy = body.data?.copy;
    const password = typeof copy?.password === 'string' ? copy.password : '';
    const cookies = (await context.cookies()).map((cookie) => cookie.name);
    await note('the claim', {
      path: '/bff/auth/demo/claim',
      status: answer.status(),
      'cache-control': answer.headers()['cache-control'],
      message: body.message,
      // The copy's end as the server wrote it: its form is what the app's check of a claim reads.
      copyEnds: copy?.expiresAt,
      copyEndsInZ: typeof copy?.expiresAt === 'string' && copy.expiresAt.endsWith('Z'),
      contacts: Array.isArray(copy?.contacts) ? copy.contacts.length : null,
      passwordLength: password.length,
      passwordIsFourGroupsOfFour: /^[A-Za-z0-9]{4}(-[A-Za-z0-9]{4}){3}$/.test(password),
      cookies,
    });

    await expect(page).toHaveURL(/\/dashboard(?:[?#]|$)/, { timeout: 15_000 });
    await keepForTheSearch();
    const kept = await keptCopy(page);

    expect({
      status: answer.status(),
      cacheControl: answer.headers()['cache-control'],
      // The digits the page prints under its PIN boxes are the copy's own.
      pinIsTheDemoPin: copy?.pin === '123456',
      // What the browser keeps is what the claim answered: the copy's end, not the session's.
      keepsTheCopysEnd: kept?.expiresAt === copy?.expiresAt,
      keepsTheCopysPassword: kept?.password === password,
      // READ, from the BFF's source (backend/src/AzureBank.Bff/Program.cs: the prefix outside
      // Development), not yet seen in this browser on the stack: the session's cookie is a
      // `__Host-` one, and Chromium keeps it although the page came over http from localhost.
      sessionCookieIsHostOnly: cookies.some((name) => name.startsWith('__Host-')),
    }).toEqual({
      status: 200,
      cacheControl: 'no-store',
      pinIsTheDemoPin: true,
      keepsTheCopysEnd: true,
      keepsTheCopysPassword: true,
      sessionCookieIsHostOnly: true,
    });
  });

  test("the dashboard shows the copy's money, says whose the copy is and what signs in to it", async () => {
    await expect(sum()).toHaveText(STARTING_SUM);
    // The two accounts' own sums are in the buttons that choose which account the page is
    // about, beside the total.
    const scope = page.getByRole('group', { name: 'Account scope' });
    await expect(scope.getByRole('button')).toHaveCount(3);
    const sums = (await scope.getByRole('button').allTextContents()).map(
      (text) => /€[\d,]+\.\d{2}$/.exec(text)?.[0] ?? text,
    );
    expect(sums.sort()).toEqual(STARTING_SUMS);

    const panel = panelOf(page);
    await expect(panel.getByRole('heading', { level: 2, name: 'Your private copy' })).toBeVisible();
    expect(await panel.locator('p').allTextContents()).toEqual([
      expect.stringMatching(
        /^Other visitors can't see this copy\. It works until .+ · .+, then it is closed and deleted\.$/,
      ),
      'PIN: 123456, unless you changed it',
      expect.stringMatching(/^Contacts you can pay: @\S+ and @\S+$/),
    ]);
    expect({
      showSignInDetails: await panel
        .getByRole('button', { name: 'Show sign-in details', exact: true })
        .count(),
      startOver: await panel.getByRole('button', { name: START_OVER, exact: true }).count(),
    }).toEqual({ showSignInDetails: 1, startOver: 1 });

    const shown = await shownSignInDetails(page);
    const kept = await keptCopy(page);
    firstAddress = shown.email;
    expect({
      addressIsOfThePool: POOL_ADDRESS.test(shown.email),
      showsTheKeptAddress: shown.email === kept?.email,
      showsTheKeptPassword: shown.password === kept?.password,
    }).toEqual({ addressIsOfThePool: true, showsTheKeptAddress: true, showsTheKeptPassword: true });
  });

  test('the dashboard has no finding the gate fails on, with the sign-in details closed and open', async () => {
    await expect(page).toHaveTitle('Home · AzureBank');
    await expect(sum()).toHaveText(STARTING_SUM);

    // Closed, as every visitor first sees it. The button then names, with `aria-controls`, an
    // element that is not in the page: what axe makes of that is written down.
    const closed = await scan(page, 'demo-dashboard', '/dashboard');
    await note('aria-controls with the details closed', {
      'aria-valid-attr-value': {
        violation: closed.violations.some((v) => v.id === 'aria-valid-attr-value'),
        incomplete: closed.incomplete.some((r) => r.id === 'aria-valid-attr-value'),
      },
    });

    // Open: the copy's email and password are on the page, so the report quotes no markup, and
    // nothing but the scan looks at the page until they are off it again.
    const open = await whileSignInDetailsAreShown(page, () =>
      scan(page, 'demo-dashboard-details-open', '/dashboard', undefined, { nodeMarkup: false }),
    );
    expect(open.violations.flatMap((v) => v.targets).filter((target) => 'html' in target)).toEqual(
      [],
    );
  });

  test("a transfer to the copy's first contact goes through, with the PIN the page gives", async () => {
    // The handle is read off the panel: each copy has contacts of its own.
    const contacts = await panelOf(page)
      .getByText(/^Contacts you can pay: /)
      .textContent();
    const handle = /^Contacts you can pay: @(\S+)/.exec(contacts ?? '')?.[1] ?? '';
    expect(handle, 'the panel names a contact').not.toBe('');

    // By the app's own navigation, as a visitor goes: the wizard's names are the ones the
    // screenshot capture drives (screenshots/app.capture.ts).
    await page
      .getByRole('navigation', { name: 'Main navigation' })
      .getByRole('button', { name: 'Transfer' })
      .click();
    await expect(page.getByRole('heading', { level: 1, name: 'Send Money' })).toBeVisible();
    await page.getByRole('textbox', { name: 'Recipient handle' }).fill(handle);
    await page.getByRole('button', { name: 'Verify' }).click();
    await expect(page.getByText(`@${handle}`).first()).toBeVisible();
    await page.getByRole('textbox', { name: 'Transfer amount' }).fill('1');
    await page.getByRole('button', { name: 'Review Transfer' }).click();
    await expect(page.getByRole('heading', { level: 1, name: 'Review Transfer' })).toBeVisible();
    await page.getByRole('button', { name: 'Continue' }).click();
    await expect(page.getByRole('heading', { level: 1, name: 'Confirm with PIN' })).toBeVisible();

    // The PIN is the one the page prints under the boxes, and not one this file knows: a
    // visitor has no other.
    const hint = page.getByText(/^Demo PIN: \d{6}, unless you changed it\.$/);
    await expect(hint).toHaveCount(1);
    const digits = /Demo PIN: (\d{6})/.exec((await hint.textContent()) ?? '')?.[1] ?? '';
    expect(digits, 'the line under the boxes gives six digits').toMatch(/^\d{6}$/);

    // The sixth digit sends: first the authorisation the PIN buys, then the transfer.
    const authorised = answerTo(page, 'POST', '/api/transfers/authorizations');
    const sent = answerTo(page, 'POST', '/api/transfers');
    await page.getByRole('textbox', { name: 'Digit 1 of 6' }).pressSequentially(digits);
    const [authorisation, transfer] = await Promise.all([authorised, sent]);
    await note('the transfer', {
      authorisation: { path: '/api/transfers/authorizations', status: authorisation.status() },
      transfer: { path: '/api/transfers', status: transfer.status() },
    });
    // READ, from the API's source (backend/src/AzureBank.Api/Controllers/TransferController.cs)
    // and from the mock, not yet seen on the stack: 201 and 201.
    expect({ authorisation: authorisation.status(), transfer: transfer.status() }).toEqual({
      authorisation: 201,
      transfer: 201,
    });
    await expect(page.getByRole('heading', { level: 1, name: 'Transfer Complete' })).toBeVisible({
      timeout: 20_000,
    });

    // Back to the dashboard, which now adds up to one euro less: the sum a new copy must not show.
    await page.getByRole('button', { name: 'Done', exact: true }).click();
    await expect(sum()).toHaveText(SUM_AFTER_THE_TRANSFER);
  });

  test('"Start over" asks first, takes the keyboard, and brings a new copy', async () => {
    // From the keyboard: where focus goes is half of what this test is about.
    await panelOf(page).getByRole('button', { name: START_OVER, exact: true }).focus();
    expect(await focusOf(page)).toEqual({ on: START_OVER, inTheDialog: false });
    await page.keyboard.press('Enter');
    const dialog = page.getByRole('alertdialog', { name: START_OVER_TITLE });
    await expect(dialog).toBeVisible();

    // Focus is in the dialog, on its first control, and Tab goes round inside it, both ways.
    await expect.poll(() => focusOf(page)).toEqual({ on: 'Close', inTheDialog: true });
    await page.keyboard.press('Tab');
    expect(await focusOf(page)).toEqual({ on: KEEP_THIS_COPY, inTheDialog: true });
    await page.keyboard.press('Tab');
    expect(await focusOf(page)).toEqual({ on: START_OVER, inTheDialog: true });
    await page.keyboard.press('Tab');
    expect(await focusOf(page)).toEqual({ on: 'Close', inTheDialog: true });
    await page.keyboard.press('Shift+Tab');
    expect(await focusOf(page)).toEqual({ on: START_OVER, inTheDialog: true });

    // Written down, not asserted: what a pointer over the navigation's first link would meet
    // while the dialog is open.
    await note('the open dialog and the navigation', {
      atTheNavigationsFirstLink: await page.evaluate(() => {
        const link = Array.from(
          document.querySelectorAll('nav[aria-label="Main navigation"] a'),
        ).find((candidate) => candidate.getClientRects().length > 0);
        if (link === undefined) return 'no link of the navigation is drawn';
        const box = link.getBoundingClientRect();
        const top = document.elementFromPoint(box.left + box.width / 2, box.top + box.height / 2);
        if (top === null) return 'nothing';
        if (link.contains(top)) return 'the link itself';
        if (top.closest('[role="alertdialog"]') !== null) return 'the dialog';
        return top.querySelector('[role="alertdialog"]') !== null ? 'the overlay' : top.tagName;
      }),
    });

    // One confirm dialog is in the page, and it is this one: the scan's scope names one element.
    await scan(page, 'demo-start-over-dialog', '/dashboard', '[role="alertdialog"]');

    // Confirmed inside the dialog, where focus is: the panel behind has a button of this name.
    expect(await focusOf(page)).toEqual({ on: START_OVER, inTheDialog: true });
    const claimed = answerTo(page, 'POST', '/bff/auth/demo/claim');
    await page.keyboard.press('Enter');
    const answer = await claimed;
    await note('the second claim', {
      path: '/bff/auth/demo/claim',
      status: answer.status(),
      'cache-control': answer.headers()['cache-control'],
    });
    expect(answer.status()).toBe(200);

    await expect(dialog).toBeHidden();
    await expect(page.getByText(NEW_COPY).first()).toBeVisible();
    // Focus is given back to the button the dialog was opened from.
    await expect.poll(() => focusOf(page)).toEqual({ on: START_OVER, inTheDialog: false });
    await keepForTheSearch();

    // The new copy's money, not the first one's: the first had sent a euro.
    await expect(sum()).toHaveText(STARTING_SUM);
    const shown = await shownSignInDetails(page);
    const kept = await keptCopy(page);
    secondAddress = shown.email;
    expect({
      addressIsOfThePool: POOL_ADDRESS.test(shown.email),
      aNewAddress: shown.email !== firstAddress,
      showsTheKeptAddress: shown.email === kept?.email,
      showsTheKeptPassword: shown.password === kept?.password,
    }).toEqual({
      addressIsOfThePool: true,
      aNewAddress: true,
      showsTheKeptAddress: true,
      showsTheKeptPassword: true,
    });
  });

  test('signed out, the sign-in page offers the copy this browser keeps', async () => {
    await signOut(page);

    await expect(
      page.getByText(/^This browser remembers a demo copy\. It works until .+\.$/),
    ).toBeVisible();
    expect({
      continueWithMyCopy: await buttons(CONTINUE).count(),
      getANewCopy: await buttons(GET_A_NEW_COPY).count(),
      forgetThisCopy: await buttons(FORGET).count(),
      tryTheDemo: await buttons(TRY_THE_DEMO).count(),
      // The sign-out left the copy where it was.
      stillKeepsTheSecondCopy: (await keptCopy(page))?.email === secondAddress,
    }).toEqual({
      continueWithMyCopy: 1,
      getANewCopy: 1,
      forgetThisCopy: 1,
      tryTheDemo: 0,
      stillKeepsTheSecondCopy: true,
    });
  });

  test('"Continue with my copy" signs in to the kept copy, and the state is saved signed in', async () => {
    const signedIn = answerTo(page, 'POST', '/bff/auth/login');
    await buttons(CONTINUE).click();
    const answer = await signedIn;
    await note('continue with my copy', { path: '/bff/auth/login', status: answer.status() });
    expect(answer.status()).toBe(200);

    await expect(page).toHaveURL(/\/dashboard(?:[?#]|$)/, { timeout: 15_000 });
    await expect(sum()).toHaveText(STARTING_SUM);
    const shown = await shownSignInDetails(page);
    expect({ theCopyIsTheSecond: shown.email === secondAddress }).toEqual({
      theCopyIsTheSecond: true,
    });

    /*
      SAVED SIGNED IN, on purpose. The project after the next one asserts that the session is
      gone once the two containers have restarted. Saved signed out, the state would hold no
      cookie, and "no session" would be true with or without a restart.
    */
    await mkdir(dirname(DEMO_STATE), { recursive: true });
    await context.storageState({ path: DEMO_STATE });
    const saved = JSON.parse(await readFile(DEMO_STATE, 'utf8')) as {
      cookies: { name: string }[];
      origins: { localStorage: { name: string }[] }[];
    };
    expect({
      cookies: saved.cookies.length > 0,
      keepsTheCopy: saved.origins.some((origin) =>
        origin.localStorage.some((entry) => entry.name === KEPT_COPY_KEY),
      ),
    }).toEqual({ cookies: true, keepsTheCopy: true });
  });
});
