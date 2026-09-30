import { expect, test, type Page, type Route } from '@playwright/test';

/**
 * A slow or unavailable service, in the served build under its real CSP.
 *
 * The outages here are made in the browser, not in the stack: `page.route` parks a request (a
 * server that has not answered) or answers it with the wire's own 503, and `page.clock` moves the
 * page's time so a 65 s wait costs a second. What is proven is the SPA's reaction — the hint, the
 * stop, the words, the retry — to a copied wire. The wire itself is pinned by the backend's own
 * tests; the real outages (a stopped or paused database, a paused API) are run by hand against the
 * compose stack, because stopping a database mid-suite would fail every spec after this one.
 *
 * Order matters in each test: the clock is installed before the page loads, and time moves only
 * after the parked request and its spinner are there — a timer the page has not created yet
 * cannot be fast-forwarded.
 */

const SLOW = 'Taking longer than usual…';
const STILL_TRYING = 'Still trying…';
const UNAVAILABLE = 'The service is temporarily unavailable. Please try again later.';

/** The API's outage 503 for `path`, as ADR-0058 measured it. */
function apiOutageBody(path: string) {
  return JSON.stringify({
    type: 'https://httpstatuses.com/503',
    title: 'Service Unavailable',
    status: 503,
    detail: 'The service is temporarily unavailable. Try again shortly.',
    instance: path,
    errorCode: 'SERVICE_UNAVAILABLE',
    traceId: '0af7651916cd43dd8448eb211c80319c',
    retryAfterSeconds: 10,
  });
}

/** The BFF's own outage 503: its extension order, and never `applied`. */
function bffOutageBody(path: string) {
  return JSON.stringify({
    type: 'https://httpstatuses.com/503',
    title: 'Service Unavailable',
    status: 503,
    detail: 'The service is temporarily unavailable. Try again shortly.',
    instance: path,
    errorCode: 'SERVICE_UNAVAILABLE',
    retryAfterSeconds: 10,
    traceId: '4bf92f3577b34da6a3ce929d0e0e4736',
  });
}

/** A route handler that answers nothing, keeping each request so the test can release it. */
function park(parked: Route[]) {
  return (route: Route) => {
    parked.push(route);
  };
}

/**
 * Move the page's clock a second at a time until `done` holds. A timer the page creates after a
 * response lands (the retry's wait) only exists once that response is processed, so one big jump
 * could pass over it.
 */
async function stepUntil(page: Page, done: () => boolean | Promise<boolean>, maxSeconds: number) {
  for (let second = 0; second < maxSeconds && !(await done()); second++) {
    await page.clock.fastForward(1_000);
    await page.waitForTimeout(50);
  }
}

