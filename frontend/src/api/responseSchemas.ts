import { z, type ZodType } from 'zod';
import type { components } from './schema';
import {
  AccountNumberResponse,
  AccountResponse,
  BalanceResponse,
  DepositResponse,
  InternalTransferResponse,
  PaginatedResponseOfTransactionResponse,
  RecipientLookupResponse,
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
