import { createApi } from '@reduxjs/toolkit/query/react';
import type { FetchBaseQueryMeta } from '@reduxjs/toolkit/query';
import type {
  BffLoginResponse,
  BffMeResponse,
  BffPinVerificationResponse,
  BffSessionStatusResponse,
} from '../../api/bffTypes';
import { unwrap } from '../../api/envelope';
import { problemBaseQuery } from '../../api/problemBaseQuery';
import type { components } from '../../api/schema';

type Schemas = components['schemas'];

export type LoginRequest = Schemas['LoginRequest'];
export type RegisterRequest = Schemas['RegisterRequest'];
export type VerifyPinRequest = Schemas['VerifyPinRequest'];
export type SetPinRequest = Schemas['SetPinRequest'];
export type AccountResponse = Schemas['AccountResponse'];
export type BalanceResponse = Schemas['BalanceResponse'];
export type CreateAccountRequest = Schemas['CreateAccountRequest'];
export type UpdateAccountRequest = Schemas['UpdateAccountRequest'];
export type TransactionResponse = Schemas['TransactionResponse'];
export type PaginatedTransactions = Schemas['PaginatedResponseOfTransactionResponse'];
export type DepositRequest = Schemas['DepositRequest'];
export type DepositResponse = Schemas['DepositResponse'];
export type WithdrawRequest = Schemas['WithdrawRequest'];
export type WithdrawResponse = Schemas['WithdrawResponse'];
export type TransferRequest = Schemas['TransferRequest'];
export type TransferResponse = Schemas['TransferResponse'];
export type InternalTransferRequest = Schemas['InternalTransferRequest'];
export type InternalTransferResponse = Schemas['InternalTransferResponse'];

/** Argument shape of every idempotent money mutation — the key comes from useIdempotentMutation. */
export interface IdempotentArg<TBody> {
  idempotencyKey: string;
  body: TBody;
}

/**
 * Money-mutation results carry replay detection read from the `Idempotency-Replayed`
 * response header in the SUCCESS path (D4) — a replayed 2xx means "this intent was
 * already processed", surfaced as a polite inline note, never an error.
 */
export type WithReplay<T> = T & { replayed: boolean };

function withReplay<T>(data: T, meta: FetchBaseQueryMeta | undefined): WithReplay<T> {
  return { ...data, replayed: meta?.response?.headers.get('Idempotency-Replayed') === 'true' };
}

export interface TransactionsQuery {
  accountId?: string;
  fromDate?: string;
  toDate?: string;
  page?: number;
  pageSize?: number;
}

/**
 * The typed data layer over the OpenAPI contract. Response/request shapes come from the
 * generated schema (types-only, CI drift gate); success envelopes unwrap in
 * `transformResponse`; errors normalize to ApiProblem in problemBaseQuery.
 *
 * Cache tag ledger (D7) — every provides/invalidates below implements it:
 *   provides:    accounts list -> [LIST + one per row]; account/balance -> {Account,id}
 *                (balance only for CURRENT balance — historical snapshots are immutable,
 *                tag-less); transactions list -> [{Transaction,LIST}]; detail -> {Transaction,id}
 *   invalidates: deposit/withdraw -> [{Account,accountId},{Transaction,LIST}];
 *                transfer -> from only (recipient is another user); internal -> from+to;
 *                create -> LIST; rename -> id; delete -> [LIST,id];
 *                setPrimary -> blanket 'Account' (two accounts flip isPrimary).
 * Invalidation happens only on SUCCESS (`error ? [] : ...`): failed mutations changed
 * nothing server-side, and RESULT_UNKNOWN recovery invalidates explicitly in its flow.
 */
