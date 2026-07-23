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

/** Deposit keeps the higher inflow cap; every outflow form caps at €100k. */
export const DEPOSIT_MAX = 1_000_000;
export const OUTFLOW_MAX = 100_000;

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
// PER-FORM SCHEMAS (exact legacy copy preserved)
// ============================================

export function depositFormSchema() {
  return z.object({
    accountId: z.string().min(1),
    amount: amountField({
      max: DEPOSIT_MAX,
      messages: {
        min: 'Minimum deposit is €0.01.',
        max: 'Maximum deposit is €1,000,000.',
      },
    }),
    description: descriptionField,
  });
}

export function withdrawFormSchema(availableBalance: number) {
  return z.object({
    accountId: z.string().min(1),
    amount: amountField({
      max: OUTFLOW_MAX,
      balance: availableBalance,
      messages: {
        min: 'Minimum withdrawal is €0.01.',
        max: 'Maximum withdrawal is €100,000.',
        balance: `Exceeds available balance of ${formatCurrency(availableBalance)}.`,
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
      max: OUTFLOW_MAX,
      balance: availableBalance,
      messages: {
        min: 'Minimum transfer is €0.01.',
        max: 'Maximum transfer is €100,000.',
        balance: `Exceeds available balance of ${formatCurrency(availableBalance)}.`,
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
        max: OUTFLOW_MAX,
        balance: availableBalance,
        messages: {
          min: 'Minimum transfer is €0.01.',
          max: 'Maximum transfer is €100,000.',
          balance: `Exceeds available balance of ${formatCurrency(availableBalance)}.`,
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

export type DepositFormValues = z.input<ReturnType<typeof depositFormSchema>>;
export type WithdrawFormValues = z.input<ReturnType<typeof withdrawFormSchema>>;
export type TransferFormValues = z.input<ReturnType<typeof transferFormSchema>>;
export type InternalTransferFormValues = z.input<ReturnType<typeof internalTransferFormSchema>>;
