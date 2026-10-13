import { describe, expect, it } from 'vitest';
import {
  formatCurrency,
  formatDateHeading,
  formatDateTime,
  formatLockHorizon,
  formatTime,
  isIncomeType,
  maskAccountNumber,
} from './format';

/**
 * The display formatters on their own, so that the page tests may use them as the oracle for
 * "what the page renders for this instant" without the two being able to be wrong together.
 *
 * The date ones format the VIEWER'S calendar day, and that is the property worth pinning: it is
 * why the history and transaction-detail tests build their expectations with these functions
 * rather than with `toLocaleDateString(..., { timeZone: 'UTC' })` — a UTC expectation names a
 * different day than the page for part of every day, west or east of Greenwich. CI runs this suite
 * in UTC, America/Los_Angeles and Asia/Tokyo, which is what makes the second test below a guard
 * rather than a tautology.
 */
describe('date formatters', () => {
  it('formatDateHeading writes the long month, the day and the year', () => {
    // Noon UTC: the same calendar day in every zone between UTC-11 and UTC+11, so the literal is
    // stable wherever this runs.
    expect(formatDateHeading('2026-03-15T12:00:00Z')).toBe('March 15, 2026');
  });

  it("formatDateHeading follows the viewer's calendar day, not UTC's", () => {
    // 23:30 UTC is still 15 March in Los Angeles and already 16 March in Tokyo. The expectation is
    // derived from the same instant through the platform's own local calendar, so this passes in
    // every zone the suite runs in and would fail in Tokyo if the formatter ever switched to UTC.
    const instant = '2026-03-15T23:30:00Z';
    const localDay = new Date(instant).getDate();
    expect(formatDateHeading(instant)).toBe(`March ${localDay}, 2026`);
  });

  it("formatTime is the viewer's 12-hour clock with the meridiem", () => {
    // The exact string, derived from the same instant through the platform's local clock: noon UTC
    // is 5:00 AM in Los Angeles, 9:00 PM in Tokyo and 12:00 PM in UTC, and a shape-only match would
    // have accepted any of them anywhere.
    const instant = '2026-03-15T12:00:00Z';
    const date = new Date(instant);
    const hour24 = date.getHours();
    const expected = `${hour24 % 12 || 12}:${String(date.getMinutes()).padStart(2, '0')} ${
      hour24 >= 12 ? 'PM' : 'AM'
    }`;
    expect(formatTime(instant)).toBe(expected);
  });

  it('formatDateTime is the heading and the time, joined by a middle dot', () => {
    const instant = '2026-03-15T12:00:00Z';
    expect(formatDateTime(instant)).toBe(`${formatDateHeading(instant)} · ${formatTime(instant)}`);
  });
});

describe('formatCurrency', () => {
  it('formats euro with two decimals and an Irish-English separator convention', () => {
    expect(formatCurrency(1250.5)).toBe('€1,250.50');
    expect(formatCurrency(0)).toBe('€0.00');
  });
});

describe('isIncomeType', () => {
  it('reads the direction of money from the type: in for a deposit and a transfer received', () => {
    expect({
      Deposit: isIncomeType('Deposit'),
      TransferIn: isIncomeType('TransferIn'),
      Withdrawal: isIncomeType('Withdrawal'),
      TransferOut: isIncomeType('TransferOut'),
    }).toEqual({ Deposit: true, TransferIn: true, Withdrawal: false, TransferOut: false });
  });
});

/*
  How long a lock lasts, in the words a refusal uses ("Try again in about 15 minutes."). A part
  of a minute counts as a whole one: "1 minute" for 90 seconds would promise an unlock that is
  not there yet.
*/
describe('formatLockHorizon', () => {
  it('rounds up to whole minutes from one minute on', () => {
    expect([60, 61, 90, 120, 900].map(formatLockHorizon)).toEqual([
      '1 minute',
      '2 minutes',
      '2 minutes',
      '2 minutes',
      '15 minutes',
    ]);
  });

  it('counts in seconds under a minute, and says "1 second" for one', () => {
    expect([59, 45, 2, 1].map(formatLockHorizon)).toEqual([
      '59 seconds',
      '45 seconds',
      '2 seconds',
      '1 second',
    ]);
  });
});

/*
  The second line of defence for an account number. The API masks it first, so what arrives is
  already `AB-****-****-90`; this turns the middle into bullets, and would do the same to a full
  number if one ever arrived. What it must never do is hand back, readable, a string it does not
  recognise.
*/
describe('maskAccountNumber', () => {
  it('keeps the prefix and the last group, and bullets the groups between', () => {
    expect(maskAccountNumber('AB-****-****-90')).toBe('AB-••••-••••-90');
    // A full number, were one to reach the client: its middle does not reach the screen.
    expect(maskAccountNumber('AB-1234-5678-90')).toBe('AB-••••-••••-90');
  });

  it('shows only the last two characters of a number in a form it does not know', () => {
    expect(maskAccountNumber('1234567890')).toBe('••••••••90');
    // Two groups is not the scheme either: nothing of the first group is kept.
    expect(maskAccountNumber('AB-1234567890')).toBe('•••••••••••90');
  });

  it('shows nothing of a string too short to mask', () => {
    expect(['', '9', '90'].map(maskAccountNumber)).toEqual(['••', '••', '••']);
  });
});
