import { describe, expect, it } from 'vitest';
import {
  DepositRequest,
  InternalTransferAuthorizationRequest,
  InternalTransferRequest,
  TransferAuthorizationRequest,
  TransferRequest,
  WithdrawRequest,
} from '../api/generated/apiSchemas';
import { MIN_MONEY_AMOUNT } from '../utils/amountSchema';
import {
  MONEY_MAX,
  depositFormSchema,
  describeMoneyBound,
  internalTransferFormSchema,
  normalizeAzureTag,
  parseAmountInput,
  sanitizeAmountInput,
  transferFormSchema,
  withdrawFormSchema,
} from './moneySchemas';

/**
 * THE TRIPWIRE (ADR-0046). The forms hold the money bound as a literal; the contract holds it as
 * `maximum: 100000.00` on every money request schema, regenerated into `api/generated/apiSchemas.ts`
 * as `.max(100000)`. This is the only thing that turns the forms red when the server's constant
 * changes and the contract is regenerated — the way the deposit form promised 1,000,000 for two
 * months against a contract that said 100,000, with three green tests asserting its own literal.
 *
 * In the tripwire suite on purpose (package.json `test:tripwire`), so it runs under `npm test` and
 * under both zone legs.
 */
describe('the money bound is the contract’s, on every form', () => {
  const generated = {
    DepositRequest,
    WithdrawRequest,
    TransferRequest,
    InternalTransferRequest,
    TransferAuthorizationRequest,
    InternalTransferAuthorizationRequest,
  };

  it.each(Object.entries(generated))(
    '%s.amount publishes exactly the bounds the forms enforce',
    (_name, schema) => {
      const amount = schema.shape.amount;
      expect(amount.maxValue).toBe(MONEY_MAX);
      expect(amount.minValue).toBe(MIN_MONEY_AMOUNT);
    },
  );

  it('states the bound in whole euro, so the copy follows the number', () => {
    expect(describeMoneyBound(MONEY_MAX)).toBe('€100,000');
    const over = depositFormSchema().safeParse({ accountId: 'a1', amount: String(MONEY_MAX + 1) });
    expect(over.success).toBe(false);
    if (!over.success) {
      expect(over.error.issues.map((i) => i.message)).toContain(
        `Maximum deposit is ${describeMoneyBound(MONEY_MAX)}.`,
      );
    }
    expect(
      depositFormSchema().safeParse({ accountId: 'a1', amount: String(MONEY_MAX) }).success,
    ).toBe(true);
  });
});

describe('sanitizeAmountInput', () => {
  it('strips non-numerics and collapses to a single decimal point', () => {
    expect(sanitizeAmountInput('1.2.3')).toBe('1.23');
    expect(sanitizeAmountInput('€1,250abc')).toBe('1250');
  });

  it('clamps to 2 decimals (EUR minor units)', () => {
    expect(sanitizeAmountInput('1.239')).toBe('1.23');
    expect(sanitizeAmountInput('0.999')).toBe('0.99');
  });

  it('parses empty/unparsable to 0 like the legacy handlers', () => {
    expect(parseAmountInput('')).toBe(0);
    expect(parseAmountInput('.')).toBe(0);
  });
});

describe('normalizeAzureTag', () => {
  it('trims and strips one leading @', () => {
    expect(normalizeAzureTag(' @john_d ')).toBe('john_d');
    expect(normalizeAzureTag('jane')).toBe('jane');
  });
});

describe('money form schemas', () => {
  it('deposit: accepts a valid form and coerces the amount string', () => {
    const result = depositFormSchema().safeParse({
      accountId: 'a1',
      amount: '250.50',
      description: '  salary  ',
    });
    expect(result.success).toBe(true);
    if (result.success) {
      expect(result.data.amount).toBe(250.5);
      expect(result.data.description).toBe('salary');
    }
  });

  it('deposit: empty description becomes undefined (legacy body shape)', () => {
    const result = depositFormSchema().safeParse({
      accountId: 'a1',
      amount: '50',
      description: '',
    });
    expect(result.success).toBe(true);
    if (result.success) {
      expect(result.data.description).toBeUndefined();
    }
  });

  it('deposit: refuses one cent above the contract bound with the deposit copy', () => {
    // 100,001 sits inside the 1,000,000 the form used to allow and above the 100,000 the contract
    // publishes — red on the old constant, green on the new one (ADR-0046).
    const over = depositFormSchema().safeParse({ accountId: 'a1', amount: '100001' });
    expect(over.success).toBe(false);
    if (!over.success) {
      expect(over.error.issues.some((i) => i.message === 'Maximum deposit is €100,000.')).toBe(
        true,
      );
    }
    const under = depositFormSchema().safeParse({ accountId: 'a1', amount: '' });
    expect(under.success).toBe(false);
    if (!under.success) {
      expect(under.error.issues.some((i) => i.message === 'Minimum deposit is €0.01.')).toBe(true);
    }
  });

  it('withdraw: enforces the balance cap with the formatted legacy message', () => {
    const result = withdrawFormSchema(830).safeParse({ accountId: 'a1', amount: '900' });
    expect(result.success).toBe(false);
    if (!result.success) {
      expect(
        result.error.issues.some((i) => i.message === 'Exceeds available balance of €830.00.'),
      ).toBe(true);
    }
  });

  it('transfer: normalizes the recipient tag and validates bounds', () => {
    const result = transferFormSchema(1000).safeParse({
      fromAccountId: 'a1',
      recipientTag: ' @friend ',
      amount: '50',
    });
    expect(result.success).toBe(true);
    if (result.success) {
      expect(result.data.recipientTag).toBe('friend');
      expect(result.data.amount).toBe(50);
    }
  });

  it('internal: rejects the same source and destination on the toAccountId path', () => {
    const result = internalTransferFormSchema(1000).safeParse({
      fromAccountId: 'a1',
      toAccountId: 'a1',
      amount: '50',
    });
    expect(result.success).toBe(false);
    if (!result.success) {
      const issue = result.error.issues.find((i) => i.message === 'Choose two different accounts.');
      expect(issue?.path).toEqual(['toAccountId']);
    }
  });

  it('internal: accepts two distinct accounts', () => {
    const result = internalTransferFormSchema(1000).safeParse({
      fromAccountId: 'a1',
      toAccountId: 'a2',
      amount: '50',
    });
    expect(result.success).toBe(true);
  });
});
