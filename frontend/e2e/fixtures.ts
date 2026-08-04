/**
 * The seeded dev user, and the constraints that shape every spec in this directory.
 *
 * Fixtures are not contract — only shapes, codes and rendered behaviour are. These values exist
 * so a spec never hardcodes a credential inline, and so the constraints below sit next to the
 * thing they constrain.
 */
export const USER = {
  email: 'admin@azurebank.dev',
  password: 'Test123!',
  /**
   * NEVER send a wrong one. `ValidationRules.MaxPinAttempts` is 3 and `PinLockoutMinutes` is 15,
   * enforced API-side, and this is the only account the dev database seeds — three bad attempts
   * lock the whole suite out for a quarter of an hour.
   */
  pin: '123456',
} as const;

/**
 * The BFF's own origin. Specs drive vite on 5173 and let it proxy, exactly as a developer's
 * browser does; this is only for preflighting that the backend is actually up.
 */
export const BFF_ORIGIN = 'http://localhost:5000';

/**
 * Dev session windows, from `AzureBank.Bff/appsettings.Development.json`:
 * inactivity 10 minutes, absolute 20. A saved `storageState` is therefore perishable — it is
 * regenerated on every run and never committed, and a suite that ran longer than the absolute
 * window would start seeing its own session expire mid-flight.
 */
export const SESSION_ABSOLUTE_TIMEOUT_MINUTES = 20;
