import type { TransactionType } from './enums';
import { REQUEST_TIMEOUT_MS } from './problemBaseQuery';
import { movementsPageSchema } from './responseSchemas';

/**
 * The listing by idempotency key, read for a money send whose answer was lost (ADR-0009,
 * ADR-0022): what the send wrote, if anything, and from when an empty page is evidence.
 *
 * Pure, and kept apart from the hook that asks, so that every clause can be held by a test of its
 * own without a store or a server.
 */

/**
 * How old a claim on a key must be before it is stale: `Idempotency:ProcessingStaleAfter` in the
 * API's settings, two minutes. Past it, nothing under a claim made by then can still commit.
 * `timeoutChain.test.ts` reads the number from the file that sets it.
 */
export const PROCESSING_STALE_AFTER_MS = 120_000;

/**
 * From the start of the latest send under a key, how long until nothing under that key can still
 * commit: the SPA hangs up on every send by `REQUEST_TIMEOUT_MS`, a hang-up ends the API request's
 * ability to claim or commit, and a claim made by then is stale `PROCESSING_STALE_AFTER_MS` later.
 * 3 min 5 s. No client clock covers a frozen API, or a COMMIT that outlives its connection's cut
 * (ADR-0058).
 */
export const LIVE_BOUND_MS = REQUEST_TIMEOUT_MS + PROCESSING_STALE_AFTER_MS;

/** What is asked: the send's key, on one of the visitor's own accounts, for one type of row. */
export interface MovementLookupArg {
  readonly idempotencyKey: string;
  readonly accountId: string;
  readonly type: TransactionType;
  /** The move's other account, whose balance the found movement changed too. */
  readonly otherAccountId?: string;
}

/** A movement the listing found by the key. The key itself is not in it, on purpose. */
export interface FoundMovement {
  readonly id: string;
  readonly type: TransactionType;
  readonly transactionNumber: string;
  readonly amount: number;
  readonly createdAt: string;
}

export type MovementReading =
  | { readonly kind: 'found'; readonly movement: FoundMovement }
  | { readonly kind: 'none' }
  | { readonly kind: 'unknown' };

const NONE: MovementReading = { kind: 'none' };
const UNKNOWN: MovementReading = { kind: 'unknown' };

/**
 * What a page of the listing says about the send that `arg` names. Never throws.
 *
 * - `found`: the newest row whose key is the sent one (case aside: `crypto.randomUUID()` and the
 *   API write lower case, and the comparison does not depend on it), whose type is the one the
 *   send writes on that account, and whose status is `Completed`, the only status the API
 *   writes. Only the fields a view shows are kept: the key never reaches a stored result.
 * - `none`: an empty page that counts no rows at all. Also true of an app that ignores the key,
 *   whose unfiltered empty page means the account has no rows.
 * - `unknown`: anything else. A page that does not parse, rows of the key of another type or
 *   status, and rows that carry no key: an app that does not filter by key answers the
 *   account's latest page.
 *
 * A row's `createdAt` is never compared with the browser's clock.
 */
export function readMovement(raw: unknown, arg: MovementLookupArg): MovementReading {
  const parsed = movementsPageSchema.safeParse(raw);
  if (!parsed.success) return UNKNOWN;
  const { data, pagination } = parsed.data;
  const key = arg.idempotencyKey.toLowerCase();
  const row = data.find(
    (candidate) =>
      candidate.idempotencyKey?.toLowerCase() === key &&
      candidate.type === arg.type &&
      candidate.status === 'Completed',
  );
  if (row) {
    return {
      kind: 'found',
      movement: {
        id: row.id,
        type: row.type,
        transactionNumber: row.transactionNumber,
        amount: row.amount,
        createdAt: row.createdAt,
      },
    };
  }
  if (data.length === 0 && pagination.totalItems === 0) return NONE;
  return UNKNOWN;
}

/**
 * The instant, on `performance.now()`'s clock, from which nothing sent under the key can still
 * commit: `LIVE_BOUND_MS` after the latest send began. Minus infinity when every send carried an
 * authorisation and every one presented was refused: nothing under that key can commit any more,
 * and a copy of a send still on its way carries a refused authorisation too (ADR-0022, decision 6).
 */
export function liveUntil(sent: {
  readonly lastSentAt: number;
  readonly everyAuthorisationRefused: boolean;
}): number {
  return sent.everyAuthorisationRefused ? -Infinity : sent.lastSentAt + LIVE_BOUND_MS;
}
