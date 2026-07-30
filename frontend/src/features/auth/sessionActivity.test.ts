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

    // What re-authentication produces: same user, new session, a window that is genuinely LONGER
    // measured from the server's own clock. The first draft reported `serverTime + 150_000` — the
    // same 150s remaining as the original policy, i.e. the same window merely re-quoted — so the two
    // deadlines differed only by however many milliseconds passed between two `Date.now()` calls in
    // this file. It passed whenever that was 1ms and failed when it was 0: a flaky test that asserted
    // clock jitter rather than the behaviour. The gap has to come from the FIXTURE, not from timing.
    syncFromProbe({
      serverTime: iso(10_000),
      inactivityExpiresAt: iso(10_000 + 30 * MINUTE),
      absoluteExpiresAt: iso(10_000 + 300_000),
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

    // Exactly `before`, not merely 'no earlier'. The cap BINDS in this fixture, so an untouched
    // policy can only produce the same number — and the looser comparison would also accept a guard
    // that moved the cap FORWARD here, which is the neighbouring bug (a copy-paste that re-derives
    // the cap from the inactivity window would do exactly that).
    expect(getSessionDeadline()!).toBe(before);
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

    // Exactly `before`, not merely 'no earlier'. The cap BINDS in this fixture, so an untouched
    // policy can only produce the same number — and the looser comparison would also accept a guard
    // that moved the cap FORWARD here, which is the neighbouring bug (a copy-paste that re-derives
    // the cap from the inactivity window would do exactly that).
    expect(getSessionDeadline()!).toBe(before);
  });
});
