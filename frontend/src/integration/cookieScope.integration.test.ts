import { createServer } from 'node:http';
import { describe, expect, it } from 'vitest';
import { currentCookieHeader, seedCookieJar } from './setup';

/**
 * A regression guard on the harness itself, not on the product.
 *
 * `setup.ts` replaces `globalThis.fetch` PROCESS-WIDE for the duration of a file. An earlier version
 * attached the jar's cookies to every outgoing request and stored `Set-Cookie` from every response,
 * with no origin check. Every request this suite makes happens to go to the BFF, so nothing leaked
 * — but "nothing leaks today" is a property of the current test list, not of the shim, and the next
 * test (or a dependency fetching a schema, a font, a telemetry beacon) would have changed that
 * silently.
 *
 * Measured with the check removed: the foreign server received the real
 * `.AzureBank.Session=…` value, and its own `evil=1` was stored and would have been sent to the BFF
 * on the following call. Both directions, so both are asserted below.
 *
 * No login here on purpose. What is asserted is true of any cookie, and a real session would spend
 * one of the ten auth requests the BFF allows per minute to prove nothing extra.
 */

describe('integration harness: the cookie jar is scoped to the BFF', () => {
  it('sends nothing to a foreign origin and accepts nothing from one', async () => {
    seedCookieJar('.AzureBank.Session', 'seeded-not-a-real-session');
    expect(currentCookieHeader()).toContain('.AzureBank.Session');

    let seenCookie: string | undefined = 'NEVER_CALLED';
    const server = createServer((request, response) => {
      seenCookie = request.headers.cookie;
      response.setHeader('Set-Cookie', 'evil=1; path=/');
      response.end('{}');
    });
    /*
      `listen` reports failure by EMITTING 'error', not by throwing, so a promise that only wires
      `resolve` has no rejection path: an EADDRINUSE would hang this test until the suite timeout
      and surface as an unhandled error rather than as "the port was taken".
    */
    await new Promise<void>((resolve, reject) => {
      server.once('error', reject);
      server.listen(0, '127.0.0.1', resolve);
    });
    const { port } = server.address() as { port: number };

    try {
      await fetch(`http://127.0.0.1:${port}/whatever`);
    } finally {
      await new Promise<void>((resolve) => server.close(() => resolve()));
    }

    // Outbound: the session never reaches an origin that did not issue it.
    expect(seenCookie).toBeUndefined();
    // Inbound: a foreign Set-Cookie never enters the jar, so it can never be replayed at the BFF.
    expect(currentCookieHeader()).not.toContain('evil');
  });
});
