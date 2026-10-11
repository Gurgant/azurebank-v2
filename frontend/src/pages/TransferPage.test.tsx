import { Route, Routes } from 'react-router-dom';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { confirmWithPin, enterPin } from '../test/pinFlow';
import { describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '../mocks/server';
import { problem } from '../mocks/problem';
import { renderWithProviders } from '../test/renderWithProviders';
import { StepUpModal } from '../features/auth';
import { TransferPage } from './TransferPage';
import { MAIN_ACCOUNT_ID, SAVINGS_ACCOUNT_ID, mockState, seedMockSession } from '../mocks/state';
import { ACCOUNT_CLOSED } from '../api/problemMessages';

/**
 * PR-11 — the external transfer end to end, PIN collected in the page (ADR-0041/0042): pick an
 * account, confirm a recipient by exact AzureTag, enter an amount, review, PIN → the authorisation
 * is minted and spent on Send → receipt. Until ADR-0041 Send met a level-2 403 and the root
 * StepUpModal collected the PIN; it stays mounted below to prove it no longer appears.
 */

function renderTransfer() {
  return renderWithProviders(
    <Routes>
      <Route
        path="/"
        element={
          <>
            <TransferPage />
            <StepUpModal />
          </>
        }
      />
      <Route path="/dashboard" element={<div>DASHBOARD</div>} />
      <Route path="/history" element={<div>HISTORY</div>} />
    </Routes>,
    { routerEntries: ['/'] },
  );
}

async function verifyRecipient(tag: string) {
  await userEvent.type(screen.getByLabelText('Recipient handle'), tag);
  await userEvent.click(screen.getByRole('button', { name: 'Verify' }));
}

/*
  A session, seeded, because the step-up routes now demand one.

  The mock used to answer /bff/auth/verify-pin for a caller with NO session at all, so these
  tests never had to establish one and quietly exercised a state the product cannot reach: the
  real BFF reads the session first and answers 401. Aligning the mock (contract gate) made that
  visible. Seeding is the faithful fix — a user reaching a PIN prompt is, by construction,
  already signed in.
*/
beforeEach(() => {
  seedMockSession();
});

describe('external transfer (PR-11)', () => {
  it('confirms a recipient, authorises with a PIN in-form, and shows the receipt', async () => {
    renderTransfer();
    await screen.findByText('Main Account'); // accounts loaded; first is auto-selected

    await verifyRecipient('friend');
    expect(await screen.findByText('A. Friend')).toBeInTheDocument();
    expect(screen.getByText('@friend')).toBeInTheDocument();

    await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
    await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));

    /*
      Review -> Continue -> PIN -> Send, all in the page. Until ADR-0041 this line waited for the
      root step-up modal ("Verify it's you") to appear off a 403 from the BFF; the PIN now travels
      in the request body and the API verifies it, so there is no modal and no 403 to react to.
    */
    await confirmWithPin();
    expect(screen.queryByText("Verify it's you")).not.toBeInTheDocument();

    expect(await screen.findByText('Transfer Sent!')).toBeInTheDocument();
    expect(screen.getByText('-€50.00')).toBeInTheDocument();
    expect(screen.getByText('€1,200.50')).toBeInTheDocument(); // 1250.50 - 50
    await userEvent.click(await screen.findByRole('button', { name: 'Done' }));
    expect(await screen.findByText('DASHBOARD')).toBeInTheDocument();
  });

  it('surfaces "not found" for an unknown handle and blocks review', async () => {
    renderTransfer();
    await screen.findByText('Main Account');

    await verifyRecipient('nobody');
    expect(await screen.findByText(/We couldn't find @nobody/)).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
    // No confirmed recipient → Review stays disabled.
    expect(screen.getByRole('button', { name: 'Review Transfer' })).toBeDisabled();
  });

  it('Back from the PIN step returns to review without sending', async () => {
    /*
      This used to be "cancelling the PIN modal returns to review without sending" — the modal was
      the root step-up dialog, driven by a 403. ADR-0041 removed both, so the property is now about
      the page's own step machine: leaving the PIN step must not send, and must land on review with
      the send control still reachable.
    */
    renderTransfer();
    await screen.findByText('Main Account');
    await verifyRecipient('friend');
    await screen.findByText('A. Friend');
    await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
    await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));

    /*
      FIVE digits, not six, and that is the whole point of the rewrite.

      This test used to type a full PIN and then press Back, on the premise that leaving the PIN
      step must not send. ADR-0042 reversed the premise: the sixth digit IS the send, so a complete
      PIN can no longer be entered without sending, and a test that tried would be asserting
      something the product deliberately no longer offers. An INCOMPLETE PIN is the honest
      expression of "on the PIN step, nothing sent yet" — and it still exercises what this test was
      really defending, which is that Back returns to review and does not carry the digits with it.
    */
    await enterPin('12345');
    const backs = screen.getAllByRole('button', { name: 'Back' });
    await userEvent.click(backs[backs.length - 1]);

    expect(await screen.findByRole('button', { name: 'Continue' })).toBeInTheDocument();
    expect(screen.queryByText('Transfer Sent!')).not.toBeInTheDocument();
    // And the PIN did not survive the trip: coming back must re-ask rather than reuse it.
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
    // findBy, not getBy: `onReviewed` awaits the funds gate before it calls `wizard.toPin()`, so a
    // synchronous query here depends on that request settling inside userEvent's act scope.
    expect(await screen.findByLabelText('Digit 1 of 6')).toHaveValue('');
  });

  it('after IN_FLIGHT the retained key freezes Back; Send reuses the SAME key (no double-spend)', async () => {
    // The transfer returns IN_FLIGHT (a keep-key outcome) — the review Back must be disabled
    // (keyLive), so the body can't be edited to null the retained key, and Send reuses it.
    const keys: (string | null)[] = [];
    let calls = 0;
    server.use(
      http.post('*/api/transfers', ({ request }) => {
        keys.push(request.headers.get('Idempotency-Key'));
        calls += 1;
        return calls === 1
          ? problem({
              status: 409,
              errorCode: 'IDEMPOTENCY_IN_FLIGHT',
              detail: 'Still processing.',
            })
          : HttpResponse.json(
              {
                data: {
                  transactionNumber: 'TXN-X',
                  amount: 50,
                  newBalance: 1200.5,
                  recipientAzureTag: 'friend',
                  recipientName: 'A. Friend',
                  processedAt: '2026-07-22T12:00:00.0000000Z',
                },
                message: 'ok',
              },
              { status: 201 },
            );
      }),
    );
    renderTransfer();
    await screen.findByText('Main Account');
    await verifyRecipient('friend');
    await screen.findByText('A. Friend');
    await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
    await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
    await confirmWithPin();

    expect(await screen.findByText(/Still processing/)).toBeInTheDocument();
    // Both the header and review Back are frozen while the key is retained (can't reach the
    // form to edit the body and null the key).
    screen.getAllByRole('button', { name: 'Back' }).forEach((b) => expect(b).toBeDisabled());

    /*
      The retry is the banner's own `Check again`, not another walk through the flow: an IN_FLIGHT
      failure leaves the user on the PIN step with the PIN they already typed, because the page only
      clears it for PIN-specific refusals. With the sixth digit as the submit, re-typing is not
      available either — the value never changes, so `onComplete` cannot fire again — which is
      exactly why that control exists. It reuses the retained key AND re-presents the same
      authorisation, rather than minting a second one for an intent that is already in flight.
    */
    await userEvent.click(screen.getByRole('button', { name: 'Check again' }));
    expect(await screen.findByText('Transfer Sent!')).toBeInTheDocument();
    expect(keys).toHaveLength(2);
    expect(keys[0]).toBe(keys[1]); // SAME key on the retry
  });

  it('the source-account cards announce their name and which one is chosen', async () => {
    // Added with the drift fix. The internal wizard's identical cards have carried both attributes
    // since it was written; without them a screen-reader user here heard an unnamed button and had
    // no way to tell which account was selected — the blue border says that to sighted users only.
    renderTransfer();
    await screen.findByText('Main Account');

    const main = screen.getByRole('button', { name: 'From Main Account' });
    const rainy = screen.getByRole('button', { name: 'From Rainy Day' });
    expect(main).toHaveAttribute('aria-pressed', 'true'); // primary is auto-selected
    expect(rainy).toHaveAttribute('aria-pressed', 'false');

    await userEvent.click(rainy);
    expect(rainy).toHaveAttribute('aria-pressed', 'true');
    expect(main).toHaveAttribute('aria-pressed', 'false');
  });

  it('says the accounts could not be loaded, and Retry actually refetches', async () => {
    // External had no error handling at all — a failed load simply rendered an empty account
    // picker. Added here as well as on the internal page, because fixing one of two identical
    // wizards is how the drift this PR removes got created in the first place.
    let calls = 0;
    server.use(
      http.get('*/api/accounts', () => {
        calls += 1;
        return calls === 1
          ? problem({ status: 500, errorCode: 'INTERNAL_ERROR' })
          : HttpResponse.json({ data: [], message: null });
      }),
    );
    renderTransfer();

    expect(await screen.findByText(/Could not load your accounts/)).toBeInTheDocument();

    // And the form is gone: an amount field over an empty picker would stack "Available: €0.00"
    // and "Exceeds available balance of €0.00" underneath the real error.
    expect(screen.queryByLabelText('Transfer amount')).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(calls).toBe(2));
  });

  it('a rate-limited lookup says so, instead of blaming the connection', async () => {
    /*
      The recipient lookup had a bare `catch` that reported every failure as connectivity. That
      endpoint has a DEDICATED limiter — /api/users/* runs a tight per-user sliding window
      (ADR-0014) and rejects with 429 + Retry-After — so the failure a user is most likely to
      provoke, by tapping Verify repeatedly, told them to check a connection that was fine.

      Asserted on STATUS, not on an errorCode: the BFF's rejection body is a bare ProblemDetails
      with no errorCode, so the client synthesises HTTP_429 and there is nothing to match on.
    */
    server.use(
      http.get('*/api/users/:azureTag', () =>
        HttpResponse.json(
          { type: 'https://httpstatuses.com/429', title: 'Too Many Requests', status: 429 },
          { status: 429, headers: { 'Retry-After': '90' } },
        ),
      ),
    );
    renderTransfer();
    await screen.findByText('Main Account');

    await verifyRecipient('friend');

    expect(await screen.findByText(/Too many lookups/)).toBeInTheDocument();
    expect(screen.getByText(/2 minutes/)).toBeInTheDocument(); // 90s, rounded up by formatLockHorizon
    expect(screen.queryByText(/check your connection/)).not.toBeInTheDocument();
  });

  it(
    'still blames the connection when the connection really is the problem',
    { timeout: 15_000 },
    async () => {
      // Without this the test above passes just as well against a catch that says "too many lookups"
      // for everything — the exact defect being fixed, with the wording swapped.
      //
      // The generous timeout is not padding: problemBaseQuery RETRIES a NETWORK failure up to three
      // times before it ever reaches the catch, so the message cannot appear on the first tick.
      server.use(http.get('*/api/users/:azureTag', () => HttpResponse.error()));
      renderTransfer();
      await screen.findByText('Main Account');

      await verifyRecipient('friend');

      expect(
        await screen.findByText(/check your connection/, {}, { timeout: 8000 }),
      ).toBeInTheDocument();
      expect(screen.queryByText(/Too many lookups/)).not.toBeInTheDocument();
    },
  );

  it('a failed Send is ANNOUNCED, not just displayed', async () => {
    /*
      Measured before fixing: the money banners rendered a bare <MessageBar intent="error"> with no
      role and no aria-live. Fluent's MessageBar root is role="group" — NOT a live region — and it
      delegates announcing to useAnnounce(), whose default context is a no-op; this app mounts no
      AnnounceProvider anywhere (grep: zero hits). So a screen-reader user was told nothing at all
      when their transfer failed.

      LoginPage already solved this and says why in a comment. The money flows never got the same
      treatment — which also made the keep-vs-clear question below literally unobservable to those
      users until now.
    */
    server.use(
      http.post('*/api/transfers', () =>
        problem({ status: 422, errorCode: 'INSUFFICIENT_FUNDS', detail: 'Not enough.' }),
      ),
    );
    renderTransfer();
    await screen.findByText('Main Account');
    await verifyRecipient('friend');
    await screen.findByText('A. Friend');
    await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
    await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
    await confirmWithPin();

    const words = await screen.findByText('Insufficient funds for this transfer.', {
      exact: false,
    });
    expect(words.closest('[role="alert"]')).not.toBeNull();
  });

  it('a recipient the mint does not find reads "re-check the handle", not the server sentence', async () => {
    /*
      The Verify step found the handle; the mint, a moment later, does not. Observed on the API for
      a handle no user holds: 404 errorCode RECIPIENT_NOT_FOUND, detail "Recipient with identifier
      '<the handle>' was not found."
    */
    server.use(
      http.post('*/api/transfers/authorizations', () =>
        problem({
          status: 404,
          errorCode: 'RECIPIENT_NOT_FOUND',
          detail: "Recipient with identifier 'friend' was not found.",
        }),
      ),
    );
    renderTransfer();
    await screen.findByText('Main Account');
    await verifyRecipient('friend');
    await screen.findByText('A. Friend');
    await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
    await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
    await confirmWithPin();

    expect(
      await screen.findByText('That recipient could not be found. Please re-check the handle.', {
        exact: false,
      }),
    ).toBeInTheDocument();
    expect(screen.queryByText(/Recipient with identifier/)).not.toBeInTheDocument();
  });

  it('a recipient the send does not find reads the same sentence as on the mint', async () => {
    /*
      The mint found the handle and minted; the send, a moment later, does not find it. The same
      code reaches the page by the other of its two paths: the answer of POST /api/transfers.
    */
    server.use(
      http.post('*/api/transfers', () =>
        problem({
          status: 404,
          errorCode: 'RECIPIENT_NOT_FOUND',
          detail: "Recipient with identifier 'friend' was not found.",
        }),
      ),
    );
    renderTransfer();
    await screen.findByText('Main Account');
    await verifyRecipient('friend');
    await screen.findByText('A. Friend');
    await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
    await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
    await confirmWithPin();

    expect(
      await screen.findByText('That recipient could not be found. Please re-check the handle.', {
        exact: false,
      }),
    ).toBeInTheDocument();
    expect(screen.queryByText(/Recipient with identifier/)).not.toBeInTheDocument();
  });

  /*
    The visitor's own source account, closed in another tab or session while this page shows it.
    Measured through the BFF on a spare account closed after the page's requests were prepared:
    the mint and the send both answer 404 errorCode ACCOUNT_NOT_FOUND, detail "Account with
    identifier '<the id>' was not found.", and GET /api/accounts no longer lists the account.
    The mock answers the same once the account is out of its state, so these tests close the
    account there and override nothing.
  */
  describe('the source account is closed under the page', () => {
    /** As another session closes it: the account leaves the server's state, the page is not told. */
    function closeElsewhere(id: string) {
      mockState.accounts = mockState.accounts.filter((account) => account.id !== id);
    }

    /** Counts what the page asks, and lets the mock answer. */
    function watchRequests() {
      const seen = { lists: 0, mints: 0, sends: [] as { fromAccountId: string }[] };
      server.use(
        http.get('*/api/accounts', () => {
          seen.lists += 1;
        }),
        http.post('*/api/transfers/authorizations', () => {
          seen.mints += 1;
        }),
        http.post('*/api/transfers', async ({ request }) => {
          seen.sends.push((await request.clone().json()) as { fromAccountId: string });
        }),
      );
      return seen;
    }

    async function fillFromRainyDay() {
      renderTransfer();
      await screen.findByText('Main Account');
      await userEvent.click(screen.getByRole('button', { name: 'From Rainy Day' }));
      await verifyRecipient('friend');
      await screen.findByText('A. Friend');
      await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
    }

    function expectTheChoiceCleared() {
      // Nothing of the server's sentence, and no identifier.
      expect(screen.queryByText(/Account with identifier/)).not.toBeInTheDocument();
      expect(document.body).not.toHaveTextContent(SAVINGS_ACCOUNT_ID);
      // The picker is the list read again: the closed account is not in it.
      expect(screen.queryByRole('button', { name: 'From Rainy Day' })).not.toBeInTheDocument();
      // No silent switch: the account that is left is offered, not chosen.
      expect(screen.getByRole('button', { name: 'From Main Account' })).toHaveAttribute(
        'aria-pressed',
        'false',
      );
      expect(screen.getByRole('button', { name: 'Review Transfer' })).toBeDisabled();
      expect(screen.getByText('Choose an account to continue.')).toBeInTheDocument();
      // The amount typed is kept, and is not judged against the balance of no account.
      expect(screen.queryByText(/Exceeds available balance/)).not.toBeInTheDocument();
    }

    it('says the account is closed on the mint, reads the list again and clears the choice', async () => {
      const seen = watchRequests();
      await fillFromRainyDay();
      await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
      await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
      await screen.findByLabelText('Digit 1 of 6');

      closeElsewhere(SAVINGS_ACCOUNT_ID);
      const listsBefore = seen.lists;
      await enterPin();

      const words = await screen.findByText(ACCOUNT_CLOSED);
      expect(words.closest('[role="alert"]')).not.toBeNull();
      expect(ACCOUNT_CLOSED).toBe('This account is closed. Choose another account.');
      await waitFor(() => expect(seen.lists).toBeGreaterThan(listsBefore));
      await waitFor(() =>
        expect(screen.queryByRole('button', { name: 'From Rainy Day' })).not.toBeInTheDocument(),
      );
      expectTheChoiceCleared();
      expect(seen.mints).toBe(1);
      expect(seen.sends).toEqual([]);

      // The visitor chooses, and only then can the transfer go: from the account they picked.
      await userEvent.click(screen.getByRole('button', { name: 'From Main Account' }));
      expect(screen.queryByText(ACCOUNT_CLOSED)).not.toBeInTheDocument();
      await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
      await confirmWithPin();
      expect(await screen.findByText('Transfer Sent!')).toBeInTheDocument();
      expect(seen.sends).toEqual([expect.objectContaining({ fromAccountId: MAIN_ACCOUNT_ID })]);
    });

    it('says the same when it is the send that meets the closed account', async () => {
      const seen = watchRequests();
      // The mint finds the account; it is closed before the send arrives.
      server.use(
        http.post('*/api/transfers', () => {
          closeElsewhere(SAVINGS_ACCOUNT_ID);
        }),
      );
      await fillFromRainyDay();
      await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
      await confirmWithPin();

      expect(await screen.findByText(ACCOUNT_CLOSED)).toBeInTheDocument();
      await waitFor(() =>
        expect(screen.queryByRole('button', { name: 'From Rainy Day' })).not.toBeInTheDocument(),
      );
      expectTheChoiceCleared();
      expect(seen.mints).toBe(1);
      expect(seen.sends).toEqual([expect.objectContaining({ fromAccountId: SAVINGS_ACCOUNT_ID })]);
      // A refusal: the key of that send is not kept, so the header's exits work again.
      expect(screen.getByRole('button', { name: 'Close' })).toBeEnabled();
    });

    it('is on the form with the sentence, no PIN asked and no mint, when the list read on Continue lacks the account', async () => {
      const seen = watchRequests();
      await fillFromRainyDay();
      await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));

      closeElsewhere(SAVINGS_ACCOUNT_ID);
      await userEvent.click(screen.getByRole('button', { name: 'Continue' }));

      expect(await screen.findByText(ACCOUNT_CLOSED)).toBeInTheDocument();
      expectTheChoiceCleared();
      expect(screen.queryByLabelText('Digit 1 of 6')).not.toBeInTheDocument();
      expect(seen.mints).toBe(0);
      expect(seen.sends).toEqual([]);
    });

    it('clears the choice on the answer alone, while the list still shows the account', async () => {
      /*
        The answer, and a list that could not be read again: the page keeps the last list it
        received, with the closed account in it. The choice is cleared all the same.
      */
      await fillFromRainyDay();
      await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
      await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
      await screen.findByLabelText('Digit 1 of 6');
      server.use(
        http.get('*/api/accounts', () => HttpResponse.error()),
        http.post('*/api/transfers/authorizations', () =>
          problem({
            status: 404,
            errorCode: 'ACCOUNT_NOT_FOUND',
            detail: `Account with identifier '${SAVINGS_ACCOUNT_ID}' was not found.`,
          }),
        ),
      );
      await enterPin();

      const words = await screen.findByText(ACCOUNT_CLOSED);
      expect(words.closest('[role="alert"]')).not.toBeNull();
      expect(screen.queryByText(/Account with identifier/)).not.toBeInTheDocument();
      expect(screen.getByRole('button', { name: 'From Rainy Day' })).toHaveAttribute(
        'aria-pressed',
        'false',
      );
      expect(screen.getByRole('button', { name: 'From Main Account' })).toHaveAttribute(
        'aria-pressed',
        'false',
      );
      expect(screen.getByRole('button', { name: 'Review Transfer' })).toBeDisabled();
    });

    /*
      A send that answers a refused authorisation keeps its key for the same transfer with a new
      authorisation (ADR-0022, ADR-0042), and the visitor stays on the PIN step. The account is
      closed before the PIN is typed again, so the mint answers ACCOUNT_NOT_FOUND. The page does
      not stay on a PIN step with its exits locked: it shows the check view, whose two buttons
      are the ways on. The mock gives neither refusal to a send by itself, so the first answer
      is written here.
    */
    const REFUSED = {
      AUTHORIZATION_EXPIRED: {
        detail: 'This authorisation has expired. Enter your PIN again to confirm.',
        words: /your confirmation expired/,
      },
      AUTHORIZATION_INVALID: {
        detail: 'This authorisation cannot be used.',
        words: /That confirmation can no longer be used/,
      },
    } as const;

    async function reachTheCheckViewUnderAKeptKey(errorCode: keyof typeof REFUSED) {
      const seen = watchRequests();
      const keys: (string | null)[] = [];
      server.use(
        http.post('*/api/transfers', ({ request }) => {
          keys.push(request.headers.get('Idempotency-Key'));
          if (keys.length === 1) {
            return problem({ status: 401, errorCode, detail: REFUSED[errorCode].detail });
          }
        }),
      );
      await fillFromRainyDay();
      await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
      await confirmWithPin();
      await screen.findByText(REFUSED[errorCode].words);
      expect(screen.getByRole('button', { name: 'Close' })).toBeDisabled();

      closeElsewhere(SAVINGS_ACCOUNT_ID);
      await enterPin();

      expect(await screen.findByText("We couldn't confirm your transfer")).toBeInTheDocument();
      expect(screen.queryByLabelText('Digit 1 of 6')).not.toBeInTheDocument();
      expect(screen.queryByText(/Account with identifier/)).not.toBeInTheDocument();
      expect(seen.mints).toBe(2);
      expect(keys).toHaveLength(1);
      return { seen, keys };
    }

    it.each(['AUTHORIZATION_EXPIRED', 'AUTHORIZATION_INVALID'] as const)(
      'shows the check view when a PIN typed again after %s meets the closed account, and starts over under a new key',
      async (errorCode) => {
        const { seen, keys } = await reachTheCheckViewUnderAKeptKey(errorCode);

        await userEvent.click(
          screen.getByRole('button', { name: "It didn't go through — start over" }),
        );
        const words = await screen.findByText(ACCOUNT_CLOSED);
        expect(words.closest('[role="alert"]')).not.toBeNull();
        expectTheChoiceCleared();
        expect(screen.getByRole('button', { name: 'Close' })).toBeEnabled();

        // The transfer made next is another one: it does not carry the key that was let go.
        await userEvent.click(screen.getByRole('button', { name: 'From Main Account' }));
        await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
        await confirmWithPin();
        expect(await screen.findByText('Transfer Sent!')).toBeInTheDocument();
        expect(keys).toHaveLength(2);
        expect(keys[1]).not.toBe(keys[0]);
        expect(seen.sends).toEqual([expect.objectContaining({ fromAccountId: MAIN_ACCOUNT_ID })]);
      },
    );

    it('lets the key go at the check view: "Check recent transactions" leaves for the history, unasked', async () => {
      // A key still held would keep the visitor on the page: every exit refuses under one.
      const { keys } = await reachTheCheckViewUnderAKeptKey('AUTHORIZATION_EXPIRED');

      await userEvent.click(screen.getByRole('button', { name: 'Check recent transactions' }));

      expect(await screen.findByText('HISTORY')).toBeInTheDocument();
      expect(screen.queryByText('Leave without finishing?')).not.toBeInTheDocument();
      expect(screen.queryByText("We couldn't confirm your transfer")).not.toBeInTheDocument();
      expect(keys).toHaveLength(1);
    });
  });

  it('disables Review when the amount exceeds the balance', async () => {
    renderTransfer();
    await screen.findByText('Main Account');
    await verifyRecipient('friend');
    await screen.findByText('A. Friend');
    await userEvent.type(screen.getByLabelText('Transfer amount'), '99999');

    expect(screen.getByText(/Exceeds available balance/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Review Transfer' })).toBeDisabled();
  });

  it('revalidates the amount against the NEW balance when the source account changes', async () => {
    // CodeRabbit-caught regression guard: mode 'onChange' only revalidates the field that
    // changed, so without the explicit trigger-on-bound-change an account switch would
    // leave canReview reflecting the PREVIOUS account's balance (silent no-op on Send).
    renderTransfer();
    await screen.findByText('Main Account');
    await verifyRecipient('friend');
    await screen.findByText('A. Friend');
    // 900 fits Main Account (1250.50) but exceeds Rainy Day (830).
    await userEvent.type(screen.getByLabelText('Transfer amount'), '900');
    expect(screen.getByRole('button', { name: 'Review Transfer' })).toBeEnabled();

    await userEvent.click(screen.getByRole('button', { name: /Rainy Day/ }));
    expect(await screen.findByText('Exceeds available balance of €830.00.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Review Transfer' })).toBeDisabled();
  });
});