export const apiSlice = createApi({
  reducerPath: 'api',
  baseQuery: problemBaseQuery,
  tagTypes: ['Account', 'Transaction', 'Session'],
  endpoints: (builder) => ({
    // ========== ACCOUNTS ==========

    getAccounts: builder.query<AccountResponse[], void>({
      query: () => '/api/accounts',
      transformResponse: (response: Schemas['ApiResponseOfListOfAccountResponse']) =>
        unwrap(response),
      providesTags: (result) => [
        { type: 'Account' as const, id: 'LIST' },
        ...(result ?? []).map(({ id }) => ({ type: 'Account' as const, id })),
      ],
    }),

    getAccount: builder.query<AccountResponse, string>({
      query: (id) => `/api/accounts/${id}`,
      transformResponse: (response: Schemas['ApiResponseOfAccountResponse']) => unwrap(response),
      providesTags: (_result, _error, id) => [{ type: 'Account' as const, id }],
    }),

    getAccountBalance: builder.query<BalanceResponse, { id: string; at?: string }>({
      query: ({ id, at }) => ({
        url: `/api/accounts/${id}/balance`,
        params: at ? { at } : undefined,
      }),
      transformResponse: (response: Schemas['ApiResponseOfBalanceResponse']) => unwrap(response),
      providesTags: (_result, _error, { id, at }) => (at ? [] : [{ type: 'Account' as const, id }]),
    }),

    createAccount: builder.mutation<AccountResponse, CreateAccountRequest>({
      query: (body) => ({ url: '/api/accounts', method: 'POST', body }),
      transformResponse: (response: Schemas['ApiResponseOfAccountResponse']) => unwrap(response),
      invalidatesTags: (_result, error) =>
        error ? [] : [{ type: 'Account' as const, id: 'LIST' }],
    }),

    renameAccount: builder.mutation<AccountResponse, { id: string; body: UpdateAccountRequest }>({
      query: ({ id, body }) => ({ url: `/api/accounts/${id}`, method: 'PATCH', body }),
      transformResponse: (response: Schemas['ApiResponseOfAccountResponse']) => unwrap(response),
      invalidatesTags: (_result, error, { id }) =>
        error ? [] : [{ type: 'Account' as const, id }],
    }),

    setPrimaryAccount: builder.mutation<void, string>({
      query: (id) => ({ url: `/api/accounts/${id}/set-primary`, method: 'PATCH' }),
      // Message-only ApiResponse — nothing to unwrap.
      transformResponse: () => undefined,
      invalidatesTags: (_result, error) => (error ? [] : ['Account']),
    }),

    deleteAccount: builder.mutation<void, string>({
      query: (id) => ({ url: `/api/accounts/${id}`, method: 'DELETE' }),
      transformResponse: () => undefined,
      invalidatesTags: (_result, error, id) =>
        error
          ? []
          : [
              { type: 'Account' as const, id: 'LIST' },
              { type: 'Account' as const, id },
            ],
    }),

    // ========== TRANSACTIONS ==========

    getTransactions: builder.query<PaginatedTransactions, TransactionsQuery>({
      // T1 is one of the two BARE responses — no envelope, no unwrap, by contract.
      query: (filters) => ({
        url: '/api/transactions',
        params: {
          AccountId: filters.accountId,
          FromDate: filters.fromDate,
          ToDate: filters.toDate,
          Page: filters.page,
          PageSize: filters.pageSize,
        },
      }),
      providesTags: [{ type: 'Transaction' as const, id: 'LIST' }],
    }),

    getTransaction: builder.query<TransactionResponse, string>({
      query: (id) => `/api/transactions/${id}`,
      transformResponse: (response: Schemas['ApiResponseOfTransactionResponse']) =>
        unwrap(response),
      providesTags: (_result, _error, id) => [{ type: 'Transaction' as const, id }],
    }),

    deposit: builder.mutation<WithReplay<DepositResponse>, IdempotentArg<DepositRequest>>({
      query: ({ idempotencyKey, body }) => ({
        url: '/api/transactions/deposit',
        method: 'POST',
        body,
        headers: { 'Idempotency-Key': idempotencyKey },
      }),
      transformResponse: (response: Schemas['ApiResponseOfDepositResponse'], meta) =>
        withReplay(unwrap(response), meta),
      invalidatesTags: (_result, error, { body }) =>
        error
          ? []
          : [
              { type: 'Account' as const, id: body.accountId },
              { type: 'Transaction' as const, id: 'LIST' },
            ],
    }),

    withdraw: builder.mutation<WithReplay<WithdrawResponse>, IdempotentArg<WithdrawRequest>>({
      // PIN travels in the BODY (D1) — this endpoint never triggers the step-up interceptor.
      query: ({ idempotencyKey, body }) => ({
        url: '/api/transactions/withdraw',
        method: 'POST',
        body,
        headers: { 'Idempotency-Key': idempotencyKey },
      }),
      transformResponse: (response: Schemas['ApiResponseOfWithdrawResponse'], meta) =>
        withReplay(unwrap(response), meta),
      invalidatesTags: (_result, error, { body }) =>
        error
          ? []
          : [
              { type: 'Account' as const, id: body.accountId },
              { type: 'Transaction' as const, id: 'LIST' },
            ],
    }),

    // ========== TRANSFERS (level-2 step-up — interceptor arrives with PR-11) ==========

    transfer: builder.mutation<WithReplay<TransferResponse>, IdempotentArg<TransferRequest>>({
      query: ({ idempotencyKey, body }) => ({
        url: '/api/transfers',
        method: 'POST',
        body,
        headers: { 'Idempotency-Key': idempotencyKey },
      }),
      transformResponse: (response: Schemas['ApiResponseOfTransferResponse'], meta) =>
        withReplay(unwrap(response), meta),
      invalidatesTags: (_result, error, { body }) =>
        error
          ? []
          : [
              { type: 'Account' as const, id: body.fromAccountId },
              { type: 'Transaction' as const, id: 'LIST' },
            ],
    }),

    // ========== BFF AUTH (cookie transport — no token ever reaches this code) ==========

    login: builder.mutation<BffLoginResponse, LoginRequest>({
      query: (body) => ({ url: '/bff/auth/login', method: 'POST', body }),
      transformResponse: (response: { data?: BffLoginResponse | null }) => unwrap(response),
      invalidatesTags: (_result, error) => (error ? [] : ['Session']),
    }),

    register: builder.mutation<BffLoginResponse, RegisterRequest>({
      query: (body) => ({ url: '/bff/auth/register', method: 'POST', body }),
      transformResponse: (response: { data?: BffLoginResponse | null }) => unwrap(response),
      invalidatesTags: (_result, error) => (error ? [] : ['Session']),
    }),

    getMe: builder.query<BffMeResponse, void>({
      // B3 — the ONE bootstrap probe (D6), and the deliberate "Stay signed in"
      // keep-alive: the BFF counts it as activity.
      query: () => '/bff/auth/me',
      transformResponse: (response: { data?: BffMeResponse | null }) => unwrap(response),
      providesTags: [{ type: 'Session' as const, id: 'CURRENT' }],
    }),

    logout: builder.mutation<void, void>({
      query: () => ({ url: '/bff/auth/logout', method: 'POST' }),
      // Message-only ApiResponse — nothing to unwrap.
      transformResponse: () => undefined,
      invalidatesTags: (_result, error) => (error ? [] : ['Session']),
    }),

    getSessionStatus: builder.query<BffSessionStatusResponse, void>({
      // B5 — BARE by contract (no envelope). The cheap guard re-check: the BFF
      // deliberately does NOT count it as session activity, so it can never
      // keep a session alive (ADR-0018). Never poll it on a timer regardless.
      query: () => '/bff/auth/session-status',
      providesTags: [{ type: 'Session' as const, id: 'STATUS' }],
    }),

    verifyPin: builder.mutation<BffPinVerificationResponse, VerifyPinRequest>({
      // B6 — wrong PIN is HTTP 200 with verified:false, NEVER an error (the error
      // channel would trip global 401/step-up handling).
      query: (body) => ({ url: '/bff/auth/verify-pin', method: 'POST', body }),
      transformResponse: (response: { data?: BffPinVerificationResponse | null }) =>
        unwrap(response),
      invalidatesTags: (_result, error) =>
        error ? [] : [{ type: 'Session' as const, id: 'STATUS' }],
    }),

    setPin: builder.mutation<void, SetPinRequest>({
      query: (body) => ({ url: '/bff/auth/set-pin', method: 'POST', body }),
      transformResponse: () => undefined,
      invalidatesTags: (_result, error) => (error ? [] : ['Session']),
    }),

    transferInternal: builder.mutation<
      WithReplay<InternalTransferResponse>,
      IdempotentArg<InternalTransferRequest>
    >({
      query: ({ idempotencyKey, body }) => ({
        url: '/api/transfers/internal',
        method: 'POST',
        body,
        headers: { 'Idempotency-Key': idempotencyKey },
      }),
      transformResponse: (response: Schemas['ApiResponseOfInternalTransferResponse'], meta) =>
        withReplay(unwrap(response), meta),
      invalidatesTags: (_result, error, { body }) =>
        error
          ? []
          : [
              { type: 'Account' as const, id: body.fromAccountId },
              { type: 'Account' as const, id: body.toAccountId },
              { type: 'Transaction' as const, id: 'LIST' },
            ],
    }),
  }),
});

export const {
  // Accounts
  useGetAccountsQuery,
  useGetAccountQuery,
  useGetAccountBalanceQuery,
  useCreateAccountMutation,
  useRenameAccountMutation,
  useSetPrimaryAccountMutation,
  useDeleteAccountMutation,
  // Transactions
  useGetTransactionsQuery,
  useGetTransactionQuery,
  useDepositMutation,
  useWithdrawMutation,
  // Transfers
  useTransferMutation,
  useTransferInternalMutation,
  // BFF auth
  useLoginMutation,
  useRegisterMutation,
  useGetMeQuery,
  useLogoutMutation,
  useGetSessionStatusQuery,
  useVerifyPinMutation,
  useSetPinMutation,
} = apiSlice;
