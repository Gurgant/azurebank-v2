import { describe, expect, it } from 'vitest';
import { bffDemoClaimResponseSchema } from '../api/bffSchemas';
import { enableDemoMode, resetDemoMode } from '../test/demoMode';
import {
  MOCK_DEMO_POOL,
  MOCK_PASSWORD,
  MOCK_USER,
  mockState,
  resetMockState,
  seedMockDemoCopy,
} from './state';

/*
  Executable contract for the mock's demo: the claim, and who signs in while the page is the demo.

  Every test starts as the suite's setup leaves it: signed in as the mock's own user, the page
  without the demo's tag. A test that wants the demo puts the tag on the page; the same tag is what
  the mock reads, so the page and the mock never disagree about it.

  What a claim hands out is checked with the product's own schema, and its shapes are typed out
  here beside the places that give them: the address and the handles in
  backend/tools/AzureBank.Seeder/Pool/DemoCredentials.cs, the password in
  backend/src/AzureBank.Api/Security/DemoPasswordGenerator.cs, the PIN in
  backend/src/AzureBank.Shared/Constants/DemoCopyDefaults.cs, the two accounts in
  backend/tools/AzureBank.Seeder/Pool/DemoCopyBuilder.cs, and the refusal's sentence in
  backend/src/AzureBank.Shared/Exceptions/DemoRefusalException.cs.

  This file pins what the mock answers. Where that is what a running stack was seen to answer,
  handlers.ts and state.ts say so beside the value, with the day and what it was measured against;
  where it is read and not measured they say that, in the words "Read, not measured". A value
  replaced there is replaced here with it.
*/
const JSON_HEADERS = { 'Content-Type': 'application/json' };

/**
 * A claim as the app sends it: an empty JSON object, called JSON. `null` sends no body and no
 * content type; a second argument sends the body as that type instead.
 */
function claim(body: string | null = '{}', contentType = JSON_HEADERS['Content-Type']) {
  return fetch('/bff/auth/demo/claim', {
    method: 'POST',
    ...(body === null ? {} : { headers: { 'Content-Type': contentType }, body }),
  });
}

/** A claim that has to succeed: the answer's `data`, as the product's schema reads it. */
async function claimed() {
  const res = await claim();
  expect(res.status).toBe(200);
  return bffDemoClaimResponseSchema.parse((await res.json()).data);
}

function signIn(email: string, password: string) {
  return fetch('/bff/auth/login', {
    method: 'POST',
    headers: JSON_HEADERS,
    body: JSON.stringify({ email, password }),
  });
}

function reauthenticate(password: string) {
  return fetch('/bff/auth/reauthenticate', {
    method: 'POST',
    headers: JSON_HEADERS,
    body: JSON.stringify({ password }),
  });
}

function verifyPin(pin: string) {
  return fetch('/bff/auth/verify-pin', {
    method: 'POST',
    headers: JSON_HEADERS,
    body: JSON.stringify({ pin }),
  });
}

/** Whose session the mock holds, as the app asks: the address and the level, or the refusal. */
async function whoAmI() {
  const res = await fetch('/bff/auth/me');
  if (res.status !== 200) return { status: res.status };
  const { data } = await res.json();
  return { status: 200, email: data.user.email, authLevel: data.session.authLevel };
}

/**
 * The user the mock's session is on, to write on as a handler would. It throws when there is no
 * session, so a step that renames the user is never skipped in silence. Read through a function
 * because the compiler takes `mockState.session` for `null` from the line where a test sets it to
 * `null` to the end of that test, whatever a request did to it in between.
 */
function sessionUser() {
  const session = mockState.session;
  if (!session) throw new Error('The mock holds no session, so there is no user to write on.');
  return session;
}

interface ListedAccount {
  id: string;
  name: string;
  type: string;
  balance: number;
  isPrimary: boolean;
}

/** The signed-in user's accounts, in the order the API lists them: the primary one first. */
async function listedAccounts(): Promise<ListedAccount[]> {
  const res = await fetch('/api/accounts');
  expect(res.status).toBe(200);
  return (await res.json()).data;
}

