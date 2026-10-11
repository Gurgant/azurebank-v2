import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import {
  asProblem,
  call,
  closeAccount,
  elevate,
  firstAccountId,
  idempotencyKey,
  login,
} from './client';
import { FIXTURES } from './target';

/**
 * What the stack answers to each thing the app can do on an account that was closed after the
 * app prepared the request: closed in another tab or session, while a page still showed it.
 *
 * The screens are built on one fact, held here against both targets: every one of these answers
 * is 404 ACCOUNT_NOT_FOUND, on the mint as on the send, for the source of a move as for its
 * destination, and the accounts list no longer has the account. The code does not say which of
 * a move's two accounts is meant; the list does.
 *
 * Measured through the BFF (API over http, a seeded LocalDB) on a spare account with a zero
 * balance, each authorisation minted before the close and presented after it. All of them:
 *   404 {"type":"https://httpstatuses.com/404","title":"Not Found","status":404,
 *        "detail":"Account with identifier '<the spare's id>' was not found.",
 *        "instance":"<the path>","errorCode":"ACCOUNT_NOT_FOUND","traceId":"<32 hex>"}
 * A send with no authorisation at all answers the same: the account is looked for first.
 *
 * The reveal of the full number is the one request behind session level 2. Below it the closed
 * account answers what a live one does, 403 `type: STEP_UP_REQUIRED` with
 * `X-Auth-Level-Current: 1`, before the account is looked for; once the session is raised it
 * answers the same 404 as the rest.
 *
 * No money moves: every request after the close is refused, and the mints before it move none.
 * The spare is closed again when the test ends, however it ends; on a spare already closed
 * that second close is answered 404 and changes nothing. A closure that is itself refused (the
 * PIN locked, the limiter, the session ended) leaves the spare on the fixture user: the file
 * then fails after the test, on the accounts list that still has it.
 */

interface Minted {
  data: { authorizationId: string };
}

const json = (body: unknown, headers: Record<string, string> = {}) => ({
  method: 'POST',
  headers,
  body: JSON.stringify(body),
});

/** The spare of the test below, once it exists: what the list is read for after the test. */
let spareLeftToCheck: string | undefined;

beforeAll(async () => {
  expect((await login()).status).toBe(200);
});

afterAll(async () => {
  if (!spareLeftToCheck) return;
  const list = await call('/api/accounts');
  expect(list.status, 'accounts read after the test, to see that the spare is closed').toBe(200);
  const ids = (list.body as { data: { id: string }[] }).data.map((account) => account.id);
  expect(ids, 'the spare is still listed: its closure was refused').not.toContain(spareLeftToCheck);
});

describe('contract: an account closed under the app answers ACCOUNT_NOT_FOUND to everything', () => {
  it('on each request a screen can make, and leaves the accounts list', async () => {
    const own = await firstAccountId();
    const created = await call('/api/accounts', json({ name: 'Contract Closed', type: 'Savings' }));
    expect(created.status).toBe(201);
    const spare = (created.body as { data: { id: string } }).data.id;
    spareLeftToCheck = spare;

    try {
      const mint = async (path: string, body: Record<string, unknown>) => {
        const minted = await call(path, json({ ...body, pin: FIXTURES.pin }));
        expect(minted.status).toBe(201);
        return (minted.body as Minted).data.authorizationId;
      };
      const transfer = {
        fromAccountId: spare,
        recipientAzureTag: FIXTURES.recipientAzureTag,
        amount: 1,
      };
      const moveOut = { fromAccountId: spare, toAccountId: own, amount: 1 };
      const moveIn = { fromAccountId: own, toAccountId: spare, amount: 1 };
      const withdrawal = { accountId: spare, amount: 1 };
      const forTransfer = await mint('/api/transfers/authorizations', transfer);
      const forMoveOut = await mint('/api/transfers/internal/authorizations', moveOut);
      const forMoveIn = await mint('/api/transfers/internal/authorizations', moveIn);
      const forWithdrawal = await mint('/api/transactions/withdraw/authorizations', withdrawal);

      // The close, as the delete dialog does it: mint, then DELETE with the minted id.
      const forClose = await mint(`/api/accounts/${spare}/deletion-authorizations`, {});
      const closed = await call(`/api/accounts/${spare}`, {
        method: 'DELETE',
        headers: { 'Step-Up-Authorization': forClose },
      });
      expect(closed.status).toBe(200);

      const send = (authorization: string) => ({
        'Idempotency-Key': idempotencyKey(),
        'Step-Up-Authorization': authorization,
      });
      const pin = { pin: FIXTURES.pin };
      const requests: [string, Promise<{ status: number; body: unknown }>][] = [
        ['transfer mint', call('/api/transfers/authorizations', json({ ...transfer, ...pin }))],
        ['transfer send', call('/api/transfers', json(transfer, send(forTransfer)))],
        [
          'move mint, closed source',
          call('/api/transfers/internal/authorizations', json({ ...moveOut, ...pin })),
        ],
        [
          'move send, closed source',
          call('/api/transfers/internal', json(moveOut, send(forMoveOut))),
        ],
        [
          'move mint, closed destination',
          call('/api/transfers/internal/authorizations', json({ ...moveIn, ...pin })),
        ],
        [
          'move send, closed destination',
          call('/api/transfers/internal', json(moveIn, send(forMoveIn))),
        ],
        [
          'deposit',
          call(
            '/api/transactions/deposit',
            json({ accountId: spare, amount: 1 }, { 'Idempotency-Key': idempotencyKey() }),
          ),
        ],
        [
          'withdrawal mint',
          call('/api/transactions/withdraw/authorizations', json({ ...withdrawal, ...pin })),
        ],
        [
          'withdrawal send',
          call('/api/transactions/withdraw', json(withdrawal, send(forWithdrawal))),
        ],
        [
          'rename',
          call(`/api/accounts/${spare}`, {
            ...json({ name: 'Contract Renamed' }),
            method: 'PATCH',
          }),
        ],
        ['set-primary', call(`/api/accounts/${spare}/set-primary`, { method: 'PATCH' })],
      ];

      for (const [label, request] of requests) {
        const { status, body } = await request;
        const problem = asProblem(body);
        expect([label, status, problem.errorCode, problem.title, problem.detail]).toEqual([
          label,
          404,
          'ACCOUNT_NOT_FOUND',
          'Not Found',
          `Account with identifier '${spare}' was not found.`,
        ]);
      }

      // Raised here and not before, so that every request above is made at level 1.
      const revealPath = `/api/accounts/${spare}/full-number`;
      const belowLevelTwo = await call(revealPath);
      expect([
        belowLevelTwo.status,
        (belowLevelTwo.body as { type?: string }).type,
        belowLevelTwo.headers.get('X-Auth-Level-Current'),
      ]).toEqual([403, 'STEP_UP_REQUIRED', '1']);
      expect((await elevate()).status).toBe(200);
      const reveal = await call(revealPath);
      const revealProblem = asProblem(reveal.body);
      expect([
        reveal.status,
        revealProblem.errorCode,
        revealProblem.title,
        revealProblem.detail,
      ]).toEqual([
        404,
        'ACCOUNT_NOT_FOUND',
        'Not Found',
        `Account with identifier '${spare}' was not found.`,
      ]);

      const list = await call('/api/accounts');
      expect(list.status).toBe(200);
      const ids = (list.body as { data: { id: string }[] }).data.map((account) => account.id);
      expect(ids).toContain(own);
      expect(ids).not.toContain(spare);
    } finally {
      await closeAccount(spare);
    }
  });
});
