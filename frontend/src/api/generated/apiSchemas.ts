import { z } from 'zod';

// <Schemas>
export type AccountNumberResponse = z.infer<typeof AccountNumberResponse>;
export const AccountNumberResponse = z.object({ accountId: z.uuid(), accountNumber: z.string() });

export type AccountType = z.infer<typeof AccountType>;
export const AccountType = z.enum(['Checking', 'Savings', 'Investment']);

export type AccountResponse = z.infer<typeof AccountResponse>;
export const AccountResponse = z.object({
  id: z.uuid(),
  accountNumber: z.string(),
  name: z.string(),
  type: AccountType,
  balance: z.number(),
  isPrimary: z.boolean(),
  createdAt: z.iso.datetime(),
});

export type ApiResponse = z.infer<typeof ApiResponse>;
export const ApiResponse = z.object({ message: z.string().nullable() }).partial();

export type ApiResponseOfAccountNumberResponse = z.infer<typeof ApiResponseOfAccountNumberResponse>;
export const ApiResponseOfAccountNumberResponse = z
  .object({ data: AccountNumberResponse.nullable(), message: z.string().nullable() })
  .partial();

export type ApiResponseOfAccountResponse = z.infer<typeof ApiResponseOfAccountResponse>;
export const ApiResponseOfAccountResponse = z
  .object({ data: AccountResponse.nullable(), message: z.string().nullable() })
  .partial();

export type BalanceResponse = z.infer<typeof BalanceResponse>;
export const BalanceResponse = z.object({
  accountId: z.uuid(),
  balance: z.number(),
  currency: z.string().optional(),
  asOf: z.iso.datetime(),
  isHistorical: z.boolean(),
});

export type ApiResponseOfBalanceResponse = z.infer<typeof ApiResponseOfBalanceResponse>;
export const ApiResponseOfBalanceResponse = z
  .object({ data: BalanceResponse.nullable(), message: z.string().nullable() })
  .partial();

export type TransactionType = z.infer<typeof TransactionType>;
export const TransactionType = z.enum(['Deposit', 'Withdrawal', 'TransferIn', 'TransferOut']);

export type TransactionStatus = z.infer<typeof TransactionStatus>;
export const TransactionStatus = z.enum(['Pending', 'Completed', 'Failed', 'Reversed']);

export type TransactionResponse = z.infer<typeof TransactionResponse>;
export const TransactionResponse = z.object({
  id: z.uuid(),
  transactionNumber: z.string(),
  type: TransactionType,
  amount: z.number(),
  balanceAfter: z.number(),
  description: z.string().nullable().optional(),
  recipientAzureTag: z.string().nullable().optional(),
  senderAzureTag: z.string().nullable().optional(),
  status: TransactionStatus,
  createdAt: z.iso.datetime(),
});

export type DepositResponse = z.infer<typeof DepositResponse>;
export const DepositResponse = z.object({
  transaction: TransactionResponse,
  newBalance: z.number(),
});

export type ApiResponseOfDepositResponse = z.infer<typeof ApiResponseOfDepositResponse>;
export const ApiResponseOfDepositResponse = z
  .object({ data: DepositResponse.nullable(), message: z.string().nullable() })
  .partial();

export type InternalTransferResponse = z.infer<typeof InternalTransferResponse>;
export const InternalTransferResponse = z.object({
  transferId: z.uuid(),
  transactionNumber: z.string(),
  fromAccountId: z.uuid(),
  toAccountId: z.uuid(),
  amount: z.number(),
  description: z.string().nullable().optional(),
  fromAccountNewBalance: z.number(),
  toAccountNewBalance: z.number(),
  processedAt: z.iso.datetime(),
});

export type ApiResponseOfInternalTransferResponse = z.infer<
  typeof ApiResponseOfInternalTransferResponse
>;
export const ApiResponseOfInternalTransferResponse = z
  .object({ data: InternalTransferResponse.nullable(), message: z.string().nullable() })
  .partial();

export type ApiResponseOfListOfAccountResponse = z.infer<typeof ApiResponseOfListOfAccountResponse>;
export const ApiResponseOfListOfAccountResponse = z
  .object({ data: z.array(AccountResponse).nullable(), message: z.string().nullable() })
  .partial();

export type UserLoginInfo = z.infer<typeof UserLoginInfo>;
export const UserLoginInfo = z.object({
  id: z.uuid(),
  azureTag: z.string(),
  email: z.string(),
  firstName: z.string(),
  lastName: z.string(),
  hasPin: z.boolean(),
});

export type LoginResponse = z.infer<typeof LoginResponse>;
export const LoginResponse = z.object({
  token: z.string(),
  expiresAt: z.iso.datetime(),
  refreshToken: z.string().nullable().optional(),
  user: UserLoginInfo,
});

export type ApiResponseOfLoginResponse = z.infer<typeof ApiResponseOfLoginResponse>;
export const ApiResponseOfLoginResponse = z
  .object({ data: LoginResponse.nullable(), message: z.string().nullable() })
  .partial();