async function accounts() {
  return (await listedAccounts()).map(({ name, type, balance, isPrimary }) => ({
    name,
    type,
    balance,
    isPrimary,
  }));
}

/** What every copy starts with: "Main Savings" 12,450.00, the primary, and "Checking" 2,300.00. */
const STARTING_ACCOUNTS = [
  { name: 'Main Savings', type: 'Savings', balance: 12450, isPrimary: true },
  { name: 'Checking', type: 'Checking', balance: 2300, isPrimary: false },
];

/** The body of a refused sign-in: one answer for a wrong password and for an unknown address. */
const INVALID_CREDENTIALS = {
  status: 401,
  errorCode: 'INVALID_CREDENTIALS',
  detail: 'Invalid email or password.',
  instance: '/api/auth/login',
};

describe("the mock's demo claim", () => {
  it('without the tag the claim is a path the BFF does not have', async () => {
    // A live session, last active a minute ago. A path the BFF does not have still slides that
    // clock (handlers.ts, at `runSessionActivityMiddleware`: measured on a route that does not
    // exist), so the claim does too, with or without a body.
    const aMinuteAgo = Date.now() - 60_000;
    mockState.sessionLastActivity = aMinuteAgo;

    const withABody = await claim();
    const withNone = await claim(null);

    expect({
      withABody: { status: withABody.status, body: await withABody.text() },
      withNone: { status: withNone.status, body: await withNone.text() },
      claimed: mockState.demoCopies,
      session: mockState.session,
      theClockSlid: mockState.sessionLastActivity > aMinuteAgo,
    }).toEqual({
      withABody: { status: 404, body: '' },
      withNone: { status: 404, body: '' },
      claimed: [],
      session: MOCK_USER,
      theClockSlid: true,
    });
  });

  it('with the tag a claim opens a session on a fresh copy', async () => {
    enableDemoMode();
    // The session the claim arrives with is five minutes old and had verified its PIN. The one
    // the claim opens starts now, and has verified nothing.
    mockState.sessionCreatedAt = Date.now() - 5 * 60_000;
    mockState.authLevel = 2;

    const res = await claim();

    expect(res.status).toBe(200);
    expect(res.headers.get('Cache-Control')).toBe('no-store');
    const body = await res.json();
    expect(Object.keys(body).sort()).toEqual(['data', 'message']);
    const data = bffDemoClaimResponseSchema.parse(body.data);
    // Nothing beside what the schema knows: no token, and no member the app would not read.
    expect(body.data).toEqual(data);

    expect(data.copy.email).toMatch(/^demo-[a-z0-9]{16}@azurebank\.example$/);
    expect(data.copy.password).toMatch(
      /^[A-HJ-NP-Za-kmnp-z2-9]{4}(-[A-HJ-NP-Za-kmnp-z2-9]{4}){3}$/,
    );
    expect(data.copy.pin).toBe('123456');

    // The owner is John Smith, and the copy's address is his. His handle and his two contacts'
    // share one suffix; the contacts come bare and sorted.
    expect(data.user.azureTag).toMatch(/^john_[a-z0-9]{4}$/);
    const suffix = data.user.azureTag.slice('john_'.length);
    expect(data.user).toEqual({
      id: data.user.id,
      email: data.copy.email,
      firstName: 'John',
      lastName: 'Smith',
      azureTag: `john_${suffix}`,
      hasPin: true,
    });
    expect(data.copy.contacts).toEqual([`jane_${suffix}`, `mike_${suffix}`]);

    // Two ends, apart: the copy's is 24 hours from the claim, the access token's 15 minutes.
    expect(Date.parse(data.copy.expiresAt) - Date.parse(data.expiresAt)).toBe(
      24 * 60 * 60_000 - 15 * 60_000,
    );
    // The mock's form for a copy's end: seven fractional digits and a Z, the longest the stack
    // writes. Measured 2026-10-05 on compose.yaml with compose.demo.yaml (Production):
    // "expiresAt": "2026-10-06T09:22:42.2604692Z" in `data.copy`. The stack writes fewer digits
    // when the last ones are zeros (src/mocks/state.ts), and the mock does not.
    expect(data.copy.expiresAt).toMatch(/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{7}Z$/);

    expect(await whoAmI()).toEqual({ status: 200, email: data.copy.email, authLevel: 1 });
    expect(await accounts()).toEqual(STARTING_ACCOUNTS);
  });

  it('a second claim is another copy with the starting balances', async () => {
    enableDemoMode();
    const first = await claimed();
    // The first copy spends: its sums are no longer the starting ones.
    mockState.accounts[0].balance = 11000.5;
    mockState.accounts[1].balance = 0;

    const second = await claimed();

    expect({
      sameAddress: second.copy.email === first.copy.email,
      samePassword: second.copy.password === first.copy.password,
      sameOwner: second.user.id === first.user.id,
      sameHandle: second.user.azureTag === first.user.azureTag,
    }).toEqual({ sameAddress: false, samePassword: false, sameOwner: false, sameHandle: false });
    expect(await whoAmI()).toEqual({ status: 200, email: second.copy.email, authLevel: 1 });
    expect(await accounts()).toEqual(STARTING_ACCOUNTS);
  });

  it("a copy's history ends on its two balances, and its two contacts are whom it can pay", async () => {
    enableDemoMode();
    const { copy } = await claimed();

    // The newest row of each account's history says what the account holds.
    const newestBalanceAfter: number[] = [];
    for (const account of await listedAccounts()) {
      const feed = await fetch(`/api/transactions?accountId=${account.id}&pageSize=1`);
      expect(feed.status).toBe(200);
      newestBalanceAfter.push((await feed.json()).data[0].balanceAfter);
    }
    expect(newestBalanceAfter).toEqual([12450, 2300]);

    // `friend` is whom the mock's own user can pay: the copy's directory holds its two contacts,
    // Jane Smith and Mike Brown, each under the masked name a lookup shows.
    const found: Record<string, string | false> = {};
    for (const handle of [...copy.contacts, 'friend']) {
      const lookup = await fetch(`/api/users/${handle}`);
      expect(lookup.status).toBe(200);
      const { data } = await lookup.json();
      found[handle] = data.exists && data.displayName;
    }
    expect(found).toEqual({
      [copy.contacts[0]]: 'Jane S.',
      [copy.contacts[1]]: 'Mike B.',
      friend: false,
    });
  });

  it('a new copy has the demo PIN, unlocked, whatever the copy before it did to its own', async () => {
    enableDemoMode();
    await claimed();
    // The first copy changed its PIN, missed twice, and is locked for a minute more.
    mockState.pin = '654321';
    mockState.pinAttempts = 2;
    mockState.pinLockedUntil = new Date(Date.now() + 60_000).toISOString();

    const { copy } = await claimed();
    // A wrong PIN first: with the old count of two it would be the third, and lock.
    const wrong = await verifyPin('000000');
    const right = await verifyPin(copy.pin);

    expect({
      wrong: { status: wrong.status, verified: (await wrong.json()).data?.verified },
      right: { status: right.status, verified: (await right.json()).data?.verified },
    }).toEqual({
      wrong: { status: 200, verified: false },
      right: { status: 200, verified: true },
    });
  });

  it('the pool runs out', async () => {
    enableDemoMode();
    const copies = [(await claimed()).copy, (await claimed()).copy, (await claimed()).copy];
    const addresses = copies.map((copy) => copy.email);
    expect(new Set(addresses).size).toBe(3);
    // Each of the three has the shapes the first is checked for above.
    for (const copy of copies) {
      expect(copy.email).toMatch(/^demo-[a-z0-9]{16}@azurebank\.example$/);
      expect(copy.password).toMatch(/^[A-HJ-NP-Za-kmnp-z2-9]{4}(-[A-HJ-NP-Za-kmnp-z2-9]{4}){3}$/);
    }

    const fourth = await claim();

    expect(fourth.status).toBe(429);
    expect(fourth.headers.get('Retry-After')).toBeNull();
    const refusal = await fourth.json();
    expect(refusal).toMatchObject({
      status: 429,
      errorCode: 'DEMO_POOL_EMPTY',
      detail: 'All demo copies are in use right now. Please try again later.',
      instance: '/api/auth/demo/claim',
    });
    expect(refusal).not.toHaveProperty('retryAfterSeconds');
    // A refused claim leaves the session it came with alive: the third copy's.
    expect(await whoAmI()).toEqual({ status: 200, email: addresses[2], authLevel: 1 });
    // And a test that asks for a fourth copy is told so, not handed nothing.
    expect(() => seedMockDemoCopy()).toThrow(/three demo copies/);
  });

  it("a claim spends one of the address's auth requests", async () => {
    enableDemoMode();
    const first = await claimed();
    expect(mockState.authCallTimes).toHaveLength(1);

    // Ten requests inside the minute: the limiter's budget for one address is used up.
    mockState.authCallTimes = Array.from({ length: 10 }, () => Date.now());
    const limited = await claim();

    expect(limited.status).toBe(429);
    expect(limited.headers.get('Retry-After')).toBe('60');
    expect(await limited.json()).toMatchObject({
      status: 429,
      errorCode: 'RATE_LIMIT_EXCEEDED',
      instance: '/bff/auth/demo/claim',
    });
    // The limiter answered before the claim: no second copy was taken, and the session stayed.
    expect(mockState.demoCopies).toHaveLength(1);
    expect(await whoAmI()).toEqual({ status: 200, email: first.copy.email, authLevel: 1 });

    // Off the demo the limiter still answers first: with the budget used up the claim is the
    // limiter's 429, where with a permit left it is the 404.
    resetDemoMode();
    expect((await claim()).status).toBe(429);
  });

  it('a claim with no readable body takes nothing', async () => {
    enableDemoMode();
    // A live session, last active a minute ago. A refused claim leaves it as it was but for its
    // clock, which a request that carries the cookie slides on the demo as it does off it.
    const aMinuteAgo = Date.now() - 60_000;
    mockState.sessionLastActivity = aMinuteAgo;

    /*
      Two refusals, and which one depends on what the body is called before on what it holds.
      Measured 2026-10-05 on compose.yaml with compose.demo.yaml (Production), six requests to
      POST /bff/auth/demo/claim:

        no body, no Content-Type              415
        Content-Type: text/plain, {}          415
        Content-Type: application/json, ""    400, errors keyed "" and "request"
        Content-Type: application/json, null  400, errors keyed "" and "request"
        ... "not json"                        400, errors keyed "$" and "request"
        ... []                                400, errors keyed "$" and "request"

      All six are the framework's own answers: application/problem+json, no errorCode, no
      cookie. The 415's body has no `errors` member.
    */
    const refused = async (res: Response) => {
      const body = await res.json();
      return {
        status: res.status,
        type: res.headers.get('Content-Type'),
        title: body.title,
        keyedBy: body.errors ? Object.keys(body.errors) : 'no errors member',
        errorCode: 'errorCode' in body,
      };
    };
    const unsupported = {
      status: 415,
      type: 'application/problem+json',
      title: 'Unsupported Media Type',
      keyedBy: 'no errors member',
      errorCode: false,
    };
    const unreadable = (...keyedBy: string[]) => ({
      status: 400,
      type: 'application/problem+json',
      title: 'One or more validation errors occurred.',
      keyedBy,
      errorCode: false,
    });

    expect({
      withNone: await refused(await claim(null)),
      asPlainText: await refused(await claim('{}', 'text/plain')),
      empty: await refused(await claim('')),
      aNull: await refused(await claim('null')),
      notJson: await refused(await claim('not json')),
      aList: await refused(await claim('[]')),
      claimed: mockState.demoCopies,
      session: mockState.session,
      theClockSlid: mockState.sessionLastActivity > aMinuteAgo,
    }).toEqual({
      withNone: unsupported,
      asPlainText: unsupported,
      empty: unreadable('', 'request'),
      aNull: unreadable('', 'request'),
      notJson: unreadable('$', 'request'),
      aList: unreadable('$', 'request'),
      claimed: [],
      session: MOCK_USER,
      theClockSlid: true,
    });
    // The pool as before: the next claim is handed its first copy.
    expect((await claimed()).copy.email).toBe(MOCK_DEMO_POOL[0].user.email);
  });

  it('what is done to a claimed copy reaches neither the pool nor the copy as claimed', () => {
    const handedOut = seedMockDemoCopy();
    const asHandedOut = structuredClone(handedOut);
    const asClaimed = structuredClone(mockState.demoCopies);
    expect(asClaimed).toHaveLength(1);

    // The visitor renames their handle, which the mock writes on the session's user, and a test
    // edits what it was handed. The copy the mock holds as claimed is as it was.
    sessionUser().azureTag = 'renamed';
    handedOut.user.firstName = 'Edited';
    handedOut.copy.contacts.push('someone_else');
    expect(mockState.demoCopies).toEqual(asClaimed);

    // A test edits the claimed copy itself. The pool is one constant for every test of a file:
    // after the reset the next test is handed the pool's first copy as the first test was.
    mockState.demoCopies[0].user.lastName = 'Edited';
    mockState.demoCopies[0].contacts.pop();
    resetMockState();
    const again = seedMockDemoCopy();
    expect({
      user: again.user,
      password: again.copy.password,
      contacts: again.copy.contacts,
    }).toEqual({
      user: asHandedOut.user,
      password: asHandedOut.copy.password,
      contacts: asHandedOut.copy.contacts,
    });
  });
});

