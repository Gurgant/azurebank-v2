import { z } from 'zod';
import { makeAmountSchema, MIN_MONEY_AMOUNT } from '../utils/amountSchema';
import { formatCurrency } from '../utils/format';

/**
 * RHF+Zod backbone for the money forms (deposit / withdraw / transfer / internal transfer).
 *
 * Design (locked in the plan):
 * - Amount lives in form state as a SANITIZED STRING (what the user sees) and is coerced to a
 *   number by the schema — `z.string().transform(parse).pipe(makeAmountSchema(...))` — so the
 *   runtime bounds are the SAME #33 schema the imperative checks used, while each form keeps
 *   its exact legacy error copy via the messages overrides.
 * - Schemas with a balance bound are built per render via useMemo(balance) and fed to
 *   zodResolver — the resolver identity changes only when the balance does.
 * - The recipient handle is NOT format-validated here (behavior-preserving: the exact-match
 *   lookup IS the validator, ADR-0014); the schema only requires a non-empty normalized tag.
 * - The PIN never enters RHF (plan D5): the withdraw PIN step machine is untouched.
 */

/**
 * ONE per-transaction cap for every money move, deposits included — the contract's bound, not a
 * product choice of this file's own.
 *
 * `docs/api/openapiv1.json` publishes `maximum: 100000.00` on all six money request schemas, from
 * `ValidationRules.TransactionMaxAmount` through `[MoneyRange]`, and the generated
 * `api/generated/apiSchemas.ts` carries it as `.max(100000)`. Until 2026-09-03 the deposit form said
 * 1,000,000 under a comment calling the higher inflow cap deliberate; no decision record ever did,
 * the figure was legacy copy carried from the original UI, and the backend suite pins 1,000,000 as
 * REFUSED. Measured on the running API that day: 500,000 → 400 `{"Amount":["Amount must be between
 * 0.01 EUR and 100000.00 EUR"]}`, 100,000 → 201 (ADR-0046).
 *
 * A literal, on purpose — importing the generated request schema into the forms would couple them
 * to Zod's getter API for a number that changes once a year at most. What keeps it honest is the
 * tripwire in `moneySchemas.test.ts`: every generated request schema's `amount` bound must equal
 * this constant, so a server change that regenerates the contract turns this file red.
 */
export const MONEY_MAX = 100_000;

/**
 * The bound as the copy states it — whole euro, no decimals — so the sentence and the number cannot
 * drift apart the way "€1,000,000." and a 100,000 contract did.
 */
export function describeMoneyBound(amount: number): string {
  return new Intl.NumberFormat('en-IE', {
    style: 'currency',
    currency: 'EUR',
    maximumFractionDigits: 0,
  }).format(amount);
}

/**
 * Sanitize raw amount text exactly like the legacy handlers: strip non-numerics, collapse to a
 * single decimal point, clamp to 2 decimals (EUR minor units, ISO 4217 — the backend rejects a
 * finer scale as VALIDATION_ERROR, so it stops at the source). The 2-decimal clamp now applies
 * to deposit too (it previously lacked it — normalization, noted in the PR).
 */
export function sanitizeAmountInput(value: string): string {
  const digitsAndDots = value.replace(/[^0-9.]/g, '');
  const firstDot = digitsAndDots.indexOf('.');
  const singleDot =
    firstDot === -1
      ? digitsAndDots
      : digitsAndDots.slice(0, firstDot + 1) + digitsAndDots.slice(firstDot + 1).replace(/\./g, '');
  const dot = singleDot.indexOf('.');
  return dot === -1 ? singleDot : singleDot.slice(0, dot + 3);
}

/** Parse a sanitized amount string to a number — 0 when empty/unparsable (legacy behavior). */
export function parseAmountInput(value: string): number {
  return parseFloat(value) || 0;
}

/** Normalize a recipient handle the way the verify flow does: trim + strip one leading @. */
export function normalizeAzureTag(value: string): string {
  return value.trim().replace(/^@/, '');
}

/**
 * What the FIELD says about a value that is too large. All three outflow schemas built this string
 * inline, identically — which is how one condition ends up phrased two ways the moment somebody
 * edits one of them.
 */
export function exceedsBalanceMessage(availableBalance: number): string {
  return `Exceeds available balance of ${formatCurrency(availableBalance)}.`;
}

/**
 * What the BANNER says when an operation was refused for the same reason.
 *
 * Deliberately a different sentence from {@link exceedsBalanceMessage}, because it answers a
 * different question. The field hint explains a value the user can see and edit; this one explains
 * that an action they just took did not happen, and names the balance as it is NOW — which, when
 * the funds gate fires, is a number they have not seen yet.
 */
