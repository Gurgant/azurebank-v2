import { createElement } from 'react';
import { Provider } from 'react-redux';
import { act, render, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { problem } from '../../mocks/problem';
import { server } from '../../mocks/server';
import { MOCK_PASSWORD, MOCK_USER, mockState } from '../../mocks/state';
import { enableDemoMode } from '../../test/demoMode';
import { makeTestStore, type TestStore } from '../../test/renderWithProviders';
import { apiSlice } from '../api/apiSlice';
import { AuthBootstrap } from '../auth/AuthBootstrap';
import { getDemoCopySnapshot } from './demoCopyStorage';

/*
  The claim at the store: what goes out, what comes back, and what a claim that succeeded makes of
  the visitor, of the browser's storage and of the cache.

  Three groups. In the first each test arms its own answer, so what it asserts is what this file
  was sent and what this file answered. In the second the page is the demo and the answer is the
  mock's own (src/mocks/handlers.ts), which hands out a copy and signs its owner in. The third is
  the same question about the cache, put to a sign-in.

  The first group's password and PIN are its own, on purpose. '987654' is not the demo's PIN
  (`DemoCopyDefaults.Pin`, backend/src/AzureBank.Shared/Constants/DemoCopyDefaults.cs), so a
  report that quoted either could only have taken it from this answer.
*/
const CLAIM = '*/bff/auth/demo/claim';

const copy = {
  email: 'demo-k7m2x9q4w8e1r5t3@azurebank.example',
  password: 'Fixture-Pass-7!',
  pin: '987654',
  contacts: ['jane_k7m2', 'mike_k7m2'],
  // The copy's end. The access token's, beside `user`, is another instant.
  expiresAt: '2026-10-05T09:00:00.1234567Z',
};

const data = {
  user: {
    id: 'u1',
    email: 'demo-k7m2x9q4w8e1r5t3@azurebank.example',
    firstName: 'John',
    lastName: 'Smith',
    azureTag: 'john_k7m2',
    hasPin: true,
  },
  expiresAt: '2026-10-04T09:15:00.1234567Z',
  copy,
};

/** The house envelope around a claim's `data`. */
function answer(claimed: unknown = data) {
  return HttpResponse.json(
    { data: claimed, message: 'Demo copy claimed' },
    { headers: { 'Cache-Control': 'no-store' } },
  );
}

function claim(store: TestStore) {
  return store.dispatch(apiSlice.endpoints.claimDemoCopy.initiate()).unwrap();
}

describe('a demo claim', () => {
  it('sends an empty JSON object with a JSON content type', async () => {
    const seen: { body?: string; contentType?: string | null } = {};
    server.use(
      http.post(CLAIM, async ({ request }) => {
        seen.contentType = request.headers.get('content-type');
        seen.body = await request.text();
        return answer();
      }),
    );

    await claim(makeTestStore());

    expect(seen).toStrictEqual({ body: '{}', contentType: 'application/json' });
  });

  it("hands back the envelope's data, checked", async () => {
    // One member the schema does not know, inside the copy: checked data comes back without it.
    server.use(http.post(CLAIM, () => answer({ ...data, copy: { ...copy, somethingNew: 123 } })));

    const claimed = await claim(makeTestStore());

    expect(claimed).toStrictEqual(data);
  });

  /*
    Redux Toolkit writes a response transform that threw to console.error and throws it again, and
    this suite fails any test that writes there (src/test/setup.ts). In the two tests below the
    refusal is the subject, so the report is stubbed, and then read: it is the one place where a
    claim's answer meets a log, and the answer holds a password and a PIN.

    Returns every text the refusal left behind: each argument of the one report, as a string and
    as JSON, and the error the store keeps for the caller.
  */
  async function textsLeftByARefusedAnswer(refusedCopy: unknown) {
    const report = vi.spyOn(console, 'error').mockImplementation(() => {});
    server.use(http.post(CLAIM, () => answer({ ...data, copy: refusedCopy })));
    const store = makeTestStore();

    await expect(claim(store)).rejects.toMatchObject({ name: 'ZodError' });

    expect(report).toHaveBeenCalledTimes(1);
    const reported = report.mock.calls[0];
    report.mockRestore();
    expect(String(reported[0])).toMatch(/An unhandled error occurred processing/);
    expect(String(reported[0])).toContain('the endpoint "claimDemoCopy"');

    const kept = Object.values(store.getState().api.mutations).map((entry) => entry?.error);
    expect(kept).toHaveLength(1);
    expect(kept[0]).toMatchObject({ name: 'ZodError' });

    return [...reported, ...kept].flatMap((left) => [String(left), String(JSON.stringify(left))]);
  }

  it('rejects an answer whose copy is not whole, and the report it leaves names no secret', async () => {
    const noContacts = {
      email: copy.email,
      password: copy.password,
      pin: copy.pin,
      expiresAt: copy.expiresAt,
    };

    for (const text of await textsLeftByARefusedAnswer(noContacts)) {
      expect(text).not.toContain('Fixture-Pass-7!');
      expect(text).not.toContain('987654');
    }
  });

  it('rejects an answer whose password and PIN are retyped, and the report does not quote them', async () => {
    // Here the members refused are the secrets themselves. A parser set to quote the input it
    // refused would write them into its error; the test above cannot show that, because a member
    // that is missing has no input to quote.
    const retyped = { ...copy, password: ['Fixture-Pass-7!'], pin: 987654 };

    for (const text of await textsLeftByARefusedAnswer(retyped)) {
      expect(text).not.toContain('Fixture-Pass-7!');
      expect(text).not.toContain('987654');
    }
  });

  it("a claim that succeeded signs the visitor in as the copy's owner", async () => {
    // ANONYMOUS on purpose: the shared setup signs every test in, and a visitor who claims from
    // the sign-in page has no session. The answer armed below is this test's own and reads no
    // mock state, so the line only says where the claim starts from.
    mockState.session = null;
    server.use(http.post(CLAIM, () => answer()));
    const store = makeTestStore();
    expect(store.getState().auth.status).toBe('unknown');

    await claim(store);

    expect(store.getState().auth.status).toBe('authenticated');
    expect(store.getState().auth.user?.email).toBe('demo-k7m2x9q4w8e1r5t3@azurebank.example');
    expect(store.getState().auth.user).toStrictEqual(data.user);
  });

  it('a claim that succeeded makes the app ask who is signed in again, and a refused one does not', async () => {
    // The app keeps `/bff/auth/me` in use from its first render
    // (src/features/auth/AuthBootstrap.tsx), and a fulfilled answer of it is the one place the
    // session's policy is learned (src/features/auth/sessionMiddleware.ts). A claim that
    // succeeded opens another session, so the question has to be put again; a refused one opens
    // none.
    //
    // The probe is MOUNTED here, as the app mounts it, because that is what puts the question
    // again: a claim that succeeded drops the whole cache, and a hook that is mounted asks again
    // for what it lost. A query kept in use by the store alone is not asked again after that.
    //
    // The refusal is the API's for an empty pool
    // (backend/src/AzureBank.Shared/Exceptions/DemoRefusalException.cs), and it comes first: the
    // count it leaves at 1 is the count the claim then moves to 2.
    let asked = 0;
    let poolIsEmpty = true;
    server.use(
      // Counted here, answered by the mock's own handler.
      http.get('*/bff/auth/me', () => {
        asked += 1;
        return undefined;
      }),
      http.post(CLAIM, () =>
        poolIsEmpty
          ? problem({
              status: 429,
              errorCode: 'DEMO_POOL_EMPTY',
              detail: 'All demo copies are in use right now. Please try again later.',
            })
          : answer(),
      ),
    );
    const store = makeTestStore();
    // Whatever the claim set going is given the time to ask, and then to be answered. The same
    // wait follows the refused claim and the one that succeeded, so the count it leaves unmoved
    // after the first is a count it would have seen move.
    const settled = () =>
      act(async () => {
        await Promise.all(store.dispatch(apiSlice.util.getRunningQueriesThunk()));
      });

    render(createElement(Provider, { store, children: createElement(AuthBootstrap) }));
    await waitFor(() => expect(store.getState().auth.status).toBe('authenticated'));
    const atFirst = asked;

    await act(async () => {
      await expect(claim(store)).rejects.toMatchObject({
        status: 429,
        errorCode: 'DEMO_POOL_EMPTY',
      });
    });
    await settled();
    const afterARefusedClaim = asked;

    poolIsEmpty = false;
    await act(async () => {
      await claim(store);
    });
    await settled();
    const afterAClaim = asked;

    expect({ atFirst, afterARefusedClaim, afterAClaim }).toStrictEqual({
      atFirst: 1,
      afterARefusedClaim: 1,
      afterAClaim: 2,
    });
  });
});

/*
  In the demo, against the mock's own claim. The first copy the mock hands out is its pool's first
  (src/mocks/state.ts), typed out below: a fixture no server knows.
*/
const KEY = 'azurebank.demoCopy';

/** What the browser's storage holds under the key, with nothing of the product in between. */
function keptInTheBrowser(): unknown {
  const raw = localStorage.getItem(KEY);
  return raw === null ? null : JSON.parse(raw);
}

/** How many answers to "which accounts?" the store's cache holds. */
function accountsAnswersIn(store: TestStore): number {
  return Object.values(store.getState().api.queries).filter(
    (entry) => entry?.endpointName === 'getAccounts' && entry.status === 'fulfilled',
  ).length;
}

/** Puts the signed-in user's accounts in the cache, in use, as a mounted page keeps them. */
async function readAccounts(store: TestStore) {
  await store.dispatch(apiSlice.endpoints.getAccounts.initiate()).unwrap();
}

describe('a demo claim, in the demo', () => {
  it("a claim that succeeded leaves the copy in the browser, with the copy's end and not the token's", async () => {
    enableDemoMode();
    const store = makeTestStore();

    const claimed = await claim(store);

    const kept = keptInTheBrowser();
    expect({
      kept,
      // The answer carries two ends, and the mock's two differ: the access token's beside
      // `user`, and the copy's. Only the second is the copy's to keep.
      theAnswersTwoEndsDiffer: claimed.expiresAt !== claimed.copy.expiresAt,
      whatTheProductReadsBack: getDemoCopySnapshot(),
    }).toStrictEqual({
      kept: {
        v: 1,
        email: 'demo-k7m2x9q4w8e1r5t3@azurebank.example',
        password: 'Xk7p-Rm3w-Hn8d-Tq5v',
        pin: '123456',
        contacts: ['jane_k7m2', 'mike_k7m2'],
        expiresAt: claimed.copy.expiresAt,
      },
      theAnswersTwoEndsDiffer: true,
      whatTheProductReadsBack: kept,
    });
  });

  it('…and nothing of the cache it found', async () => {
    enableDemoMode();
    const store = makeTestStore();
    // Whoever was signed in before the claim: the accounts in the cache are theirs.
    await readAccounts(store);
    const before = accountsAnswersIn(store);

    await claim(store);
    await Promise.all(store.dispatch(apiSlice.util.getRunningQueriesThunk()));

    expect({ before, after: accountsAnswersIn(store) }).toStrictEqual({ before: 1, after: 0 });
  });

  it('…and its password nowhere in the store', async () => {
    enableDemoMode();
    const store = makeTestStore();

    const claimed = await claim(store);

    const state = JSON.stringify(store.getState());
    expect(claimed.copy.password).toBe('Xk7p-Rm3w-Hn8d-Tq5v');
    // The owner's address is in the store, so this is the state after the claim, read whole.
    expect(state).toContain('demo-k7m2x9q4w8e1r5t3@azurebank.example');
    expect(state).not.toContain('Xk7p-Rm3w-Hn8d-Tq5v');
  });

  it('a claim that was refused changes neither', async () => {
    // CONTROL: green before this change
    enableDemoMode();
    server.use(
      http.post(CLAIM, () =>
        problem({
          status: 429,
          errorCode: 'DEMO_POOL_EMPTY',
          detail: 'All demo copies are in use right now. Please try again later.',
        }),
      ),
    );
    const store = makeTestStore();
    await readAccounts(store);

    await expect(claim(store)).rejects.toMatchObject({ status: 429, errorCode: 'DEMO_POOL_EMPTY' });
    await Promise.all(store.dispatch(apiSlice.util.getRunningQueriesThunk()));

    expect({ kept: keptInTheBrowser(), accountsAnswers: accountsAnswersIn(store) }).toStrictEqual({
      kept: null,
      accountsAnswers: 1,
    });
  });
});

describe('a sign-in and the cache it finds', () => {
  it('an ordinary sign-in resets nothing', async () => {
    // CONTROL: green before this change
    // No tag: the page is not the demo, and a sign-in there leaves the cache as it found it.
    const store = makeTestStore();
    await readAccounts(store);

    await store
      .dispatch(
        apiSlice.endpoints.login.initiate({ email: MOCK_USER.email, password: MOCK_PASSWORD }),
      )
      .unwrap();

    expect({
      signedInAs: store.getState().auth.user?.email,
      accountsAnswers: accountsAnswersIn(store),
    }).toStrictEqual({ signedInAs: MOCK_USER.email, accountsAnswers: 1 });
  });
});