describe("who signs in to the mock while the page is the demo, and who doesn't", () => {
  it('a claimed copy signs in again with its own pair, and only with it', async () => {
    enableDemoMode();
    const { copy } = seedMockDemoCopy();
    // Signed out. The copy stays claimed: this is the visitor who comes back to it.
    mockState.session = null;

    const withItsPair = await signIn(copy.email, copy.password);
    expect(withItsPair.status).toBe(200);
    expect((await withItsPair.json()).data.user.email).toBe(copy.email);
    expect(await whoAmI()).toEqual({ status: 200, email: copy.email, authLevel: 1 });

    // The session a sign-in opens holds a copy of the owner: a handle renamed there, which the
    // mock writes on the session's user, is not written on the copy as claimed.
    const asClaimed = structuredClone(mockState.demoCopies);
    sessionUser().azureTag = 'renamed';
    expect(mockState.demoCopies).toEqual(asClaimed);

    // An address is found whatever its spelling, a copy's as any other.
    expect((await signIn(copy.email.toUpperCase(), copy.password)).status).toBe(200);

    const wrongPassword = await signIn(copy.email, 'Wrong-Pass-1!');
    expect(wrongPassword.status).toBe(401);
    expect(await wrongPassword.json()).toMatchObject(INVALID_CREDENTIALS);

    // The mock's own password opens the mock's own user, not a copy.
    const anotherAccountsPassword = await signIn(copy.email, MOCK_PASSWORD);
    expect(anotherAccountsPassword.status).toBe(401);
    expect(await anotherAccountsPassword.json()).toMatchObject(INVALID_CREDENTIALS);

    // A claimed copy is an account, with the tag or without it: its pair signs in off the demo.
    resetDemoMode();
    expect((await signIn(copy.email, copy.password)).status).toBe(200);
  });

  it('five wrong passwords lock a claimed copy, as they lock any account', async () => {
    enableDemoMode();
    const { copy } = seedMockDemoCopy();
    mockState.session = null;

    // Spelled in capitals: every spelling of an address counts against the one account.
    const wrong: number[] = [];
    for (let attempt = 0; attempt < 5; attempt++) {
      wrong.push((await signIn(copy.email.toUpperCase(), 'Wrong-Pass-1!')).status);
    }
    const withItsPair = await signIn(copy.email, copy.password);

    expect(wrong).toEqual([401, 401, 401, 401, 401]);
    expect(withItsPair.status).toBe(429);
    expect(await withItsPair.json()).toMatchObject({
      status: 429,
      errorCode: 'ACCOUNT_LOCKED',
      instance: '/api/auth/login',
    });
  });

  it('in the demo nobody outside a claimed copy signs in', async () => {
    enableDemoMode();

    // The mock's own user, with its own password.
    const outsideEveryCopy = await signIn(MOCK_USER.email, MOCK_PASSWORD);
    expect(outsideEveryCopy.status).toBe(401);
    expect(await outsideEveryCopy.json()).toMatchObject(INVALID_CREDENTIALS);

    // A copy of the pool that nobody has claimed, with the pair a claim would hand out.
    const [free] = MOCK_DEMO_POOL;
    const neverClaimed = await signIn(free.user.email, free.password);
    expect(neverClaimed.status).toBe(401);
    expect(await neverClaimed.json()).toMatchObject(INVALID_CREDENTIALS);

    // Re-authentication goes through the same gate. The session the suite starts on is the mock's
    // own user's, on no claimed copy: its own password does not renew it, and it stays as it was.
    const outsideEveryCopyAgain = await reauthenticate(MOCK_PASSWORD);
    expect(outsideEveryCopyAgain.status).toBe(401);
    expect(await outsideEveryCopyAgain.json()).toMatchObject(INVALID_CREDENTIALS);
    expect(await whoAmI()).toEqual({ status: 200, email: MOCK_USER.email, authLevel: 1 });

    // Off the demo the same session and the same password renew it: the tag is what refused.
    resetDemoMode();
    expect((await reauthenticate(MOCK_PASSWORD)).status).toBe(200);
  });

  it("without the tag the mock's own user signs in", async () => {
    // CONTROL: green before this change
    const res = await signIn(MOCK_USER.email, MOCK_PASSWORD);

    expect(res.status).toBe(200);
    expect((await res.json()).data.user.email).toBe(MOCK_USER.email);
  });

  it("re-authentication takes the session copy's password", async () => {
    enableDemoMode();
    // Two copies are claimed and the session is on the second: the password asked for is that
    // copy's, not another claimed copy's and not the mock's own.
    const earlier = seedMockDemoCopy();
    const { copy } = seedMockDemoCopy();

    const withItsPassword = await reauthenticate(copy.password);
    expect(withItsPassword.status).toBe(200);
    expect((await withItsPassword.json()).data.user.email).toBe(copy.email);

    const withAnotherCopys = await reauthenticate(earlier.copy.password);
    expect(withAnotherCopys.status).toBe(401);
    expect(await withAnotherCopys.json()).toMatchObject(INVALID_CREDENTIALS);

    const withTheMocksOwn = await reauthenticate(MOCK_PASSWORD);
    expect(withTheMocksOwn.status).toBe(401);
    expect(await withTheMocksOwn.json()).toMatchObject(INVALID_CREDENTIALS);
  });

  it('a test can start signed in to a claimed copy, and the next test finds none', async () => {
    enableDemoMode();
    // What a test's teardown leaves behind: no session, no cookie, no claimed copy.
    resetMockState();

    const { user, copy } = seedMockDemoCopy();

    expect(user.email).toBe(copy.email);
    expect(await whoAmI()).toEqual({ status: 200, email: copy.email, authLevel: 1 });
    // And the page holds the session's cookie again: a request slides the session's clock, which
    // the mock does for a request that carries the cookie and for no other.
    const aMinuteAgo = Date.now() - 60_000;
    mockState.sessionLastActivity = aMinuteAgo;
    await whoAmI();
    expect(mockState.sessionLastActivity).toBeGreaterThan(aMinuteAgo);

    resetMockState();

    // That pair now signs in nowhere: not in the demo, where the copy is no longer claimed, and
    // not outside it, where its address is one nobody has.
    expect(mockState.demoCopies).toEqual([]);
    const inTheDemo = await signIn(copy.email, copy.password);
    resetDemoMode();
    const outsideIt = await signIn(copy.email, copy.password);
    expect({ inTheDemo: inTheDemo.status, outsideIt: outsideIt.status }).toEqual({
      inTheDemo: 401,
      outsideIt: 401,
    });
  });
});
