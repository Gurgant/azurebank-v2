import { createApi, fetchBaseQuery } from '@reduxjs/toolkit/query/react';
import type { RootState } from '../../app/store';

// ============================================
// BASE TYPES
// ============================================

interface Account {
  id: string;
  name: string;
  accountNumber: string;
  balance: number;
  type: 'checking' | 'savings' | 'investment';
  createdAt: string;
}

interface Transaction {
  id: string;
  transactionId: string;
  type: 'deposit' | 'withdrawal' | 'transfer_out' | 'transfer_in';
  amount: number;
  description: string;
  status: 'completed' | 'pending' | 'failed';
  date: string;
  accountId: string;
  balanceBefore: number;
  balanceAfter: number;
  fee?: number;
  recipientId?: string;
  recipientName?: string;
  notes?: string;
}

interface TransferRequest {
  fromAccountId: string;
  toAccountId?: string;
  toExternalAccount?: string;
  amount: number;
  description?: string;
}

interface DepositRequest {
  accountId: string;
  amount: number;
  description?: string;
}

interface WithdrawRequest {
  accountId: string;
  amount: number;
  description?: string;
}

interface CreateAccountRequest {
  name: string;
  type: 'checking' | 'savings' | 'investment';
}

interface TransactionFilters {
  from?: string;
  to?: string;
  type?: string;
  accountId?: string;
  page?: number;
  pageSize?: number;
}

interface PaginatedResponse<T> {
  data: T[];
  page: number;
  pageSize: number;
  totalItems: number;
  totalPages: number;
}

// ============================================
// API SLICE
// ============================================

export const apiSlice = createApi({
  reducerPath: 'api',
  baseQuery: fetchBaseQuery({
    baseUrl: '/api',
    prepareHeaders: (headers, { getState }) => {
      // Get the token from auth state
      const token = (getState() as RootState).auth.token;
      if (token) {
        headers.set('Authorization', `Bearer ${token}`);
      }
      headers.set('Content-Type', 'application/json');
      return headers;
    },
  }),
  tagTypes: ['Account', 'Transaction', 'User'],
  endpoints: (builder) => ({
    // ========== ACCOUNTS ==========

    getAccounts: builder.query<Account[], void>({
      query: () => '/accounts',
      providesTags: (result) =>
        result
          ? [
              ...result.map(({ id }) => ({ type: 'Account' as const, id })),
              { type: 'Account', id: 'LIST' },
            ]
          : [{ type: 'Account', id: 'LIST' }],
    }),

    getAccount: builder.query<Account, string>({
      query: (id) => `/accounts/${id}`,
      providesTags: (_result, _error, id) => [{ type: 'Account', id }],
    }),

    getAccountBalance: builder.query<{ balance: number; asOf: string }, { id: string; at?: string }>({
      query: ({ id, at }) => ({
        url: `/accounts/${id}/balance`,
        params: at ? { at } : undefined,
      }),
      providesTags: (_result, _error, { id }) => [{ type: 'Account', id }],
    }),

    createAccount: builder.mutation<Account, CreateAccountRequest>({
      query: (body) => ({
        url: '/accounts',
        method: 'POST',
        body,
      }),
      invalidatesTags: [{ type: 'Account', id: 'LIST' }],
    }),

    // ========== TRANSACTIONS ==========

    getTransactions: builder.query<PaginatedResponse<Transaction>, TransactionFilters>({
      query: (filters) => ({
        url: '/transactions',
        params: filters,
      }),
      providesTags: (result) =>
        result
          ? [
              ...result.data.map(({ id }) => ({ type: 'Transaction' as const, id })),
              { type: 'Transaction', id: 'LIST' },
            ]
          : [{ type: 'Transaction', id: 'LIST' }],
    }),

    getTransaction: builder.query<Transaction, string>({
      query: (id) => `/transactions/${id}`,
      providesTags: (_result, _error, id) => [{ type: 'Transaction', id }],
    }),

    deposit: builder.mutation<Transaction, DepositRequest>({
      query: (body) => ({
        url: '/transactions/deposit',
        method: 'POST',
        body,
      }),
      invalidatesTags: [
        { type: 'Transaction', id: 'LIST' },
        { type: 'Account', id: 'LIST' },
      ],
    }),

    withdraw: builder.mutation<Transaction, WithdrawRequest>({
      query: (body) => ({
        url: '/transactions/withdraw',
        method: 'POST',
        body,
      }),
      invalidatesTags: [
        { type: 'Transaction', id: 'LIST' },
        { type: 'Account', id: 'LIST' },
      ],
    }),

    // ========== TRANSFERS ==========

    transfer: builder.mutation<Transaction, TransferRequest>({
      query: (body) => ({
        url: '/transfers',
        method: 'POST',
        body,
      }),
      invalidatesTags: [
        { type: 'Transaction', id: 'LIST' },
        { type: 'Account', id: 'LIST' },
      ],
    }),
  }),
});

// Export hooks for usage in components
export const {
  // Accounts
  useGetAccountsQuery,
  useGetAccountQuery,
  useGetAccountBalanceQuery,
  useCreateAccountMutation,
  // Transactions
  useGetTransactionsQuery,
  useGetTransactionQuery,
  useDepositMutation,
  useWithdrawMutation,
  // Transfers
  useTransferMutation,
} = apiSlice;
