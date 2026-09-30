import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { READ_BUDGET_MS, REQUEST_TIMEOUT_MS } from './problemBaseQuery';

/**
 * The SPA's clock, held against the numbers it depends on in another language.
 *
 * The BFF answers every request within its renewal wait plus its own wait on the API
 * (ADR-0058: 5 + 55 = 60 s). The SPA aborts later than that, or it would turn a late 503 — which
 * may say `applied: false` — into an unknown; and earlier than the ingress's 240 s cut, or the
 * visitor would read the ingress's page instead of the SPA's sentence. A read's budget holds two
 * of those worst answers: the first attempt and its one retry.
 *
 * The two backend numbers are read from the files that set them, so a change on either side of
 * the wire turns this red rather than quietly breaking the chain. Paths are plain relative:
 * Vitest runs from the frontend root (brandFillUsage.test.ts has the reason).
 */

const BFF_SETTINGS = '../backend/src/AzureBank.Bff/appsettings.json';
const TOKEN_REFRESHER = '../backend/src/AzureBank.Bff/Services/Implementations/TokenRefresher.cs';
const INGRESS_CUT_MS = 240_000;

function backendWaits() {
  // No comments in this file (only `//` inside URLs), so plain JSON.parse reads it.
  const settings = JSON.parse(readFileSync(BFF_SETTINGS, 'utf8')) as {
    BackendApi?: { TimeoutSeconds?: unknown };
  };
  const renewal = /ForegroundWait\s*=\s*TimeSpan\.FromSeconds\((\d+)\)/.exec(
    readFileSync(TOKEN_REFRESHER, 'utf8'),
  );
  return {
    upstreamSeconds: settings.BackendApi?.TimeoutSeconds,
    renewalSeconds: renewal ? Number(renewal[1]) : undefined,
  };
}

const { upstreamSeconds, renewalSeconds } = backendWaits();
const bffWorstAnswerMs = (Number(upstreamSeconds) + Number(renewalSeconds)) * 1000;

describe("the SPA's abort and budget against the BFF's worst answer", () => {
  it('reads both backend waits from the files that set them', () => {
    expect(typeof upstreamSeconds).toBe('number');
    expect(typeof renewalSeconds).toBe('number');
    expect(upstreamSeconds).toBeGreaterThan(0);
    expect(renewalSeconds).toBeGreaterThan(0);
  });

  it("aborts a request only after the BFF's worst answer, with a margin", () => {
    expect(REQUEST_TIMEOUT_MS).toBeGreaterThanOrEqual(bffWorstAnswerMs + 5_000);
  });

  it("aborts a request before the ingress's own cut", () => {
    expect(REQUEST_TIMEOUT_MS).toBeLessThan(INGRESS_CUT_MS);
  });

  it("gives a read the time for two of the BFF's worst answers", () => {
    expect(READ_BUDGET_MS).toBeGreaterThanOrEqual(2 * bffWorstAnswerMs);
  });
});
