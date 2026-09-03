import { createApi } from '@reduxjs/toolkit/query/react';
import type { FetchBaseQueryMeta } from '@reduxjs/toolkit/query';
import type {
  BffLoginResponse,
  BffMeResponse,
  BffPinVerificationResponse,
  BffSessionStatusResponse,
} from '../../api/bffTypes';
import { unwrap } from '../../api/envelope';
import {
  bffLoginResponseSchema,
  bffMeResponseSchema,
  bffPinVerificationResponseSchema,
  bffSessionStatusResponseSchema,
} from '../../api/bffSchemas';
import { baseQueryWithStepUp } from '../../api/baseQueryWithStepUp';
import type { ApiProblem } from '../../api/problemBaseQuery';
import {
  accountNumberResponseSchema,
  accountResponseSchema,
  accountsListSchema,
  balanceResponseSchema,
  depositResponseSchema,
  devOnly,
  internalTransferResponseSchema,
  paginatedTransactionsSchema,
  recipientLookupResponseSchema,
  transactionResponseSchema,
  transactionSummarySchema,
  stepUpAuthorizationResponseSchema,
  transferResponseSchema,
  updateAzureTagResponseSchema,
  withdrawResponseSchema,
} from '../../api/responseSchemas';
import type { components } from '../../api/schema';

type Schemas = components['schemas'];

/**
 * Which tags a FAILED money mutation should invalidate.
 *
 * Every money endpoint invalidates nothing on error, which is right for almost all of them: a
 * rejected request changed no server state, so refetching would only add load. `INSUFFICIENT_FUNDS`
 * is the exception, and it is exactly backwards — that error IS the server telling us the balance
 * we hold is wrong. Leaving the cache alone meant the form kept the very number that caused the
 * rejection, so the user could walk into the same wall again, and the funds gate's own bound stayed
 * stale until something unrelated refreshed it.
 */
function staleBalanceTags(error: unknown, accountIds: (string | undefined)[]) {
  if ((error as ApiProblem | undefined)?.errorCode !== 'INSUFFICIENT_FUNDS') {
    return [];
  }
  return accountIds
    .filter((id): id is string => Boolean(id))
    .map((id) => ({ type: 'Account' as const, id }));
}

export type LoginRequest = Schemas['LoginRequest'];
export type RegisterRequest = Schemas['RegisterRequest'];
export type VerifyPinRequest = Schemas['VerifyPinRequest'];
export type SetPinRequest = Schemas['SetPinRequest'];
export type AccountResponse = Schemas['AccountResponse'];
export type BalanceResponse = Schemas['BalanceResponse'];
export type CreateAccountRequest = Schemas['CreateAccountRequest'];
export type UpdateAccountRequest = Schemas['UpdateAccountRequest'];
export type AccountNumberResponse = Schemas['AccountNumberResponse'];
export type TransactionResponse = Schemas['TransactionResponse'];
export type PaginatedTransactions = Schemas['PaginatedResponseOfTransactionResponse'];
export type TransactionSummaryResponse = Schemas['TransactionSummaryResponse'];
export type DepositRequest = Schemas['DepositRequest'];
export type DepositResponse = Schemas['DepositResponse'];
export type WithdrawRequest = Schemas['WithdrawRequest'];
export type WithdrawResponse = Schemas['WithdrawResponse'];
export type TransferRequest = Schemas['TransferRequest'];
export type TransferResponse = Schemas['TransferResponse'];
export type InternalTransferRequest = Schemas['InternalTransferRequest'];
export type InternalTransferResponse = Schemas['InternalTransferResponse'];
export type RecipientLookupResponse = Schemas['RecipientLookupResponse'];
export type TransferAuthorizationRequest = Schemas['TransferAuthorizationRequest'];
export type InternalTransferAuthorizationRequest = Schemas['InternalTransferAuthorizationRequest'];
export type StepUpAuthorizationResponse = Schemas['StepUpAuthorizationResponse'];
export type UpdateAzureTagRequest = Schemas['UpdateAzureTagRequest'];
export type UpdateAzureTagResponse = Schemas['UpdateAzureTagResponse'];

