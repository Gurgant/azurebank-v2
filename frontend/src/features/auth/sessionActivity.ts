/**
 * Client mirror of the BFF's LastActivity (D14): every server response marks activity;
 * the SessionExpiryWarning compares against the inactivity window. A module-level
 * timestamp, not Redux state — it changes on every request and nothing renders from it.
 */

/** Mirror of BFF appsettings Session:InactivityTimeoutMinutes (backlog: expose inactivityExpiresAt on B3/B5). */
export const INACTIVITY_TIMEOUT_MINUTES = 30;

/** How long before expiry the "Stay signed in" warning appears. */
export const WARNING_LEAD_MINUTES = 2;

let lastServerActivity: number | null = null;

export function markServerActivity(): void {
  lastServerActivity = Date.now();
}

export function getLastServerActivity(): number | null {
  return lastServerActivity;
}

export function resetServerActivity(): void {
  lastServerActivity = null;
}
