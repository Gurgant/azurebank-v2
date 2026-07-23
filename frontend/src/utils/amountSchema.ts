import { z } from 'zod';

/** Smallest transactable amount (matches every money form). */
export const MIN_MONEY_AMOUNT = 0.01;

/** Per-bound override strings so each money form keeps its exact user-facing copy. */
export interface AmountMessages {
  min?: string;
  max?: string;
  balance?: string;
}

/**
 * Zod schema for a money amount, bounds parameterized per form (min / per-form max / optional
 * available balance). The shared validator for withdraw / deposit / transfer /
 * internal-transfer amounts — identical bounds to the previous imperative checks, declarative
 * and in one place. `messages` lets each form keep its exact legacy copy (e.g. "Maximum
 * withdrawal is €100,000.") — defaults preserve the original generic strings.
 */
export function makeAmountSchema(opts: {
  min?: number;
  max: number;
  balance?: number;
  messages?: AmountMessages;
}) {
  const min = opts.min ?? MIN_MONEY_AMOUNT;
  return z
    .number()
    .refine((n) => Number.isFinite(n) && n >= min, {
      error: opts.messages?.min ?? `Amount must be at least ${min}`,
    })
    .refine((n) => n <= opts.max, {
      error: opts.messages?.max ?? `Amount must be at most ${opts.max}`,
    })
    .refine((n) => opts.balance === undefined || n <= opts.balance, {
      error: opts.messages?.balance ?? 'Amount exceeds the available balance',
    });
}

/** Boolean convenience for the forms' `isAmountValid` checks. */
export function amountIsValid(
  amount: number,
  opts: { min?: number; max: number; balance?: number },
): boolean {
  return makeAmountSchema(opts).safeParse(amount).success;
}
