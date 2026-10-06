import { z, type ZodType } from 'zod';
import type { DemoCopyInfo } from './bffTypes';
import type { components } from './schema';
import {
  AccountNumberResponse,
  AccountResponse,
  BalanceResponse,
  DepositResponse,
  InternalTransferResponse,
  PaginatedResponseOfTransactionResponse,
  RecipientLookupResponse,
  StepUpAuthorizationResponse,
  TransactionResponse,
  TransactionSummaryResponse,
  TransferResponse,
  UpdateAzureTagResponse,
  WithdrawResponse,
} from './generated/apiSchemas';

type Schemas = components['schemas'];

/**
 * The curated seam between the SPEC-GENERATED Zod schemas (apiSchemas.ts, `npm run
 * generate:zod` — typed-openapi, Zod v4 output) and the API layer (A/B/C decision doc):
 *
 * - **B is the source**: nothing here is hand-written; regenerating from the spec keeps the
 *   validators drift-proof by construction.
 * - **A is where to enforce**: the MONEY surfaces (the four mutation receipts, the accounts
 *   list, the monthly summary) export STRICT schemas — validated fail-closed in every
 *   environment, because a silent drift there means wrong money on screen.
 * - **C is when to validate the rest**: every other response validates only outside
 *   production (`devOnly`) — catching MSW mock-drift in vitest and local integration drift
 *   with zero production crash-surface.
 *
 * The `AssertExtends` checks make the generated types and the openapi-typescript types
 * (schema.d.ts) verify EACH OTHER at compile time — if either regeneration drifts, tsc
 * fails before any runtime does. That proof is what makes the narrowing casts safe.
 */

type AssertExtends<A extends B, B> = A;

/**
 * Exported so tsc's noUnusedLocals sees a use — the tuple's only job is to force every
 * AssertExtends pair to typecheck (each entry proves one direction of one schema pair).
 */
export type _GeneratedSchemasMatchSpec = [
  AssertExtends<z.infer<typeof DepositResponse>, Schemas['DepositResponse']>,
  AssertExtends<Schemas['DepositResponse'], z.infer<typeof DepositResponse>>,
  AssertExtends<z.infer<typeof WithdrawResponse>, Schemas['WithdrawResponse']>,
  AssertExtends<Schemas['WithdrawResponse'], z.infer<typeof WithdrawResponse>>,
  AssertExtends<z.infer<typeof TransferResponse>, Schemas['TransferResponse']>,
  AssertExtends<Schemas['TransferResponse'], z.infer<typeof TransferResponse>>,
  AssertExtends<z.infer<typeof InternalTransferResponse>, Schemas['InternalTransferResponse']>,
  AssertExtends<Schemas['InternalTransferResponse'], z.infer<typeof InternalTransferResponse>>,
  AssertExtends<z.infer<typeof AccountResponse>, Schemas['AccountResponse']>,
  AssertExtends<Schemas['AccountResponse'], z.infer<typeof AccountResponse>>,
  AssertExtends<z.infer<typeof TransactionSummaryResponse>, Schemas['TransactionSummaryResponse']>,
  AssertExtends<Schemas['TransactionSummaryResponse'], z.infer<typeof TransactionSummaryResponse>>,
  AssertExtends<
    z.infer<typeof StepUpAuthorizationResponse>,
    Schemas['StepUpAuthorizationResponse']
  >,
  AssertExtends<
    Schemas['StepUpAuthorizationResponse'],
    z.infer<typeof StepUpAuthorizationResponse>
  >,
  /*
    One pair that is not about a generated schema. `DemoCopyInfo` on the left is HAND-WRITTEN
    (bffSchemas.ts): a demo claim's answer is the BFF's, and the BFF's answers are not in the spec.
    But the copy inside that answer is the API's `DemoCopyInfo`, which the BFF passes on as it got
    it (`Copy = claim.Copy`, backend/src/AzureBank.Bff/Controllers/BffAuthController.cs), and that
    one is in the spec. So the two are held to each other, both ways, and both hold: a member the
    server adds, drops, renames or retypes stops the build, where otherwise nothing would fail
    until a visitor pressed "Try the demo" and the answer was refused.

    What the pair cannot see is everything the hand-written schema asks beyond a type: that the
    address is one, that no text is empty, that the copy's end names a zone. To the compiler each
    of those is a `string`. They are checked when an answer arrives (bffSchemas.test.ts).
  */
  AssertExtends<DemoCopyInfo, Schemas['DemoCopyInfo']>,
  AssertExtends<Schemas['DemoCopyInfo'], DemoCopyInfo>,
];

// ===== A — STRICT money schemas (fail-closed everywhere) =====

export const depositResponseSchema = DepositResponse as ZodType<Schemas['DepositResponse']>;
export const withdrawResponseSchema = WithdrawResponse as ZodType<Schemas['WithdrawResponse']>;
export const transferResponseSchema = TransferResponse as ZodType<Schemas['TransferResponse']>;
export const internalTransferResponseSchema = InternalTransferResponse as ZodType<
  Schemas['InternalTransferResponse']
>;
export const accountsListSchema = z.array(AccountResponse) as ZodType<Schemas['AccountResponse'][]>;
export const transactionSummarySchema = TransactionSummaryResponse as ZodType<
  Schemas['TransactionSummaryResponse']
>;

/*
  STRICT, not soft, even though it moves no money itself. A minted authorisation is the permission a
  transfer is about to be spent against (ADR-0042): if `authorizationId` or `expiresAt` ever drifts,
  the client sends a header the server cannot match and the user is refused with no way to tell why.
  Fail closed in production, like the receipts.
*/
export const stepUpAuthorizationResponseSchema = StepUpAuthorizationResponse as ZodType<
  Schemas['StepUpAuthorizationResponse']
>;

// ===== C — soft schemas (dev + test only; undefined in production = skip) =====

/** Returns the schema outside production, undefined in production (unwrap then skips). */
export function devOnly<T>(schema: ZodType<T>): ZodType<T> | undefined {
  return import.meta.env.PROD ? undefined : schema;
}

export const accountResponseSchema = AccountResponse as ZodType<Schemas['AccountResponse']>;
export const balanceResponseSchema = BalanceResponse as ZodType<Schemas['BalanceResponse']>;
export const accountNumberResponseSchema = AccountNumberResponse as ZodType<
  Schemas['AccountNumberResponse']
>;
export const transactionResponseSchema = TransactionResponse as ZodType<
  Schemas['TransactionResponse']
>;
export const recipientLookupResponseSchema = RecipientLookupResponse as ZodType<
  Schemas['RecipientLookupResponse']
>;
export const updateAzureTagResponseSchema = UpdateAzureTagResponse as ZodType<
  Schemas['UpdateAzureTagResponse']
>;
export const paginatedTransactionsSchema = PaginatedResponseOfTransactionResponse as ZodType<
  Schemas['PaginatedResponseOfTransactionResponse']
>;
