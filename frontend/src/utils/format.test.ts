import { describe, expect, it } from 'vitest';
import { formatCurrency, formatDateHeading, formatDateTime, formatTime } from './format';

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

  it('formatTime is a 12-hour clock with the meridiem', () => {
    expect(formatTime('2026-03-15T12:00:00Z')).toMatch(/^\d{1,2}:\d{2} (AM|PM)$/);
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
