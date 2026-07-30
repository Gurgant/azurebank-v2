/**
 * Client mirror of the BFF's session clock (D14).
 *
 * The mirror used to hardcode the inactivity window as `INACTIVITY_TIMEOUT_MINUTES = 30`, described
 * in its own comment as a mirror of BFF configuration. It was wrong: the BFF ships 30/60 in base
 * configuration and 10/20 in Development, so on a developer machine the warning was timed for 28
 * minutes of idle against a session that died at 10 — it could only ever appear after the fact. And
 * the absolute cap was ignored entirely, so a continuously active user was cut off with no warning
 * at all.
 *
 * The window is now **declared by the server** and learned from `/bff/auth/me`. Nothing here is a
 * guess about policy; the only local number left is how early to warn, which is a UI decision.
 *
 * **No polling.** `/bff/auth/me` is itself activity — fetching it slides the very deadline it
 * reports — so a heartbeat against it would make the session immortal. The deadline is instead
 * computed from what the server already said plus the timestamp of the last response this tab saw.
 * `session-status` is the one probe the BFF excludes from activity (ADR-0018), and it is used only
 * to CONFIRM expiry at the moment the countdown reaches zero, never on a timer.
 */

/** How early the warning appears. A presentation choice, not server policy. */
export const WARNING_LEAD_MS = 2 * 60_000;

/**
 * Durations, never timestamps.
 *
 * A server *timestamp* compared against `Date.now()` is only as good as the reader's clock, and a
 * wrong clock here either signs someone out early or never. A *duration* between two server values
 * is immune to that, so both windows are stored as lengths and anchored to the local clock at the
 * moment the response arrived.
 */
interface SessionPolicy {
  /** Inactivity window: how long a session survives with no requests. */
  inactivityWindowMs: number;
  /** Local-clock instant at which the absolute cap falls, regardless of activity. */
  absoluteDeadline: number;
}

let lastServerActivity: number | null = null;
let policy: SessionPolicy | null = null;

export function markServerActivity(): void {
  lastServerActivity = Date.now();
}

export function getLastServerActivity(): number | null {
  return lastServerActivity;
}

/**
 * Learn the session policy from a `/bff/auth/me` response.
 *
 * Both windows are derived by subtracting one server timestamp from another, which is why they
 * survive a skewed client clock. `lastActivity` doubles as "the server's clock at the moment it
 * answered", so the absolute cap converts to a local instant with the same trick.
 *
 * Silently ignores a response from a BFF that predates the field, leaving the deadline unknown —
 * which the warning treats as "say nothing" rather than as "expire now".
 */
export function learnSessionPolicy(session: {
  lastActivity: string;
  expiresAt: string;
  inactivityExpiresAt?: string;
}): void {
  const serverNow = Date.parse(session.lastActivity);
  const absolute = Date.parse(session.expiresAt);
  if (!Number.isFinite(serverNow) || !Number.isFinite(absolute)) return;

  const inactivityExpires = session.inactivityExpiresAt
    ? Date.parse(session.inactivityExpiresAt)
    : Number.NaN;
  if (!Number.isFinite(inactivityExpires)) return;

  policy = {
    inactivityWindowMs: inactivityExpires - serverNow,
    absoluteDeadline: Date.now() + (absolute - serverNow),
  };
}

/**
 * Correct the clock from a `session-status` probe, whose remaining time is real.
 *
 * Used when the tab comes back from the background and when the countdown hits zero — the two
 * moments where this tab's idea of the deadline is least trustworthy, because another tab may have
 * kept the session warm in the meantime.
 */
export function syncFromProbe(status: {
  serverTime?: string | null;
  inactivityExpiresAt?: string | null;
  absoluteExpiresAt?: string | null;
}): void {
  const inactivityExpires = status.inactivityExpiresAt
    ? Date.parse(status.inactivityExpiresAt)
    : Number.NaN;
  const serverNow = status.serverTime ? Date.parse(status.serverTime) : Number.NaN;
  if (!Number.isFinite(inactivityExpires) || !Number.isFinite(serverNow) || policy === null) return;

  // The remainder is computed from TWO SERVER VALUES and only then anchored to the local clock.
  // The first version of this subtracted `Date.now()` from the server's deadline, which is the
  // very skew the rest of the module is built to avoid — a fast client would have warned early and
  // a slow one late, with nothing to reveal which. `serverTime` exists so the arithmetic can stay
  // entirely on the server's side of the wire.
  const remainingMs = inactivityExpires - serverNow;
  lastServerActivity = Date.now() - (policy.inactivityWindowMs - remainingMs);

  // The absolute cap IS re-read, but only ever FORWARD.
  //
  // It used to be skipped entirely, on the grounds that the cap is fixed at session creation and
  // never moves. That was true until re-authentication (U6.7, ADR-0026), which replaces the session
  // and therefore replaces the cap. Without this, one specific sequence signed a user out moments
  // after they had successfully re-authenticated: the re-auth lands, the follow-up `/me` that would
  // teach the client the new window fails, the countdown reaches the OLD cap, and the confirming
  // probe — which can see the new one — refused to look. The same gap covers a second tab that
  // re-authenticated while this one was idle.
  //
  // Monotonic on purpose. A cap that can only move later can never cause an early sign-out, whatever
  // a stale response or a skewed clock says; the worst it can do is warn late, which the zero
  // crossing then re-confirms. And the arithmetic stays between two SERVER values, exactly like the
  // line above, so the local clock is never a term in it.
  const absoluteExpires = status.absoluteExpiresAt
    ? Date.parse(status.absoluteExpiresAt)
    : Number.NaN;
  if (Number.isFinite(absoluteExpires)) {
    const candidate = Date.now() + (absoluteExpires - serverNow);
    if (candidate > policy.absoluteDeadline) policy.absoluteDeadline = candidate;
  }
}

/**
 * The effective deadline: the EARLIER of the two rules, or null when it is not yet known.
 *
 * Null is returned before the first `/me` response and against a BFF too old to declare the window.
 * Every caller must treat null as "do not warn and do not sign out" — guessing in either direction
 * is what produced the defect this replaces.
 */
export function getSessionDeadline(): number | null {
  if (lastServerActivity === null || policy === null) return null;
  return Math.min(lastServerActivity + policy.inactivityWindowMs, policy.absoluteDeadline);
}

/** True when the effective deadline is the absolute cap — the case no warning covered before. */
export function isAbsoluteDeadline(): boolean {
  if (lastServerActivity === null || policy === null) return false;
  return policy.absoluteDeadline <= lastServerActivity + policy.inactivityWindowMs;
}

export function resetServerActivity(): void {
  lastServerActivity = null;
  policy = null;
}
