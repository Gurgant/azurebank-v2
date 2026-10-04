import { describe, expect, it, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '../../mocks/server';
import { mockState } from '../../mocks/state';
import { makeTestStore, type TestStore } from '../../test/renderWithProviders';
import { apiSlice } from '../api/apiSlice';

/*
  The claim at the store: what goes out, what comes back, and what a claim that succeeded makes of
  the visitor. Each test arms its own answer, so what it asserts is what this file was sent and
  what this file answered.

  The fixture's password and PIN are its own, on purpose. '987654' is not the demo's PIN
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
});
