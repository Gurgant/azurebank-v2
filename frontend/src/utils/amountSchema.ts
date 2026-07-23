import { z } from 'zod';

/** Smallest transactable amount (matches every money form). */
export const MIN_MONEY_AMOUNT = 0.01;

/**
 * Zod schema for a money amount, bounds parameterized per form (min / per-form max / optional
 * available balance). The shared safety-net validator for withdraw / deposit / transfer /
 * internal-transfer amounts — identical bounds to the previous imperative checks, now declarative
 * and in one place. (Full RHF+Zod adoption of these forms is a dedicated follow-up PR; this keeps
 * the amount rule shared in the meantime, so the safety net exists regardless.)
 */
export function makeAmountSchema(opts: { min?: number; max: number; balance?: number }) {
  const min = opts.min ?? MIN_MONEY_AMOUNT;
  return z
    .number()
    .refine((n) => Number.isFinite(n) && n >= min, { message: `Amount must be at least ${min}` })
    .refine((n) => n <= opts.max, { message: `Amount must be at most ${opts.max}` })
    .refine((n) => opts.balance === undefined || n <= opts.balance, {
      message: 'Amount exceeds the available balance',
    });
}

/** Boolean convenience for the forms' `isAmountValid` checks. */
export function amountIsValid(
  amount: number,
  opts: { min?: number; max: number; balance?: number },
): boolean {
  return makeAmountSchema(opts).safeParse(amount).success;
}
