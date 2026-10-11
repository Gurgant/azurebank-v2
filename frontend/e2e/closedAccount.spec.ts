import { expect, test, type APIRequestContext } from '@playwright/test';
import { USER } from './fixtures';

/**
 * An account closed in another session while the transfer page has it selected, end to end:
 * real browser, real React, real BFF, real API, real SQL Server.
 *
 * It cannot be staged against the mock: the account has to leave the SERVER while the page still
 * shows it. The page is taken to its PIN step with a spare account as the source; a second
 * session, signed in apart from the browser's, empties that account and closes it; the sixth
 * digit then asks for the authorisation and the API answers 404 ACCOUNT_NOT_FOUND. The page says
 * the account is closed, reads the accounts again, shows the picker without it and chooses
 * nothing in its place.
 *
 * Nothing is sent: the mint is refused, so no transfer leaves, which the spec observes on the
 * page's own requests. The spare is made, funded with €1.00, emptied and closed within the
 * spec, and the seeded account is not touched.
 *
 * The cleanup is a hook: it runs after the test wherever the test stops, a timeout included,
 * and what it fails on is reported beside the test's own failure. It reads the accounts and
 * does nothing if the spare is not among them. Otherwise it withdraws the balance it read, when
 * that is above zero, and closes the spare: so a spare that was never funded, one still holding
 * its €1.00 and one emptied but not closed are all closed. The spare stays on the seeded user
 * when a step of the cleanup is refused (the accounts read, a mint, the withdrawal or the
 * closure) or the hook itself runs out of time; each refusal fails the spec with its status.
 * The other session is signed out whether or not its close went through. The spare's name
 * carries a per-run suffix, for the reason deleteAccount.spec.ts gives.
 *
 * The PIN typed is the right one, and the spec mints in-band only: it never verify-pins, so it
 * does not elevate the session that stepUp.spec.ts needs at level 1.
 */

const RUN = Date.now().toString(36);
const SENTENCE = 'This account is closed. Choose another account.';

async function authorizationOf(response: Awaited<ReturnType<APIRequestContext['post']>>) {
  return (await response.json()).data.authorizationId as string;
}

/**
 * Empties the spare and closes it, each as the app does it: a mint, then the request. The
 * balance is read first: a spare that is not in the list is already closed, and one that holds
 * nothing is closed with no withdrawal, which a zero balance would refuse.
 */
async function emptyAndClose(session: APIRequestContext, id: string) {
  const listed = await session.get('/api/accounts');
  expect(listed.status(), `accounts read before closing the spare ${id}`).toBe(200);
  const accounts = (await listed.json()).data as { id: string; balance: number }[];
  const spare = accounts.find((account) => account.id === id);
  if (!spare) return;

  if (spare.balance > 0) {
    const withdrawalMint = await session.post('/api/transactions/withdraw/authorizations', {
      data: { accountId: id, amount: spare.balance, pin: USER.pin },
    });
    expect(withdrawalMint.status(), `withdrawal mint for the spare ${id}`).toBe(201);
    const drained = await session.post('/api/transactions/withdraw', {
      headers: {
        'Idempotency-Key': crypto.randomUUID(),
        'Step-Up-Authorization': await authorizationOf(withdrawalMint),
      },
      data: { accountId: id, amount: spare.balance, description: 'e2e: empty the spare' },
    });
    expect(drained.status(), `withdrawal that empties the spare ${id}`).toBe(201);
  }

  const closureMint = await session.post(`/api/accounts/${id}/deletion-authorizations`, {
    data: { pin: USER.pin },
  });
  expect(closureMint.status(), `closure mint for the spare ${id}`).toBe(201);
  const closed = await session.delete(`/api/accounts/${id}`, {
    headers: { 'Step-Up-Authorization': await authorizationOf(closureMint) },
  });
  expect(closed.status(), `closure of the spare ${id}, or it stays on the seeded user`).toBe(200);
}

/** The spare of the test that is running, from the moment it exists: what the hook closes. */
let spareToClose: string | undefined;

test.afterEach(async ({ page }) => {
  const id = spareToClose;
  spareToClose = undefined;
  // Nothing to do once the test closed it; a spare that is still there is emptied, if it holds
  // anything, and closed.
  if (id) await emptyAndClose(page.request, id);
});

