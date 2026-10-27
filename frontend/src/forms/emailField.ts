import { z } from 'zod';

/** An empty email field, on both entry pages. */
export const EMAIL_ADDRESS_EMPTY = 'Enter your email address.';

/** An email field that holds something that is not an email address, on both entry pages. */
export const EMAIL_ADDRESS_MALFORMED =
  'Enter an email address in the correct format, like name@example.com.';

/**
 * The email field both entry pages use: sign-in and registration.
 * An empty field fails the first check; anything that is not an email address fails the second.
 */
export const emailField = z
  .string()
  .min(1, EMAIL_ADDRESS_EMPTY)
  .pipe(z.email(EMAIL_ADDRESS_MALFORMED));