export function insufficientFundsMessage(availableBalance: number): string {
  return `Insufficient balance for this operation — ${formatCurrency(availableBalance)} available.`;
}

interface AmountBoundsMessages {
  min: string;
  max: string;
  balance?: string;
}

/** String-in → number-out amount schema: sanitize-parse then the shared #33 bounds. */
function amountField(opts: { max: number; balance?: number; messages: AmountBoundsMessages }) {
  return z
    .string()
    .transform(parseAmountInput)
    .pipe(
      makeAmountSchema({
        min: MIN_MONEY_AMOUNT,
        max: opts.max,
        balance: opts.balance,
        messages: opts.messages,
      }),
    );
}

/** Optional free-text description: trimmed, legacy 100-char cap, empty → undefined. */
const descriptionField = z
  .string()
  .trim()
  .max(100)
  .optional()
  .transform((value) => (value ? value : undefined));

// ============================================
// PER-FORM SCHEMAS (legacy copy preserved, except the deposit bound — see MONEY_MAX)
// ============================================

export function depositFormSchema() {
  return z.object({
    accountId: z.string().min(1),
    amount: amountField({
      max: MONEY_MAX,
      messages: {
        min: 'Minimum deposit is €0.01.',
        max: `Maximum deposit is ${describeMoneyBound(MONEY_MAX)}.`,
      },
    }),
    description: descriptionField,
  });
}

export function withdrawFormSchema(availableBalance: number) {
  return z.object({
    accountId: z.string().min(1),
    amount: amountField({
      max: MONEY_MAX,
      balance: availableBalance,
      messages: {
        min: 'Minimum withdrawal is €0.01.',
        max: `Maximum withdrawal is ${describeMoneyBound(MONEY_MAX)}.`,
        balance: exceedsBalanceMessage(availableBalance),
      },
    }),
    description: descriptionField,
  });
}

export function transferFormSchema(availableBalance: number) {
  return z.object({
    fromAccountId: z.string().min(1),
    // The tag's truth comes from the exact-match lookup (ADR-0014) — the schema only
    // requires a non-empty normalized handle; the verified-recipient gate lives outside RHF.
    recipientTag: z.string().transform(normalizeAzureTag).pipe(z.string().min(1)),
    amount: amountField({
      max: MONEY_MAX,
      balance: availableBalance,
      messages: {
        min: 'Minimum transfer is €0.01.',
        max: `Maximum transfer is ${describeMoneyBound(MONEY_MAX)}.`,
        balance: exceedsBalanceMessage(availableBalance),
      },
    }),
  });
}

export function internalTransferFormSchema(availableBalance: number) {
  return z
    .object({
      fromAccountId: z.string().min(1),
      toAccountId: z.string().min(1),
      amount: amountField({
        max: MONEY_MAX,
        balance: availableBalance,
        messages: {
          min: 'Minimum transfer is €0.01.',
          max: `Maximum transfer is ${describeMoneyBound(MONEY_MAX)}.`,
          balance: exceedsBalanceMessage(availableBalance),
        },
      }),
    })
    .superRefine((value, ctx) => {
      // The one genuinely local cross-field rule (plan P5.1): source ≠ destination.
      if (value.fromAccountId && value.toAccountId && value.fromAccountId === value.toAccountId) {
        ctx.addIssue({
          code: 'custom',
          path: ['toAccountId'],
          message: 'Choose two different accounts.',
        });
      }
    });
}

// RHF needs BOTH sides of the transforming schemas: the raw INPUT shape drives the form
// state (amount as the sanitized string), the parsed OUTPUT shape reaches onSubmit
// (amount as a bounded number) — `useForm<Input, unknown, Output>`.
export type DepositFormValues = z.input<ReturnType<typeof depositFormSchema>>;
export type DepositFormOutput = z.output<ReturnType<typeof depositFormSchema>>;
export type WithdrawFormValues = z.input<ReturnType<typeof withdrawFormSchema>>;
export type WithdrawFormOutput = z.output<ReturnType<typeof withdrawFormSchema>>;
export type TransferFormValues = z.input<ReturnType<typeof transferFormSchema>>;
export type TransferFormOutput = z.output<ReturnType<typeof transferFormSchema>>;
export type InternalTransferFormValues = z.input<ReturnType<typeof internalTransferFormSchema>>;
export type InternalTransferFormOutput = z.output<ReturnType<typeof internalTransferFormSchema>>;
