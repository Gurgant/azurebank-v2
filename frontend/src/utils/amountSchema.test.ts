import { describe, expect, it } from 'vitest';
import { amountIsValid, makeAmountSchema } from './amountSchema';

describe('amountSchema', () => {
  it('accepts an in-bounds amount', () => {
    expect(amountIsValid(50, { min: 0.01, max: 100_000, balance: 1000 })).toBe(true);
  });

  it('rejects below the minimum', () => {
    expect(amountIsValid(0, { min: 0.01, max: 100_000 })).toBe(false);
    expect(amountIsValid(0.001, { min: 0.01, max: 100_000 })).toBe(false);
  });

  it('rejects above the per-form max', () => {
    expect(amountIsValid(100_001, { min: 0.01, max: 100_000 })).toBe(false);
  });

  it('rejects above the available balance when one is given', () => {
    expect(amountIsValid(1500, { min: 0.01, max: 100_000, balance: 1000 })).toBe(false);
    // No balance provided (deposit) → the balance rule does not apply.
    expect(amountIsValid(1500, { min: 0.01, max: 1_000_000 })).toBe(true);
  });

  it('rejects non-finite input', () => {
    expect(amountIsValid(Number.NaN, { max: 100_000 })).toBe(false);
    expect(amountIsValid(Infinity, { max: 100_000 })).toBe(false);
  });

  it('surfaces the balance-exceeded message when the amount is over balance', () => {
    // 1500 is within max (100_000) but over the balance (1000) — the balance issue must be present
    // (order-independent: Zod collects every failing refinement).
    const result = makeAmountSchema({ max: 100_000, balance: 1000 }).safeParse(1500);
    expect(result.success).toBe(false);
    if (!result.success) {
      expect(result.error.issues.some((i) => /available balance/.test(i.message))).toBe(true);
    }
  });
});