export type ApiResponseOfObject = z.infer<typeof ApiResponseOfObject>;
export const ApiResponseOfObject = z
  .object({ data: z.unknown(), message: z.string().nullable() })
  .partial();

export type RecipientLookupResponse = z.infer<typeof RecipientLookupResponse>;
export const RecipientLookupResponse = z.object({
  azureTag: z.string(),
  displayName: z.string(),
  exists: z.boolean(),
});

export type ApiResponseOfRecipientLookupResponse = z.infer<
  typeof ApiResponseOfRecipientLookupResponse
>;
export const ApiResponseOfRecipientLookupResponse = z
  .object({ data: RecipientLookupResponse.nullable(), message: z.string().nullable() })
  .partial();

export type RefreshResponse = z.infer<typeof RefreshResponse>;
export const RefreshResponse = z.object({
  accessToken: z.string(),
  refreshToken: z.string(),
  expiresAt: z.iso.datetime(),
});

export type ApiResponseOfRefreshResponse = z.infer<typeof ApiResponseOfRefreshResponse>;
export const ApiResponseOfRefreshResponse = z
  .object({ data: RefreshResponse.nullable(), message: z.string().nullable() })
  .partial();

export type TokenResponse = z.infer<typeof TokenResponse>;
export const TokenResponse = z.object({
  accessToken: z.string(),
  refreshToken: z.string().nullable().optional(),
  expiresIn: z.number().int(),
  tokenType: z.string().optional(),
  expiresAt: z.iso.datetime(),
});

export type RegisterResponse = z.infer<typeof RegisterResponse>;
export const RegisterResponse = z.object({
  user: UserLoginInfo,
  account: AccountResponse,
  token: TokenResponse,
});

export type ApiResponseOfRegisterResponse = z.infer<typeof ApiResponseOfRegisterResponse>;
export const ApiResponseOfRegisterResponse = z
  .object({ data: RegisterResponse.nullable(), message: z.string().nullable() })
  .partial();

export type ApiResponseOfTransactionResponse = z.infer<typeof ApiResponseOfTransactionResponse>;
export const ApiResponseOfTransactionResponse = z
  .object({ data: TransactionResponse.nullable(), message: z.string().nullable() })
  .partial();

export type TransactionSummaryResponse = z.infer<typeof TransactionSummaryResponse>;
export const TransactionSummaryResponse = z.object({
  totalIncome: z.number(),
  totalExpenses: z.number(),
  netChange: z.number(),
  pendingCount: z.number().int(),
  fromDate: z.iso.datetime(),
  toDate: z.iso.datetime(),
});

export type ApiResponseOfTransactionSummaryResponse = z.infer<
  typeof ApiResponseOfTransactionSummaryResponse
>;
export const ApiResponseOfTransactionSummaryResponse = z
  .object({ data: TransactionSummaryResponse.nullable(), message: z.string().nullable() })
  .partial();

export type TransferResponse = z.infer<typeof TransferResponse>;
export const TransferResponse = z.object({
  transactionNumber: z.string(),
  amount: z.number(),
  newBalance: z.number(),
  recipientAzureTag: z.string(),
  recipientName: z.string().nullable().optional(),
  processedAt: z.iso.datetime(),
});

export type ApiResponseOfTransferResponse = z.infer<typeof ApiResponseOfTransferResponse>;
export const ApiResponseOfTransferResponse = z
  .object({ data: TransferResponse.nullable(), message: z.string().nullable() })
  .partial();

export type UpdateAzureTagResponse = z.infer<typeof UpdateAzureTagResponse>;
export const UpdateAzureTagResponse = z.object({ azureTag: z.string() });

export type ApiResponseOfUpdateAzureTagResponse = z.infer<
  typeof ApiResponseOfUpdateAzureTagResponse
>;
export const ApiResponseOfUpdateAzureTagResponse = z
  .object({ data: UpdateAzureTagResponse.nullable(), message: z.string().nullable() })
  .partial();

export type UserResponse = z.infer<typeof UserResponse>;
export const UserResponse = z.object({
  userId: z.uuid(),
  azureTag: z.string(),
  email: z.string(),
  firstName: z.string(),
  lastName: z.string(),
});

export type ApiResponseOfUserResponse = z.infer<typeof ApiResponseOfUserResponse>;
export const ApiResponseOfUserResponse = z
  .object({ data: UserResponse.nullable(), message: z.string().nullable() })
  .partial();

export type WithdrawResponse = z.infer<typeof WithdrawResponse>;
export const WithdrawResponse = z.object({
  transaction: TransactionResponse,
  newBalance: z.number(),
});

export type ApiResponseOfWithdrawResponse = z.infer<typeof ApiResponseOfWithdrawResponse>;
export const ApiResponseOfWithdrawResponse = z
  .object({ data: WithdrawResponse.nullable(), message: z.string().nullable() })
  .partial();

