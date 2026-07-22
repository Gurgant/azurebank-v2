import { describe, expect, it } from 'vitest';
import {
  requestStepUp,
  settleStepUp,
  getStepUpSnapshot,
  __resetStepUpController,
} from './stepUpController';

/**
 * PR-11 — the single-flight mutex at the heart of the step-up bridge (DECISIONS §2.2):
 * N concurrent 403s must share ONE modal + ONE promise, and one settle resolves them all.
 * (test/setup.ts also resets the controller between cases.)
 */

describe('stepUpController (single-flight mutex)', () => {
  it('shares ONE in-flight promise across concurrent requests', async () => {
    const first = requestStepUp({ requiredAuthLevel: 2 });
    const second = requestStepUp({ requiredAuthLevel: 2 });

    expect(first).toBe(second); // same awaitable = one modal for N gated requests
    expect(getStepUpSnapshot()).not.toBeNull();

    settleStepUp('elevated');
    expect(await first).toBe('elevated');
    expect(await second).toBe('elevated'); // one settle resolves every caller
  });

  it('clears the snapshot after settling (modal unmounts)', async () => {
    const pending = requestStepUp({ requiredAuthLevel: 2 });
    settleStepUp('cancelled');
    await pending;
    await Promise.resolve(); // flush the .finally() microtask that nulls `current`
    expect(getStepUpSnapshot()).toBeNull();
  });

  it('a fresh request after a settle opens a NEW promise', async () => {
    const a = requestStepUp({ requiredAuthLevel: 2 });
    settleStepUp('elevated');
    await a;
    await Promise.resolve();
    const b = requestStepUp({ requiredAuthLevel: 2 });
    expect(b).not.toBe(a);
    __resetStepUpController();
  });
});
