/**
 * Which backend the contract suite is pointed at.
 *
 * The whole point of this directory is that ONE set of assertions runs twice — against MSW and
 * against the real API+BFF — so that a divergence becomes a failing build instead of a discovery
 * three PRs later. Two live drifts were found by hand before this existed (a 422-vs-400 on the date
 * pair, and an invented `SERVER_ERROR` where the stack emits `HTTP_502`); a static audit then raised
 * 48 more. Every one of them was invisible to a suite whose only oracle was the mock.
 *
 * The URLs are IDENTICAL for both targets, which is what makes the trick work: the mock registers
 * every handler with a wildcard origin — an asterisk followed by the path, as in the login route —
 * so a request aimed at the real BFF's address is intercepted when MSW is listening and reaches the
 * real server when it is not. Nothing in the assertions knows which one answered.
 *
 * Credentials DO differ, and that is not a contract concern — the mock seeds `demo@azurebank.dev`
 * and the dev database seeds `admin@azurebank.dev`. Fixtures are per-target; shapes are not.
 */

export type ContractTarget = 'mock' | 'real';

function readTarget(): ContractTarget {
  const raw = process.env.CONTRACT_TARGET ?? 'mock';
  if (raw !== 'mock' && raw !== 'real') {
    throw new Error(`CONTRACT_TARGET must be 'mock' or 'real', got '${raw}'.`);
  }
  return raw;
}

export const CONTRACT_TARGET = readTarget();

/**
 * The BFF's address, used for BOTH targets on purpose (see above).
 *
 * Overridable so the real run can point at a differently-hosted stack without editing code.
 */
export const BASE_URL = process.env.CONTRACT_BASE_URL ?? 'http://localhost:5000';

interface Fixtures {
  email: string;
  password: string;
  pin: string;
}

/**
 * Per-target seed credentials. Kept here rather than in the tests so that no assertion has to know
 * which backend it is talking to — the moment a test branches on the target, it stops being a
 * contract test and becomes two different tests wearing one name.
 */
export const FIXTURES: Fixtures =
  CONTRACT_TARGET === 'mock'
    ? { email: 'demo@azurebank.dev', password: 'Password1!', pin: '123456' }
    : { email: 'admin@azurebank.dev', password: 'Test123!', pin: '123456' };
