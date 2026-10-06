import { expect, test, type BrowserContext, type Page } from '@playwright/test';

/**
 * What the files of the demo's run share: the two names they look the copy up by, the one way a
 * step writes down what it observed, and what is done to the page in more than one of them.
 *
 * THE PASSWORD NEVER REACHES AN ASSERTION, A LOG OR AN ATTACHMENT. A claimed copy's password is
 * in the claim's answer, in the browser's storage and, when asked for, on the dashboard, and
 * these specs read all three. What they compare is whether two readings are equal, a length, or
 * whether a shape matches: a boolean or a number, never the value. A failed assertion prints
 * what it received, and what it prints stays in the terminal and in the run's output folder.
 */

/**
 * The key the browser keeps a claimed copy under, typed out
 * (`src/features/demo/demoCopyStorage.ts`).
 */
export const KEPT_COPY_KEY = 'azurebank.demoCopy';

/**
 * An owner's address as the pool draws them: `demo-`, sixteen of a-z and 0-9, and the pool's
 * domain (`backend/tools/AzureBank.Seeder/Pool/DemoCredentials.cs`).
 */
export const POOL_ADDRESS = /^demo-[a-z0-9]{16}@azurebank\.example$/;

/**
 * The panel the dashboard shows about the copy the visitor is signed in to. By its whole name:
 * Playwright takes a name for a part of one unless told otherwise.
 */
export const panelOf = (page: Page) =>
  page.getByRole('region', { name: 'Your private copy', exact: true });

/**
 * Listens for Content-Security-Policy violations on a page, or on every page of a context, from
 * before the page's first script runs, and hands back the list they are added to.
 *
 * The BFF serves the built page under a policy (ADR-0054), and a refused style or script does not
 * show in a spec that asks for controls by role and name: the page works and is drawn wrong, or
 * not at all where the policy bit. The default run listens on its own walk
 * (`../e2e/csp.spec.ts`), which is of a stack with the demo off and never draws the demo's
 * screens. This is the same listener for the demo's run, which draws them.
 *
 * A violation is written down as the directive, what was blocked and the page's path: none of
 * the three is a copy's password. Call it before the first page is opened.
 */
export async function hearPolicyViolations(where: BrowserContext | Page): Promise<string[]> {
  const violations: string[] = [];
  await where.exposeFunction('__cspViolation', (violation: string) => {
    violations.push(violation);
  });
  await where.addInitScript(() => {
    document.addEventListener('securitypolicyviolation', (event) => {
      const report = (window as unknown as { __cspViolation: (v: string) => void }).__cspViolation;
      report(`${event.violatedDirective} blocked ${event.blockedURI} on ${location.pathname}`);
    });
  });
  return violations;
}

/**
 * Writes down what a step observed: one line, attached to the test and printed.
 *
 * Both, because this run keeps no trace. Printed, the line is among the `list` reporter's for a
 * green run and a red one; attached as text, it is printed again under a red test's failure.
 * `facts` are statuses, paths, headers, counts and booleans. Never a claim's answer, and never a
 * password or anything worked out from one but its length and whether a shape matches.
 */
export async function note(what: string, facts: Record<string, unknown>) {
  const line = `${what}: ${JSON.stringify(facts)}`;
  await test.info().attach(what, { body: line, contentType: 'text/plain' });
  console.log(`demo run | ${line}`);
}

/**
 * The answer to the next `method` request for `path`. Call it BEFORE the press that sends the
 * request, and await it after: a wait armed afterwards can miss an answer that was quick.
 */
export function answerTo(page: Page, method: 'GET' | 'POST', path: string) {
  return page.waitForResponse(
    (response) =>
      response.request().method() === method && new URL(response.url()).pathname === path,
  );
}

/** What the browser keeps of the copy, or `null`. The value is for comparing, and nothing else. */
export function keptCopy(page: Page) {
  return page.evaluate((key) => {
    const raw = localStorage.getItem(key);
    if (raw === null) return null;
    const kept = JSON.parse(raw) as { email?: unknown; password?: unknown; expiresAt?: unknown };
    return {
      email: typeof kept.email === 'string' ? kept.email : '',
      password: typeof kept.password === 'string' ? kept.password : '',
      expiresAt: typeof kept.expiresAt === 'string' ? kept.expiresAt : '',
    };
  }, KEPT_COPY_KEY);
}

/**
 * Takes the sign-in details off the page if they are on it, and says whether they are gone.
 *
 * In the page and through no locator: this is what runs when something has already gone wrong,
 * and a locator that failed here would have Playwright write the page down, details and all.
 * The page is given a moment to draw itself again after the press before it is asked.
 *
 * GONE IS TWO THINGS, and the first alone is not enough. No button says the details are open;
 * and the password the browser keeps is not among the words the page shows. The button is found
 * by its name and its two attributes, so the day the panel names it otherwise this finds
 * nothing to press, and "no such button" would read as "gone" with the password still on the
 * page: wrong exactly when the product has changed, which is when a run is red. The password is
 * looked for where the browser keeps the copy; a page that keeps none is judged by the button
 * alone. One test of the run holds both halves, on pages of its own (`e2e-demo/demo.spec.ts`).
 */
