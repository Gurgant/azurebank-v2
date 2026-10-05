import { appendFile, mkdir, readFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { expect, test, type BrowserContext, type Page, type Response } from '@playwright/test';
import { scan } from '../e2e/axeScan';
import { focusOf } from '../e2e/focusOf';
import { DEMO_STATE, SCAN_VALUES } from '../playwright.demo.config';
import {
  KEPT_COPY_KEY,
  POOL_ADDRESS,
  answerTo,
  hearPolicyViolations,
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
 * THE PASSWORD. Each claim's answer holds one. It is taken from the answer and appended to
 * `e2e-demo/.auth/scan-values.txt`, a file git ignores, before anything is expected of the
 * answer or of the page, so that whoever ran this can search everything the run wrote for it and
 * find nothing, after a red run as after a green one. Each test that claims then holds that the
 * file knows the password the browser keeps. It is compared, never printed
 * (`e2e-demo/demoRun.ts`). The sign-in details are on the page only while a helper reads them
 * or the scan runs, and the hook below takes them off after every test, however it ended.
 *
 * NAMES ARE EXACT. Playwright matches a role's name as a substring unless told otherwise, and
 * "Start over" is the first words of the dialog's title, the name of the panel's button and the
 * name of the dialog's confirm. A text is looked for as a part too. So the names and sentences
 * the demo adds are asked for whole, with `exact` or a pattern that ends where they end,
 * wherever a page that said more than the words typed here would otherwise pass. The
 * transfer's names are the app's older ones, asked as the screenshot capture asks them
 * (`screenshots/app.capture.ts`).
 *
 * WHAT HAS BEEN SEEN WHERE. On 2026-10-05 this file ran from the first test to the last, in
 * Chromium 151 against compose.yaml with compose.demo.yaml (Production, the images built from
 * the tree as it then stood, a pool nobody had claimed from): thirteen passed, the thirteen it
 * then had, and no name in it had to be changed. Before that every name had been met on this
 * tree's dev server with the mock and the demo's tag, where the flow passes with one line
 * changed: the name of the session's cookie, which only the stack gives. Each value that is the
 * stack's to say and the mock's only to imitate is marked MEASURED where it stands, with what
 * the stack said. A test added since that run says so where it stands, with where it has run.
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
 * The pool's builder gives the two (`backend/tools/AzureBank.Seeder/Pool/DemoCopyBuilder.cs`),
 * and the mock was given the same. MEASURED on the stack, 2026-10-05: the dashboard of the first
 * copy showed these three; the sum was read again on the copy "Start over" brought and after
 * "Continue with my copy"; and the sum further down once a euro had been sent.
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

/**
 * Pages of this file's own for the sign-in details, answered here as the scan's canary is: a
 * button that says the details are open, the line it controls, which holds the canary's words,
 * and a script that says what the browser keeps and what a press on the button does.
 */
const DETAILS_PATH = '/details-canary';
const detailsPage = (button: string, script: string) => `<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8">
    <title>The sign-in details' canary</title>
  </head>
  <body>
    <main>
      <h1>The sign-in details' canary</h1>
      <button type="button" aria-expanded="true" aria-controls="details">${button}</button>
      <p id="details">Password: ${CANARY}</p>
    </main>
    <script>
      ${script}
    </script>
  </body>
</html>`;
/** What the browser is told it keeps: a copy whose password is the canary's words, or none. */
const THE_KEY = JSON.stringify(KEPT_COPY_KEY);
const A_COPY = JSON.stringify(JSON.stringify({ password: CANARY }));
const KEEPS_THE_WORDS = `localStorage.setItem(${THE_KEY}, ${A_COPY});`;
const KEEPS_NOTHING = `localStorage.removeItem(${THE_KEY});`;
/** A press takes the line off the page and turns the button round, as the panel's does. */
const A_PRESS_HIDES = `document.querySelector('button').addEventListener('click', (event) => {
        document.getElementById('details').remove();
        event.currentTarget.setAttribute('aria-expanded', 'false');
        event.currentTarget.textContent = 'Show sign-in details';
      });`;
const DETAILS_PAGES = {
  // As the dashboard's panel has them: the button by its name, and a press that takes them off.
  asThePanelHasThem: detailsPage('Hide sign-in details', `${KEEPS_THE_WORDS} ${A_PRESS_HIDES}`),
  // Under a button of another name. A press would take them off here too, for whoever knew it.
  underAnotherName: detailsPage('Hide the details', `${KEEPS_THE_WORDS} ${A_PRESS_HIDES}`),
  // The button by its name, a press that does nothing, and a browser that keeps no copy to
  // look for.
  stuckAndNothingKept: detailsPage('Hide sign-in details', KEEPS_NOTHING),
};
const THE_PAGE_WAS_LEFT =
  'The sign-in details could not be taken off the page, so the page was left.';

/**
 * A page of this file's own for the policy's listener, answered here under a policy that its one
 * styled paragraph breaks: a violation that is due, so that a listener that hears none on the
 * demo's screens is known to hear.
 */
const POLICY_CANARY_PATH = '/policy-canary';
const POLICY_CANARY_POLICY = "default-src 'self'";
const POLICY_CANARY_PAGE = `<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8">
    <title>The policy's canary</title>
  </head>
  <body>
    <main>
      <h1>The policy's canary</h1>
      <p style="color: gray">A style the policy refuses.</p>
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
  /** Every Content-Security-Policy violation a page of the shared context raised, as it came. */
  let policyViolations: string[] = [];

  test.beforeAll(async ({ browser }) => {
    // A context made by hand takes the config's `use` as a test's own does: the base URL too.
    context = await browser.newContext();
    // Before the first page: the listener is in every page of the context from its first script.
    policyViolations = await hearPolicyViolations(context);
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
   * THE FIRST THING DONE WITH A CLAIM'S ANSWER, before anything is expected of it or of the
   * page: the password it holds goes into the file the run's output is searched with. A step
   * that goes red further down has then left the search what it needs: the copy is claimed and
   * its password is in the browser whether or not the test went on.
   *
   * The answer is read here to be measured, and never written down whole. The password goes
   * nowhere else: not into an assertion, a log or an attachment. An answer that holds none (a
   * refused claim, or one that is no JSON) adds no line, and what is expected of the answer
   * afterwards says so: an empty line in that file would match every file searched.
   */
  async function keepForTheSearch(answer: Response) {
    const body = (await answer.json().catch(() => ({}))) as {
      message?: unknown;
      data?: {
        copy?: { password?: unknown; pin?: unknown; contacts?: unknown; expiresAt?: unknown };
      };
    };
    const copy = body.data?.copy;
    const password = typeof copy?.password === 'string' ? copy.password : '';
    if (password !== '') {
      await mkdir(dirname(SCAN_VALUES), { recursive: true });
      await appendFile(SCAN_VALUES, `${password}\n`);
    }
    return { message: body.message, copy, password };
  }

  /**
   * Whether the file the run's output is searched with holds the password the browser keeps:
   * asked after each claim, of the browser and not of the answer, because what the browser
   * keeps is what the dashboard shows and what the saved state holds.
   */
  async function theSearchKnows(kept: Awaited<ReturnType<typeof keptCopy>>) {
    const lines = (await readFile(SCAN_VALUES, 'utf8').catch(() => '')).split('\n');
    return kept !== null && kept.password !== '' && lines.includes(kept.password);
  }

  test('this run keeps no trace, no screenshot and no video, in any of its projects', () => {
    // CONTROL: green before this change
    /*
      BEFORE ANYTHING ELSE. A trace of a red test would hold a claim's answer, password and
      all, and a screenshot could be of the dashboard with the sign-in details open
      (`playwright.demo.config.ts`). A run that kept them would look like any other until a
      test of it went red. So the first test asks what this run was started with, for each of
      its three projects and with whatever the command line added (`--trace on`): asked here, a
      wrong answer stops the run before a copy is claimed.
    */
    const kept = test.info().config.projects.map(({ name, use }) => ({
      project: name,
      trace: use.trace,
      screenshot: use.screenshot,
      video: use.video,
    }));
    expect(kept).toEqual([
      { project: 'demo', trace: 'off', screenshot: 'off', video: 'off' },
      { project: 'restart', trace: 'off', screenshot: 'off', video: 'off' },
      { project: 'demo-after-restart', trace: 'off', screenshot: 'off', video: 'off' },
    ]);
  });

  test('the saved state and the passwords for the search are written under e2e-demo/.auth, wherever the run was started', () => {
    /*
      BEFORE ANYTHING IS CLAIMED, as the test above. The run writes two files that hold a copy's
      password: the saved state and the lines the run's output is searched with
      (`playwright.demo.config.ts`). git and docker are told to leave one folder out,
      `frontend/e2e-demo/.auth/`. A path given from the directory the run was started in lands
      there only when that directory is `frontend/`: started from anywhere else with `--config`,
      the two files would be written beside a folder nobody told git about. So both are whole
      paths, under this folder, and this is where that is held.

      Added after the run of 2026-10-05 on the stack. It asks nothing of a stack, and has run
      by itself, started from `frontend/` and from `frontend/e2e-demo`.
    */
    const auth = join(test.info().config.rootDir, '.auth');
    expect({ state: DEMO_STATE, values: SCAN_VALUES }).toEqual({
      state: join(auth, 'copy.json'),
      values: join(auth, 'scan-values.txt'),
    });
  });

  test("the scan leaves a node's markup out of its report when asked, and quotes it when not", async ({
    page: canaryPage,
  }) => {
    /*
      BEFORE ANYTHING IS CLAIMED. Further down the dashboard is scanned with a copy's
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

  test('the sign-in details count as off the page only once the kept password is off it', async ({
    page: canaryPage,
  }) => {
    /*
      BEFORE ANYTHING IS CLAIMED, as the test above. After every test a hook takes the sign-in
      details off the page, or leaves the page, before Playwright writes the page down
      (`leaveNoSignInDetails` in `e2e-demo/demoRun.ts`). It finds the details by the button that
      hides them. The day the panel calls that button something else, a helper that went by the
      button alone would find nothing to press, call that "off", and the first red test would
      leave the password on disk. So it also looks for the password the browser keeps, and this
      is where that is held: on pages of this test's own, in a browser context of its own, with
      words that are nobody's password.
    */
    await canaryPage.route(`**${DETAILS_PATH}/*`, (route) => {
      const name = new URL(route.request().url()).pathname.slice(DETAILS_PATH.length + 1);
      return route.fulfill({
        contentType: 'text/html',
        body: DETAILS_PAGES[name as keyof typeof DETAILS_PAGES],
      });
    });
    const showsTheWords = () =>
      canaryPage.evaluate((words) => document.body.innerText.includes(words), CANARY);

    const leaving = async (name: keyof typeof DETAILS_PAGES) => {
      await canaryPage.goto(`${DETAILS_PATH}/${name}`);
      // Read first: an "off" below is about a page that had the words on it.
      const shown = await showsTheWords();
      const said = await leaveNoSignInDetails(canaryPage).then(
        () => 'nothing',
        (error: unknown) => (error instanceof Error ? error.message : 'something else'),
      );
      return {
        shown,
        said,
        leftThePage: canaryPage.url() === 'about:blank',
        stillShown: await showsTheWords(),
      };
    };

    expect({
      asThePanelHasThem: await leaving('asThePanelHasThem'),
      underAnotherName: await leaving('underAnotherName'),
      stuckAndNothingKept: await leaving('stuckAndNothingKept'),
    }).toEqual({
      asThePanelHasThem: { shown: true, said: 'nothing', leftThePage: false, stillShown: false },
      underAnotherName: {
        shown: true,
        said: THE_PAGE_WAS_LEFT,
        leftThePage: true,
        stillShown: false,
      },
      stuckAndNothingKept: {
        shown: true,
        said: THE_PAGE_WAS_LEFT,
        leftThePage: true,
        stillShown: false,
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
    const { message, copy, password } = await keepForTheSearch(answer);
    const cookies = (await context.cookies()).map((cookie) => cookie.name);
    await note('the claim', {
      path: '/bff/auth/demo/claim',
      status: answer.status(),
      'cache-control': answer.headers()['cache-control'],
      message,
      // The copy's end as the server wrote it: its form is what the app's check of a claim reads.
      copyEnds: copy?.expiresAt,
      copyEndsInZ: typeof copy?.expiresAt === 'string' && copy.expiresAt.endsWith('Z'),
      contacts: Array.isArray(copy?.contacts) ? copy.contacts.length : null,
      passwordLength: password.length,
      passwordIsFourGroupsOfFour: /^[A-Za-z0-9]{4}(-[A-Za-z0-9]{4}){3}$/.test(password),
      cookies,
    });

    await expect(page).toHaveURL(/\/dashboard(?:[?#]|$)/, { timeout: 15_000 });
    const kept = await keptCopy(page);

    expect({
      status: answer.status(),
      cacheControl: answer.headers()['cache-control'],
      // The digits the page prints under its PIN boxes are the copy's own.
      pinIsTheDemoPin: copy?.pin === '123456',
      // What the browser keeps is what the claim answered: the copy's end, not the session's.
      keepsTheCopysEnd: kept?.expiresAt === copy?.expiresAt,
      keepsTheCopysPassword: kept?.password === password,
      theSearchKnowsTheKeptPassword: await theSearchKnows(kept),
      // MEASURED on the stack, 2026-10-05: the context's one cookie after the claim was
      // `__Host-AzureBank.Session` (the prefix is the BFF's outside Development:
      // backend/src/AzureBank.Bff/Program.cs). Chromium kept it, `Secure` as it is, although
      // the page came over http from localhost, and sent it: the dashboard below is behind it.
      sessionCookieHasTheHostPrefix: cookies.some((name) => name.startsWith('__Host-')),
    }).toEqual({
      status: 200,
      cacheControl: 'no-store',
      pinIsTheDemoPin: true,
      keepsTheCopysEnd: true,
      keepsTheCopysPassword: true,
      theSearchKnowsTheKeptPassword: true,
      sessionCookieHasTheHostPrefix: true,
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
    await expect(
      panel.getByRole('heading', { level: 2, name: 'Your private copy', exact: true }),
    ).toBeVisible();
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
    // Counted, so that the day a target has markup the failure prints a number and not the
    // markup, which is the line with the password if a finding is on it.
    const targets = open.violations.flatMap((v) => v.targets);
    expect({ targetsWithMarkup: targets.filter((target) => 'html' in target).length }).toEqual({
      targetsWithMarkup: 0,
    });
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
    // MEASURED on the stack, 2026-10-05: 201 and 201, as the API's source has them
    // (backend/src/AzureBank.Api/Controllers/TransferController.cs) and as the mock answers.
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
    const dialog = page.getByRole('alertdialog', { name: START_OVER_TITLE, exact: true });
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
    await keepForTheSearch(answer);
    await note('the second claim', {
      path: '/bff/auth/demo/claim',
      status: answer.status(),
      'cache-control': answer.headers()['cache-control'],
    });
    expect(answer.status()).toBe(200);

    await expect(dialog).toBeHidden();
    await expect(page.getByText(NEW_COPY, { exact: true }).first()).toBeVisible();
    // Focus is given back to the button the dialog was opened from.
    await expect.poll(() => focusOf(page)).toEqual({ on: START_OVER, inTheDialog: false });

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
      theSearchKnowsTheKeptPassword: await theSearchKnows(kept),
    }).toEqual({
      addressIsOfThePool: true,
      aNewAddress: true,
      showsTheKeptAddress: true,
      showsTheKeptPassword: true,
      theSearchKnowsTheKeptPassword: true,
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

  test("no screen of the demo's raised a Content-Security-Policy violation", async ({
    browser,
  }) => {
    /*
      LAST, after the state is saved, so that a violation fails the run and still leaves the
      state for whoever reads it. Every page the tests above drew was listened to from its first
      script: the sign-in page with no copy and with one, the dashboard with its panel, the
      sign-in details, the transfer, the dialog and the toast.

      A list with nothing in it is an answer only if two things hold, and both are held first.
      The listener hears: in a second context listened to as the shared one is, a page of this
      test's own, served under a policy its one styled paragraph breaks, is heard breaking it.
      And there was a policy to break: the page the stack serves carries one. Against vite's
      dev server, which sends none, this test fails on that line, as it should: nothing there
      was under a policy.

      Added after the run of 2026-10-05 on the stack, and NOT YET RUN ON ONE. Against the dev
      server with the mock and the demo's tag: the canary's violation was heard, the shared
      context's list was empty, and the test failed on the missing policy.
    */
    const canaryContext = await browser.newContext();
    try {
      const heard = await hearPolicyViolations(canaryContext);
      const canaryPage = await canaryContext.newPage();
      await canaryPage.route(`**${POLICY_CANARY_PATH}`, (route) =>
        route.fulfill({
          contentType: 'text/html',
          headers: { 'Content-Security-Policy': POLICY_CANARY_POLICY },
          body: POLICY_CANARY_PAGE,
        }),
      );
      await canaryPage.goto(POLICY_CANARY_PATH);
      await expect(canaryPage.getByRole('heading', { level: 1 })).toBeVisible();
      await expect
        .poll(() => heard)
        .toEqual([`style-src-attr blocked inline on ${POLICY_CANARY_PATH}`]);
    } finally {
      await canaryContext.close();
    }

    const served = await context.request.get('/login');
    expect({
      servedUnderAPolicy: Boolean(served.headers()['content-security-policy']),
      violations: policyViolations,
    }).toEqual({ servedUnderAPolicy: true, violations: [] });
  });
});