/** Argument shape of every idempotent money mutation — the key comes from useIdempotentMutation. */
export interface IdempotentArg<TBody> {
  idempotencyKey: string;
  body: TBody;
  /**
   * The step-up authorisation minted for exactly this amount and payee (ADR-0042). A SIBLING of
   * `body`, never a field inside it, and the separation is load-bearing rather than tidy: the
   * server fingerprints the request BODY alone, so an authorisation carried in the body would
   * change those bytes and make every retry a 422 `IDEMPOTENCY_KEY_REUSE` instead of reaching the
   * endpoint. Keeping it out here is what lets the same transfer be resent byte-identically while
   * the authorisation differs, expires, or is absent.
   *
   * Optional in the TYPE even though the two transfers now require it on the wire (ADR-0042), and
   * that is a deliberate limit rather than an oversight. Making it required here needs the whole
   * hook chain to be generic over the argument, which was tried and measured: `TBody` then has no
   * inference site left, degrades to `unknown`, and every money body silently stops being
   * type-checked — a transfer still carrying the deleted `pin` compiled clean. Losing real body
   * safety to gain a weaker one is a bad trade.
   *
   * So the requirement is held where it is checkable: `required: true` on the published parameter
   * (StepUpAuthorizationOperationTransformer), and a mock that refuses a headerless transfer with
   * 401 AUTHORIZATION_REQUIRED exactly as the API does — so dropping it turns tests red.
   */
  stepUpAuthorizationId?: string;
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

/** T1 filters — paging belongs to the infinite machinery, never to the caller. */
export type TransactionHistoryFilters = Omit<TransactionsQuery, 'page' | 'pageSize'>;

/** One page per request; small enough to bound the accepted refetch-all-pages cost (D7). */
export const HISTORY_PAGE_SIZE = 20;

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
  // Step-up interceptor (ADR-0022): transparently elevates + replays on a level-2 403.
  baseQuery: baseQueryWithStepUp,
  tagTypes: ['Account', 'Transaction', 'Session'],
  endpoints: (builder) => ({
    // ========== ACCOUNTS ==========

    getAccounts: builder.query<AccountResponse[], void>({
      query: () => '/api/accounts',
      // STRICT (A): balances drive every money screen — a drifted shape must fail the
      // query, never render wrong numbers.
      transformResponse: (response: Schemas['ApiResponseOfListOfAccountResponse']) =>
        unwrap(response, accountsListSchema),
      providesTags: (result) => [
        { type: 'Account' as const, id: 'LIST' },
        ...(result ?? []).map(({ id }) => ({ type: 'Account' as const, id })),
      ],
    }),

    getAccount: builder.query<AccountResponse, string>({
      query: (id) => `/api/accounts/${id}`,
      transformResponse: (response: Schemas['ApiResponseOfAccountResponse']) =>
        unwrap(response, devOnly(accountResponseSchema)),
      providesTags: (_result, _error, id) => [{ type: 'Account' as const, id }],
    }),

    getAccountBalance: builder.query<BalanceResponse, { id: string; at?: string }>({
      query: ({ id, at }) => ({
        url: `/api/accounts/${id}/balance`,
        params: at ? { at } : undefined,
      }),
      transformResponse: (response: Schemas['ApiResponseOfBalanceResponse']) =>
        unwrap(response, devOnly(balanceResponseSchema)),
      providesTags: (_result, _error, { id, at }) => (at ? [] : [{ type: 'Account' as const, id }]),
    }),

    createAccount: builder.mutation<AccountResponse, CreateAccountRequest>({
      query: (body) => ({ url: '/api/accounts', method: 'POST', body }),
      transformResponse: (response: Schemas['ApiResponseOfAccountResponse']) =>
        unwrap(response, devOnly(accountResponseSchema)),
      invalidatesTags: (_result, error) =>
        error ? [] : [{ type: 'Account' as const, id: 'LIST' }],
    }),

    renameAccount: builder.mutation<AccountResponse, { id: string; body: UpdateAccountRequest }>({
      query: ({ id, body }) => ({ url: `/api/accounts/${id}`, method: 'PATCH', body }),
      transformResponse: (response: Schemas['ApiResponseOfAccountResponse']) =>
        unwrap(response, devOnly(accountResponseSchema)),
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

    // Reveal the full (unmasked) account number. A MUTATION on purpose (ADR-0020): it must never
    // be cached and never auto-retried — it fires only on explicit user intent and the value lives
    // in transient component state, never the query cache. The path suffix `/full-number` is
    // level-2 gated, so an un-elevated call 403s and rides baseQueryWithStepUp into the PIN modal,
    // which replays the GET automatically. GET verb, no idempotency key, no cache tags.
    revealAccountNumber: builder.mutation<AccountNumberResponse, string>({
      query: (id) => ({ url: `/api/accounts/${id}/full-number`, method: 'GET' }),
      transformResponse: (response: Schemas['ApiResponseOfAccountNumberResponse']) =>
        unwrap(response, devOnly(accountNumberResponseSchema)),
    }),

    // ========== USER (self) ==========

    /*
      Rename the caller's own public AzureTag handle (ADR-0015) — a payment handle, not identity.
      Level-1 (no step-up).

      ON /bff, NOT THE PROXIED /api ROUTE, and the comment here used to be wrong about why. It said
      the Session invalidation meant "the getMe probe refetches and the auth slice picks up the new
      tag (same pattern as setPin)". Measured on the running stack: it did not. `/bff/auth/me` serves
      the BFF's CACHED session, so the refetch returned the old handle for the life of the session —
      and it was never the same pattern as setPin, because setPin is BFF-owned and writes the cache
      back while this was a plain proxied PATCH that ran no BFF code at all.

      The invalidation below is still right and still needed; it just needed an endpoint that makes
      the refetch tell the truth.
    */
    renameAzureTag: builder.mutation<UpdateAzureTagResponse, UpdateAzureTagRequest>({
      query: (body) => ({ url: '/bff/auth/azuretag', method: 'PATCH', body }),
      transformResponse: (response: Schemas['ApiResponseOfUpdateAzureTagResponse']) =>
        unwrap(response, devOnly(updateAzureTagResponseSchema)),
      invalidatesTags: (_result, error) => (error ? [] : ['Session']),
    }),

    // ========== TRANSACTIONS ==========

    getTransactions: builder.query<PaginatedTransactions, TransactionsQuery>({
      // T1 is one of the two BARE responses — no envelope, no unwrap, by contract.
      // Dev/test-only validation (C): catches mock drift without a prod crash-surface.
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
      transformResponse: (response: PaginatedTransactions) =>
        devOnly(paginatedTransactionsSchema)?.parse(response) ?? response,
      providesTags: [{ type: 'Transaction' as const, id: 'LIST' }],
    }),

    /**
     * T1 — the history feed. An INFINITE query: pages accumulate client-side and the
     * whole family carries {Transaction,'LIST'}, so any money mutation refetches every
     * loaded page (the accepted D7 cost — HISTORY_PAGE_SIZE bounds it) rather than
     * hand-patching the cache.
     */
    getTransactionHistory: builder.infiniteQuery<
      PaginatedTransactions,
      TransactionHistoryFilters,
      number
    >({
      infiniteQueryOptions: {
        initialPageParam: 1,
        getNextPageParam: (lastPage) =>
          lastPage.pagination?.hasNextPage ? lastPage.pagination.page + 1 : undefined,
      },
      query: ({ queryArg, pageParam }) => ({
        url: '/api/transactions',
        params: {
          AccountId: queryArg.accountId,
          FromDate: queryArg.fromDate,
          ToDate: queryArg.toDate,
          Page: pageParam,
          PageSize: HISTORY_PAGE_SIZE,
        },
      }),
      // Same dev/test-only page validation as the flat list (C).
      transformResponse: (response: PaginatedTransactions) =>
        devOnly(paginatedTransactionsSchema)?.parse(response) ?? response,
      providesTags: [{ type: 'Transaction' as const, id: 'LIST' }],
    }),

    getTransaction: builder.query<TransactionResponse, string>({
      query: (id) => `/api/transactions/${id}`,
      transformResponse: (response: Schemas['ApiResponseOfTransactionResponse']) =>
        unwrap(response, devOnly(transactionResponseSchema)),
      providesTags: (_result, _error, id) => [{ type: 'Transaction' as const, id }],
    }),

    /**
     * Server-side aggregate for the dashboard's Monthly Summary — SQL SUM, never a
     * fetch-a-page-and-reduce on the client. The caller sends only fromDate and leaves
     * toDate to the server's per-request "now" default ON PURPOSE: the query carries the
     * Transaction LIST tag, so every money mutation refetches it — a frozen toDate would
     * exclude the very transaction that triggered the refetch. The resolved window comes
     * back in the response.
     */
    getTransactionSummary: builder.query<
      TransactionSummaryResponse,
      { fromDate: string; toDate?: string; accountId?: string }
    >({
      query: ({ fromDate, toDate, accountId }) => ({
        url: '/api/transactions/summary',
        // `accountId` omitted means every account the caller owns, which is the server's default
        // and stays the dashboard's "All accounts" case. Sent as `AccountId` to match the filter's
        // property name, like the two dates above it.
        params: {
          FromDate: fromDate,
          ...(toDate ? { ToDate: toDate } : {}),
          ...(accountId ? { AccountId: accountId } : {}),
        },
      }),
      // STRICT (A): the dashboard's money aggregate — drift must fail, not render.
      transformResponse: (response: Schemas['ApiResponseOfTransactionSummaryResponse']) =>
        unwrap(response, transactionSummarySchema),
      providesTags: [{ type: 'Transaction' as const, id: 'LIST' }],
    }),

    deposit: builder.mutation<WithReplay<DepositResponse>, IdempotentArg<DepositRequest>>({
      query: ({ idempotencyKey, body }) => ({
        url: '/api/transactions/deposit',
        method: 'POST',
        body,
        headers: { 'Idempotency-Key': idempotencyKey },
      }),
      // STRICT (A): a money receipt — newBalance/reference must match the contract exactly.
      transformResponse: (response: Schemas['ApiResponseOfDepositResponse'], meta) =>
        withReplay(unwrap(response, depositResponseSchema), meta),
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
      // STRICT (A): a money receipt.
      transformResponse: (response: Schemas['ApiResponseOfWithdrawResponse'], meta) =>
        withReplay(unwrap(response, withdrawResponseSchema), meta),
      invalidatesTags: (_result, error, { body }) =>
        error
          ? staleBalanceTags(error, [body.accountId])
          : [
              { type: 'Account' as const, id: body.accountId },
              { type: 'Transaction' as const, id: 'LIST' },
            ],
    }),

    // ========== TRANSFERS (in-band authorisation, ADR-0041/0042 — no level-2 gate) ==========

    // Confirm a transfer recipient by EXACT AzureTag (ADR-0014 — no directory/substring).
    // Level-1 (not step-up gated); a nonexistent or self tag returns 200 { exists:false }.
    lookupRecipient: builder.query<RecipientLookupResponse, string>({
      query: (azureTag) => `/api/users/${encodeURIComponent(azureTag)}`,
      transformResponse: (response: Schemas['ApiResponseOfRecipientLookupResponse']) =>
        unwrap(response, devOnly(recipientLookupResponseSchema)),
    }),

    /*
      MINTING (ADR-0042). Two endpoints, no Idempotency-Key on either, and that is deliberate:
      minting moves no money, so there is nothing to deduplicate, and a repeat simply produces a
      second authorisation of which only one can ever be spent. Requiring a key would add a failure
      mode to the one call whose whole job is to be easy to make again after a wrong PIN.

      What a repeat DOES cost is a PIN attempt — minting IS the authentication event, and must not
      be a cheaper oracle than the transfer itself. So these answer with the same PIN codes the
      transfer does (401 INVALID_PIN, 429 PIN_LOCKED, 422 PIN_REQUIRED) and the pages route all
      three through the branch that already handles them.

      No cache invalidation: nothing a query can observe has changed.
    */
    authoriseTransfer: builder.mutation<StepUpAuthorizationResponse, TransferAuthorizationRequest>({
      query: (body) => ({ url: '/api/transfers/authorizations', method: 'POST', body }),
      // STRICT: a drifted authorisationId is a header the server cannot match, refused with no
      // way for the user to tell why.
      transformResponse: (response: Schemas['ApiResponseOfStepUpAuthorizationResponse']) =>
        unwrap(response, stepUpAuthorizationResponseSchema),
    }),

    authoriseInternalTransfer: builder.mutation<
      StepUpAuthorizationResponse,
      InternalTransferAuthorizationRequest
    >({
      query: (body) => ({
        url: '/api/transfers/internal/authorizations',
        method: 'POST',
        body,
      }),
      transformResponse: (response: Schemas['ApiResponseOfStepUpAuthorizationResponse']) =>
        unwrap(response, stepUpAuthorizationResponseSchema),
    }),

    transfer: builder.mutation<WithReplay<TransferResponse>, IdempotentArg<TransferRequest>>({
      query: ({ idempotencyKey, body, stepUpAuthorizationId }) => ({
        url: '/api/transfers',
        method: 'POST',
        body,
        headers: {
          'Idempotency-Key': idempotencyKey,
          /*
            Spread rather than a `?? undefined` value: an explicit `undefined` still serialises as a
            header with an empty VALUE on some transports.

            An earlier version of this comment said such an empty header is "a malformed GUID (400)".
            MEASURED, and it is not: `[FromHeader] Guid?` binds an empty value to NULL, so the API
            answered as though no header had been sent at all. Verified on the wire with `curl -v`
            against the running API. It matters more now than it did then — after ADR-0042's flip an
            absent authorisation is `401 AUTHORIZATION_REQUIRED`, so an accidentally-empty header
            would fail the transfer rather than be caught as a client bug.
          */
          ...(stepUpAuthorizationId ? { 'Step-Up-Authorization': stepUpAuthorizationId } : {}),
        },
      }),
      // STRICT (A): a money receipt.
      transformResponse: (response: Schemas['ApiResponseOfTransferResponse'], meta) =>
        withReplay(unwrap(response, transferResponseSchema), meta),
      invalidatesTags: (_result, error, { body }) =>
        error
          ? staleBalanceTags(error, [body.fromAccountId])
          : [
              { type: 'Account' as const, id: body.fromAccountId },
              { type: 'Transaction' as const, id: 'LIST' },
            ],
    }),

    // ========== BFF AUTH (cookie transport — no token ever reaches this code) ==========

    login: builder.mutation<BffLoginResponse, LoginRequest>({
      query: (body) => ({ url: '/bff/auth/login', method: 'POST', body }),
      transformResponse: (response: { data?: BffLoginResponse | null }) =>
        unwrap(response, bffLoginResponseSchema),
      invalidatesTags: (_result, error) => (error ? [] : ['Session']),
    }),

    register: builder.mutation<BffLoginResponse, RegisterRequest>({
      query: (body) => ({ url: '/bff/auth/register', method: 'POST', body }),
      transformResponse: (response: { data?: BffLoginResponse | null }) =>
        unwrap(response, bffLoginResponseSchema),
      invalidatesTags: (_result, error) => (error ? [] : ['Session']),
    }),

    /**
     * U6.7 — re-authenticate at the ABSOLUTE session cap, which cannot be extended.
     *
     * Password only: the BFF takes the identity from the session, so this cannot sign anyone in as
     * somebody else while their page is still on screen.
     *
     * `invalidatesTags: ['Session']` is not bookkeeping — it is how the client finds out. Re-
     * authentication moves the absolute deadline, and `AuthBootstrap` holds a live `useGetMeQuery`
     * subscription, so invalidating refetches `/me`; a fulfilled `getMe` is the ONE action
     * `sessionMiddleware` learns the session policy from. Pinned by a test, since nothing in this
     * file makes the chain visible.
     *
     * It is not the only route, and must not be treated as one. `syncFromProbe` also adopts a LATER
     * cap from a `session-status` probe (ADR-0026), which is what covers the refetch above never
     * landing — a case that otherwise signs out a user seconds after they proved their password.
     */
    reauthenticate: builder.mutation<BffLoginResponse, { password: string }>({
      query: (body) => ({ url: '/bff/auth/reauthenticate', method: 'POST', body }),
      transformResponse: (response: { data?: BffLoginResponse | null }) =>
        unwrap(response, bffLoginResponseSchema),
      invalidatesTags: (_result, error) => (error ? [] : ['Session']),
    }),

    getMe: builder.query<BffMeResponse, void>({
      // B3 — the ONE bootstrap probe (D6), and the deliberate "Stay signed in"
      // keep-alive: the BFF counts it as activity.
      query: () => '/bff/auth/me',
      transformResponse: (response: { data?: BffMeResponse | null }) =>
        unwrap(response, bffMeResponseSchema),
      providesTags: [{ type: 'Session' as const, id: 'CURRENT' }],
    }),

    logout: builder.mutation<void, void>({
      query: () => ({ url: '/bff/auth/logout', method: 'POST' }),
      // Message-only ApiResponse — nothing to unwrap.
      transformResponse: () => undefined,
      invalidatesTags: (_result, error) => (error ? [] : ['Session']),
    }),

    getSessionStatus: builder.query<BffSessionStatusResponse, void>({
      // B5 — BARE by contract (no envelope). The BFF deliberately does NOT count it as session
      // activity, so it can never keep a session alive (ADR-0018) — which is also what makes it the
      // only endpoint whose expiry timestamps mean anything. Everywhere else, reading the deadline
      // resets it.
      //
      // Still never polled on a timer. SessionExpiryWarning probes it exactly twice: when the tab
      // becomes visible again, and once at the moment its countdown reaches zero, to confirm the
      // session really is gone before ending it. Both are events, not a heartbeat.
      query: () => '/bff/auth/session-status',
      // Bare response — validate the raw body directly (no envelope to unwrap).
      transformResponse: (response: BffSessionStatusResponse) =>
        bffSessionStatusResponseSchema.parse(response),
      providesTags: [{ type: 'Session' as const, id: 'STATUS' }],
    }),

    verifyPin: builder.mutation<BffPinVerificationResponse, VerifyPinRequest>({
      // B6 — wrong PIN is HTTP 200 with verified:false, NEVER an error (the error
      // channel would trip global 401/step-up handling).
      query: (body) => ({ url: '/bff/auth/verify-pin', method: 'POST', body }),
      transformResponse: (response: { data?: BffPinVerificationResponse | null }) =>
        unwrap(response, bffPinVerificationResponseSchema),
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
      query: ({ idempotencyKey, body, stepUpAuthorizationId }) => ({
        url: '/api/transfers/internal',
        method: 'POST',
        body,
        headers: {
          'Idempotency-Key': idempotencyKey,
          /*
            Spread rather than a `?? undefined` value: an explicit `undefined` still serialises as a
            header with an empty VALUE on some transports.

            An earlier version of this comment said such an empty header is "a malformed GUID (400)".
            MEASURED, and it is not: `[FromHeader] Guid?` binds an empty value to NULL, so the API
            answered as though no header had been sent at all. Verified on the wire with `curl -v`
            against the running API. It matters more now than it did then — after ADR-0042's flip an
            absent authorisation is `401 AUTHORIZATION_REQUIRED`, so an accidentally-empty header
            would fail the transfer rather than be caught as a client bug.
          */
          ...(stepUpAuthorizationId ? { 'Step-Up-Authorization': stepUpAuthorizationId } : {}),
        },
      }),
      // STRICT (A): a money receipt.
      transformResponse: (response: Schemas['ApiResponseOfInternalTransferResponse'], meta) =>
        withReplay(unwrap(response, internalTransferResponseSchema), meta),
      invalidatesTags: (_result, error, { body }) =>
        error
          ? staleBalanceTags(error, [body.fromAccountId, body.toAccountId])
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
  useRevealAccountNumberMutation,
  // User (self)
  useRenameAzureTagMutation,
  // Transactions
  useGetTransactionsQuery,
  useGetTransactionHistoryInfiniteQuery,
  useGetTransactionQuery,
  useGetTransactionSummaryQuery,
  useDepositMutation,
  useWithdrawMutation,
  // Transfers
  useLazyLookupRecipientQuery,
  useAuthoriseTransferMutation,
  useAuthoriseInternalTransferMutation,
  useTransferMutation,
  useTransferInternalMutation,
  // BFF auth
  useLoginMutation,
  useRegisterMutation,
  useReauthenticateMutation,
  useGetMeQuery,
  useLogoutMutation,
  useGetSessionStatusQuery,
  useVerifyPinMutation,
  useSetPinMutation,
} = apiSlice;
