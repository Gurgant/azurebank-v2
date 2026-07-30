import { beforeEach, describe, expect, it } from 'vitest';
import {
  getSessionDeadline,
  isAbsoluteDeadline,
  learnSessionPolicy,
  markServerActivity,
  resetServerActivity,
  syncFromProbe,
} from './sessionActivity';

/**
 * The module is deliberately tested directly, not only through the dialog.
 *
 * `syncFromProbe`'s handling of the absolute cap has two halves and the dialog can only exercise
 * one. That it LEARNS a later cap is visible there (a re-auth whose refetch failed must not end in a
 * sign-out). That it never adopts an EARLIER one is not reachable from the UI at all: no legitimate
 * server sequence moves a live session's cap backwards, and the case that can — a slow probe's
 * response landing after a newer one — is a race a component test cannot stage. Left unpinned, the
 * monotonic guard was a line no test could tell the difference about, which is how a "simplification"
 * removes it.
 */

const T0 = Date.parse('2026-07-30T12:00:00.000Z');
const iso = (offsetMs: number) => new Date(T0 + offsetMs).toISOString();

const MINUTE = 60_000;

/**
 * A session whose ABSOLUTE cap binds: 150s away, with inactivity half an hour out.
 *
 * `markServerActivity()` is not decoration. `getSessionDeadline()` returns null until this tab has
 * seen a response, by design — an unknown deadline must never be guessed in either direction — so
 * learning the policy alone leaves every reader null. In the app the middleware marks activity on
 * the same response the policy is learned from.
 */
function policyWithNearAbsoluteCap() {
  learnSessionPolicy({
    lastActivity: iso(0),
    expiresAt: iso(150_000),
    inactivityExpiresAt: iso(30 * MINUTE),
  });
  markServerActivity();
}

describe('sessionActivity — the absolute cap', () => {
  beforeEach(() => {
    resetServerActivity();
  });

  it('binds when it falls before the inactivity deadline', () => {
    policyWithNearAbsoluteCap();
    expect(isAbsoluteDeadline()).toBe(true);
  });

  it('moves FORWARD when a probe reports a later cap — a replaced session', () => {
    policyWithNearAbsoluteCap();
    const before = getSessionDeadline()!;

    // What re-authentication produces: same user, new session, new window.
    syncFromProbe({
      serverTime: iso(10_000),
      inactivityExpiresAt: iso(10_000 + 30 * MINUTE),
      absoluteExpiresAt: iso(10_000 + 150_000),
    });

    expect(getSessionDeadline()!).toBeGreaterThan(before);
  });

  it('never adopts an EARLIER cap, whatever a probe says', () => {
    policyWithNearAbsoluteCap();
    const before = getSessionDeadline()!;

    // A stale or out-of-order response, or a skewed server clock. Adopting this would sign the user
    // out early — the one failure this module exists to make impossible, since it fails CLOSED and a
    // deadline that arrives early cannot be argued with afterwards.
    syncFromProbe({
      serverTime: iso(10_000),
      inactivityExpiresAt: iso(10_000 + 30 * MINUTE),
      absoluteExpiresAt: iso(11_000),
    });

    expect(getSessionDeadline()!).toBeGreaterThanOrEqual(before);
  });

  it('ignores a probe with no cap at all rather than treating it as zero', () => {
    // A BFF too old to send the field, or the unauthenticated shape where every timestamp is null.
    policyWithNearAbsoluteCap();
    const before = getSessionDeadline()!;

    syncFromProbe({
      serverTime: iso(10_000),
      inactivityExpiresAt: iso(10_000 + 30 * MINUTE),
      absoluteExpiresAt: null,
    });

    expect(getSessionDeadline()!).toBeGreaterThanOrEqual(before);
  });
});