test('a source account closed in another session: the page says so and chooses nothing in its place', async ({
  page,
  playwright,
  baseURL,
}) => {
  const name = `E2E Closing ${RUN}`;
  const created = await page.request.post('/api/accounts', { data: { name, type: 'Savings' } });
  expect(created.status()).toBe(201);
  const id = (await created.json()).data.id as string;
  spareToClose = id;

  // Every request of the page that matters here, so that "nothing was sent" is observed.
  const mintStatuses: number[] = [];
  const sends: string[] = [];
  let listReads = 0;
  page.on('response', (response) => {
    const { pathname } = new URL(response.url());
    const method = response.request().method();
    if (method === 'POST' && pathname === '/api/transfers/authorizations') {
      mintStatuses.push(response.status());
    } else if (method === 'POST' && pathname === '/api/transfers') {
      sends.push(pathname);
    } else if (method === 'GET' && pathname === '/api/accounts') {
      listReads += 1;
    }
  });

  const funded = await page.request.post('/api/transactions/deposit', {
    headers: { 'Idempotency-Key': crypto.randomUUID() },
    data: { accountId: id, amount: 1, description: 'e2e: fund the spare' },
  });
  expect(funded.status()).toBe(201);

  await page.goto('/transfer');
  const spare = page.getByRole('button', { name: `From ${name}` });
  await spare.click();
  await expect(spare).toHaveAttribute('aria-pressed', 'true');
  await page.getByRole('textbox', { name: 'Recipient handle' }).fill('janesmith');
  await page.getByRole('button', { name: 'Verify' }).click();
  await page.getByRole('textbox', { name: 'Transfer amount' }).fill('1');
  await page.getByRole('button', { name: 'Review Transfer' }).click();
  // The check on Continue reads the accounts: the spare is still there, with its €1.00.
  await page.getByRole('button', { name: 'Continue' }).click();
  await expect(page.getByRole('textbox', { name: 'Digit 1 of 6' })).toBeVisible();

  // THE CLOSE, from a session of its own: a second sign-in, with cookies the browser never
  // sees. The empty storage state is what makes it a second one: a request context made
  // inside a test otherwise starts with the project's saved cookies, the browser's own
  // session, and its sign-out below would then end the session of the page.
  const elsewhere = await playwright.request.newContext({
    baseURL,
    storageState: { cookies: [], origins: [] },
  });
  try {
    const signedIn = await elsewhere.post('/bff/auth/login', {
      data: { email: USER.email, password: USER.password },
    });
    expect(signedIn.status()).toBe(200);
    await emptyAndClose(elsewhere, id);
  } finally {
    // Signed out whatever happened above: a session left alive is a trace on the seeded user.
    await elsewhere.post('/bff/auth/logout').catch(() => undefined);
    await elsewhere.dispose();
  }

  // The page has been told nothing: the spare is still its source. The sixth digit is the send.
  const readsBefore = listReads;
  for (const [index, digit] of [...USER.pin].entries()) {
    await page.getByRole('textbox', { name: `Digit ${index + 1} of 6` }).fill(digit);
  }

  await expect(page.getByRole('alert').filter({ hasText: SENTENCE })).toBeVisible({
    timeout: 15_000,
  });
  // The picker is the list read again: the closed account is not in it.
  await expect(spare).toHaveCount(0, { timeout: 15_000 });
  expect(listReads).toBeGreaterThan(readsBefore);
  // No silent switch: what is left in the account picker is offered, not chosen, and nothing
  // can go on.
  await expect(page.getByRole('button', { name: /^From /, pressed: false }).first()).toBeVisible();
  await expect(page.getByRole('button', { name: /^From /, pressed: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Review Transfer' })).toBeDisabled();
  await expect(page.getByText('Choose an account to continue.')).toBeVisible();
  // Nothing of the server's sentence, and no identifier.
  await expect(page.getByText(/Account with identifier/)).toHaveCount(0);
  await expect(page.getByText(id)).toHaveCount(0);
  // The API refused the mint, and no transfer left the page.
  expect(mintStatuses).toEqual([404]);
  expect(sends).toEqual([]);
});