export type CreateAccountRequest = z.infer<typeof CreateAccountRequest>;
export const CreateAccountRequest = z.object({
  name: z.string().min(2).max(100),
  type: AccountType,
});

export type DepositRequest = z.infer<typeof DepositRequest>;
export const DepositRequest = z.object({
  accountId: z.uuid(),
  amount: z.number().min(0.01).max(100000).multipleOf(0.01),
  description: z.string().max(500).nullable().optional(),
});

export type InternalTransferRequest = z.infer<typeof InternalTransferRequest>;
export const InternalTransferRequest = z.object({
  fromAccountId: z.uuid(),
  toAccountId: z.uuid(),
  amount: z.number().min(0.01).max(100000).multipleOf(0.01),
  description: z.string().max(500).nullable().optional(),
});

export type LoginRequest = z.infer<typeof LoginRequest>;
export const LoginRequest = z.object({
  email: z.email().min(1).max(255),
  password: z
    .string()
    .min(8)
    .max(128)
    .regex(new RegExp('^(?=.*[a-z])(?=.*[A-Z])(?=.*[0-9])(?=.*[^a-zA-Z0-9])[\\x20-\\x7E]{8,128}$')),
});

export type PaginationMetadata = z.infer<typeof PaginationMetadata>;
export const PaginationMetadata = z.object({
  page: z.number().int(),
  pageSize: z.number().int(),
  totalItems: z.number().int(),
  totalPages: z.number().int(),
  hasNextPage: z.boolean(),
  hasPreviousPage: z.boolean(),
});

export type PaginatedResponseOfTransactionResponse = z.infer<
  typeof PaginatedResponseOfTransactionResponse
>;
export const PaginatedResponseOfTransactionResponse = z
  .object({ data: z.array(TransactionResponse), pagination: PaginationMetadata })
  .partial();

export type ProblemDetails = z.infer<typeof ProblemDetails>;
export const ProblemDetails = z
  .object({
    type: z.string().nullable(),
    title: z.string().nullable(),
    status: z.number().int(),
    detail: z.string().nullable(),
    instance: z.string().nullable(),
  })
  .partial();

export type RefreshRequest = z.infer<typeof RefreshRequest>;
export const RefreshRequest = z.object({ refreshToken: z.string().min(1) });

export type RegisterRequest = z.infer<typeof RegisterRequest>;
export const RegisterRequest = z.object({
  azureTag: z.string().min(3).max(20).regex(new RegExp('^[a-z][a-z0-9_]{2,19}$')),
  email: z.email().min(0).max(255),
  password: z
    .string()
    .min(8)
    .max(128)
    .regex(new RegExp('^(?=.*[a-z])(?=.*[A-Z])(?=.*[0-9])(?=.*[^a-zA-Z0-9])[\\x20-\\x7E]{8,128}$')),
  firstName: z.string().min(2).max(50).regex(new RegExp("^[a-zA-ZÀ-ÖØ-öø-ÿ\\s'-]{2,50}$")),
  lastName: z.string().min(2).max(50).regex(new RegExp("^[a-zA-ZÀ-ÖØ-öø-ÿ\\s'-]{2,50}$")),
});

export type SetPinRequest = z.infer<typeof SetPinRequest>;
export const SetPinRequest = z.object({
  pin: z.string().min(6).max(6).regex(new RegExp('^[0-9]{6}$')),
});

export type TransferRequest = z.infer<typeof TransferRequest>;
export const TransferRequest = z.object({
  fromAccountId: z.uuid(),
  recipientAzureTag: z.string().min(3).max(20).regex(new RegExp('^[a-z][a-z0-9_]{2,19}$')),
  amount: z.number().min(0.01).max(100000).multipleOf(0.01),
  description: z.string().max(500).nullable().optional(),
});

export type UpdateAccountRequest = z.infer<typeof UpdateAccountRequest>;
export const UpdateAccountRequest = z.object({ name: z.string().min(2).max(100) });

export type UpdateAzureTagRequest = z.infer<typeof UpdateAzureTagRequest>;
export const UpdateAzureTagRequest = z.object({
  azureTag: z.string().min(3).max(20).regex(new RegExp('^[a-z][a-z0-9_]{2,19}$')),
});

export type VerifyPinRequest = z.infer<typeof VerifyPinRequest>;
export const VerifyPinRequest = z.object({
  pin: z.string().min(6).max(6).regex(new RegExp('^[0-9]{6}$')),
});

export type WithdrawRequest = z.infer<typeof WithdrawRequest>;
export const WithdrawRequest = z.object({
  accountId: z.uuid(),
  amount: z.number().min(0.01).max(100000).multipleOf(0.01),
  pin: z.string().min(6).max(6).regex(new RegExp('^[0-9]{6}$')),
  description: z.string().max(500).nullable().optional(),
});

// </Schemas>
