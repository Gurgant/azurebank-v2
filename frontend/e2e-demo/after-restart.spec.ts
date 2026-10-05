import { expect, test } from '@playwright/test';
import { focusOf } from '../e2e/focusOf';
import {
  answerTo,
  keptCopy,
  leaveNoSignInDetails,
  note,
  shownSignInDetails,
  signOut,
} from './demoRun';

/**
 * The demo after the BFF and the API have restarted, from the state the first half saved signed
 * in (`playwright.demo.config.ts`; the restart is `e2e-demo/restart.setup.ts`).
 *
 * What a visitor meets who comes back to a deployment that was restarted in between: the
 * session is gone and nothing says it "expired"; the browser still keeps the copy, and
 * "Continue with my copy" signs in to it, the same copy; and "Forget this copy" removes what
 * the browser keeps, there and then.
 *
 * One test, because each part stands on the one before and a test of this project starts again
 * from the saved state: in that state the visitor is signed in to nothing after the restart.
 *
 * The copy's password is read here too, to be compared and never printed
 * (`e2e-demo/demoRun.ts`).
 *
 * As of 2026-10-05 this file has not run against the stack. On this tree's dev server with the
 * mock it ran as it stands, from the saved state, as far as the press on "Continue with my
 * copy": the mock keeps its copies in the page, so a page opened from a saved state has none to
 * sign in to, and the mock answered that press 401. Started instead from a claim made in the
 * page, it ran from the kept copy on to its end. The 401 before the press and the 200 after it
 * are READ, from the BFF's source, and not from a run against it.
 */

const CONTINUE = 'Continue with my copy';
const GET_A_NEW_COPY = 'Get a new copy';
const FORGET = 'Forget this copy';
const TRY_THE_DEMO = 'Try the demo';

// However the test ended, the sign-in details are off the page before Playwright looks at it.
test.afterEach(({ page }) => leaveNoSignInDetails(page));

test('after a restart the session is gone, the kept copy still signs in, and it can be forgotten', async ({
  page,
}) => {
  const buttons = (name: string) => page.getByRole('button', { name, exact: true });

  // The saved cookie is sent, and the restarted BFF does not know it.
  const asked = answerTo(page, 'GET', '/bff/auth/me');
  await page.goto('/dashboard');
  const me = await asked;
  await note('after the restart', { path: '/bff/auth/me', status: me.status() });
  expect(me.status()).toBe(401);

  // The visitor is sent to the sign-in page as one who was never signed in: a session that
  // ended while the page was closed is not "expired" to them.
  await expect.poll(() => new URL(page.url()).pathname).toBe('/login');
  await expect(buttons(CONTINUE)).toBeVisible();
  // Read before the press, from what the saved state put in the browser: the copy to come back to.
  const kept = await keptCopy(page);
  expect({
    expiredNotes: await page.getByText('Your session has expired').count(),
    getANewCopy: await buttons(GET_A_NEW_COPY).count(),
    tryTheDemo: await buttons(TRY_THE_DEMO).count(),
    keepsACopy: kept !== null && kept.email !== '',
  }).toEqual({ expiredNotes: 0, getANewCopy: 1, tryTheDemo: 0, keepsACopy: true });

  // The kept pair still signs in: a copy is the database's, and no restart ends it.
  const signedIn = answerTo(page, 'POST', '/bff/auth/login');
  await buttons(CONTINUE).click();
  const answer = await signedIn;
  await note('continue with my copy, after the restart', {
    path: '/bff/auth/login',
    status: answer.status(),
  });
  expect(answer.status()).toBe(200);
  await expect(page).toHaveURL(/\/dashboard(?:[?#]|$)/, { timeout: 15_000 });
  await expect(page.getByRole('heading', { level: 1 }).first()).toHaveText(/^€[\d,]+\.\d{2}$/);
  const shown = await shownSignInDetails(page);
  expect({
    theCopyIsTheOneKept: shown.email === kept?.email,
    itsPasswordIsTheOneKept: shown.password === kept?.password,
  }).toEqual({ theCopyIsTheOneKept: true, itsPasswordIsTheOneKept: true });

  // Signed out, the copy is still offered. Then it is forgotten, from the keyboard.
  await signOut(page);
  await buttons(FORGET).focus();
  expect(await focusOf(page)).toEqual({ on: FORGET, inTheDialog: false });
  await page.keyboard.press('Enter');

  // The whole of what the status says: a text given as words would be looked for as a part.
  await expect(
    page.getByRole('status').filter({ hasText: /^This browser no longer remembers the copy\.$/ }),
  ).toBeVisible();
  expect({
    tryTheDemo: await buttons(TRY_THE_DEMO).count(),
    continueWithMyCopy: await buttons(CONTINUE).count(),
    getANewCopy: await buttons(GET_A_NEW_COPY).count(),
    forgetThisCopy: await buttons(FORGET).count(),
    keepsNoCopy: (await keptCopy(page)) === null,
    // The link went from under the press; focus is on the button that took its place.
    focus: await focusOf(page),
  }).toEqual({
    tryTheDemo: 1,
    continueWithMyCopy: 0,
    getANewCopy: 0,
    forgetThisCopy: 0,
    keepsNoCopy: true,
    focus: { on: TRY_THE_DEMO, inTheDialog: false },
  });
});