test.describe('a slow or unavailable service', () => {
  test('a slow read says so, can be stopped, and Retry waits again', async ({ page }) => {
    const parked: Route[] = [];
    await page.clock.install();
    await page.route('**/api/accounts*', park(parked));
    await page.goto('/accounts');
    await expect.poll(() => parked.length).toBe(1);
    await expect(page.getByLabel('Loading accounts')).toBeVisible();

    await page.clock.fastForward('00:05');
    await expect(page.getByRole('status').filter({ hasText: SLOW })).toHaveText(SLOW);
    await page.clock.fastForward('00:15');
    await expect(page.getByRole('status').filter({ hasText: STILL_TRYING })).toHaveText(
      STILL_TRYING,
    );

    await page.getByRole('button', { name: 'Stop waiting' }).click();
    const alert = page.getByRole('alert').filter({ hasText: 'Could not load your accounts.' });
    await expect(alert).toBeVisible();
    const retry = alert.getByRole('button', { name: 'Retry' });
    await expect(retry).toBeFocused();

    // Retry while the service is still silent: the wait, and its words, come back.
    await retry.click();
    await expect.poll(() => parked.length).toBe(2);
    await expect(page.getByLabel('Loading accounts')).toBeVisible();
    await page.clock.fastForward('00:05');
    await expect(page.getByRole('status').filter({ hasText: SLOW })).toHaveText(SLOW);

    // The service answers: the accounts arrive.
    await page.unroute('**/api/accounts*');
    await parked[1].continue().catch(() => {
      // Aborted by the page before the service answered; the next load goes through unrouted.
    });
    await expect(page.getByRole('button', { name: /^Account actions for / }).first()).toBeVisible();
  });

  test('a 503 reads as unavailable, with its support code, and is retried once after Retry-After', async ({
    page,
  }) => {
    let calls = 0;
    await page.clock.install();
    await page.route('**/api/accounts*', (route) => {
      calls += 1;
      return route.fulfill({
        status: 503,
        headers: { 'Retry-After': '10', 'Cache-Control': 'no-store' },
        contentType: 'application/json; charset=utf-8',
        body: apiOutageBody('/api/accounts'),
      });
    });
    await page.goto('/accounts');
    await expect.poll(() => calls).toBe(1);

    // The retry waits the ten seconds the answer asked for.
    await stepUntil(page, () => false, 9);
    expect(calls).toBe(1);

    const alert = page.getByRole('alert').filter({ hasText: UNAVAILABLE });
    await stepUntil(page, () => alert.isVisible(), 6);
    await expect(alert).toContainText('Support code: 0af7651916cd43dd8448eb211c80319c');
    await expect(page.getByText(/shortly/i)).toHaveCount(0);

    await page.clock.fastForward('01:00');
    expect(calls).toBe(2);
    expect(new URL(page.url()).pathname).toBe('/accounts');
  });

  test('a 503 without JSON is the same outage, retried once', async ({ page }) => {
    let calls = 0;
    await page.clock.install();
    await page.route('**/api/accounts*', (route) => {
      calls += 1;
      return route.fulfill({ status: 503, contentType: 'text/plain', body: 'Service Unavailable' });
    });
    await page.goto('/accounts');
    await expect.poll(() => calls).toBe(1);

    const alert = page.getByRole('alert').filter({ hasText: UNAVAILABLE });
    await stepUntil(page, () => alert.isVisible(), 5);
    await expect(alert).toBeVisible();
    await expect(page.getByText('Unparseable 503 response.')).toHaveCount(0);

    await page.clock.fastForward('01:00');
    expect(calls).toBe(2);
  });

  test('a read with no answer ends at 65 s as an outage, and is not retried', async ({ page }) => {
    const parked: Route[] = [];
    await page.clock.install();
    await page.route('**/api/accounts*', park(parked));
    await page.goto('/accounts');
    await expect.poll(() => parked.length).toBe(1);
    await expect(page.getByLabel('Loading accounts')).toBeVisible();

    await page.clock.fastForward('01:05');

    await expect(page.getByRole('alert').filter({ hasText: UNAVAILABLE })).toBeVisible();
    await page.clock.fastForward('01:00');
    expect(parked).toHaveLength(1);
  });

  test('a boot session check that meets an outage is not a sign-out', async ({ page }) => {
    let down = true;
    await page.clock.install();
    await page.route('**/bff/auth/me', (route) =>
      down
        ? route.fulfill({
            status: 503,
            headers: { 'Retry-After': '10', 'Cache-Control': 'no-store' },
            contentType: 'application/problem+json',
            body: bffOutageBody('/bff/auth/me'),
          })
        : route.continue(),
    );
    await page.goto('/dashboard');

    const heading = page.getByRole('heading', { level: 1, name: 'Temporarily unavailable' });
    await stepUntil(page, () => heading.isVisible(), 15);
    await expect(heading).toBeVisible();
    await expect(page.getByRole('main').getByRole('alert')).toContainText(UNAVAILABLE);
    const tryAgain = page.getByRole('button', { name: 'Try again' });
    await expect(tryAgain).toBeVisible();
    expect(new URL(page.url()).pathname).not.toBe('/login');

    down = false;
    await tryAgain.click();
    await expect(page).toHaveURL(/\/dashboard$/);
    await expect(page.getByRole('heading', { level: 1 }).first()).not.toHaveText(
      'Temporarily unavailable',
    );
  });
});