function hideSignInDetails(page: Page) {
  return page.evaluate(async (key) => {
    const open = () =>
      Array.from(document.querySelectorAll('button[aria-expanded="true"][aria-controls]')).filter(
        (button) => button.textContent === 'Hide sign-in details',
      );
    let password = '';
    try {
      const raw = localStorage.getItem(key);
      const kept = raw === null ? null : (JSON.parse(raw) as { password?: unknown } | null);
      password = typeof kept?.password === 'string' ? kept.password : '';
    } catch {
      // No copy to look for. Reading the storage throws on a page that has none of its own
      // (about:blank: where the shared page is until its first visit, and where a page that
      // was left ends), and a key that is not JSON is no copy.
    }
    const gone = () =>
      open().length === 0 && (password === '' || !document.body.innerText.includes(password));
    for (const button of open()) (button as HTMLButtonElement).click();
    for (let asked = 0; asked < 40 && !gone(); asked += 1) {
      await new Promise((resolve) => setTimeout(resolve, 25));
    }
    return gone();
  }, KEPT_COPY_KEY);
}

/**
 * For an `afterEach`: the sign-in details are off the page when a test ends, however it ended.
 *
 * When a test ends red Playwright writes the page down as text, into `error-context.md` in the
 * test's output folder, whatever `trace` and `screenshot` say, and it does so after the test's
 * `afterEach` hooks have run. With the details open that text holds the copy's password. If
 * they cannot be taken off, the page itself is left, and the test is red for that.
 */
export async function leaveNoSignInDetails(page: Page) {
  if (await hideSignInDetails(page)) return;
  await page.goto('about:blank');
  throw new Error('The sign-in details could not be taken off the page, so the page was left.');
}

/**
 * Puts the sign-in details on the page, lets `look` look, and takes them off again whatever
 * `look` did.
 *
 * WHILE THEY ARE ON THE PAGE NO LOCATOR MAY BE ASSERTED ON. When a locator's expectation fails,
 * Playwright writes down, as text, the whole page if it found no element and the element if it
 * found one; and when a test ends red it writes the whole page down again. All of it goes into
 * `error-context.md` in the run's output folder, and with the details open it holds the copy's
 * password. So `look` reads the page in the page (`evaluate`) or scans it, and nothing else.
 * The scan asserts on two locators of its own, and both are counts (`e2e/axeScan.ts`): a count
 * that fails is written down as a number.
 *
 * They are taken off before they are put on, too, whatever an earlier step left: the
 * expectation on the button below is a locator's.
 */
export async function whileSignInDetailsAreShown<T>(page: Page, look: () => Promise<T>) {
  try {
    expect(await hideSignInDetails(page), 'the sign-in details start off the page').toBe(true);
    const show = panelOf(page).getByRole('button', { name: 'Show sign-in details', exact: true });
    await expect(show).toHaveAttribute('aria-expanded', 'false');
    await show.click();
    return await look();
  } finally {
    expect(await hideSignInDetails(page), 'the sign-in details are off the page again').toBe(true);
  }
}

/**
 * The address and the password the dashboard shows for the copy, read off the page: the line
 * is found the way a screen reader finds it, through the button's `aria-controls`.
 */
export async function shownSignInDetails(page: Page) {
  const line = await whileSignInDetailsAreShown(page, () =>
    page.evaluate(() => {
      const hide = Array.from(document.querySelectorAll('button[aria-expanded="true"]')).find(
        (button) => button.textContent === 'Hide sign-in details',
      );
      const controls = hide?.getAttribute('aria-controls');
      return controls ? (document.getElementById(controls)?.textContent ?? null) : null;
    }),
  );
  // The line as the panel writes it. Its words are not quoted when it reads otherwise.
  const read = /^Email: (\S+) · Password: (\S+)$/.exec(line ?? '');
  if (read === null) {
    throw new Error(
      line === null
        ? 'The button that shows the sign-in details controls no element of the page.'
        : `The sign-in details do not read "Email: … · Password: …" (${line.length} characters).`,
    );
  }
  return { email: read[1], password: read[2] };
}

/** Signs out from the shell and waits for the sign-in page. The request's status is noted. */
export async function signOut(page: Page) {
  const answered = answerTo(page, 'POST', '/bff/auth/logout');
  await page.getByRole('button', { name: 'Sign out', exact: true }).click();
  const answer = await answered;
  await note('sign out', { path: '/bff/auth/logout', status: answer.status() });
  await expect.poll(() => new URL(page.url()).pathname).toBe('/login');
  await expect(page.getByLabel('Email address', { exact: true })).toBeVisible();
}
