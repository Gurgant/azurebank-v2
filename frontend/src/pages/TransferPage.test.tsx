import { Route, Routes } from 'react-router-dom';
import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { confirmWithPin, enterPin } from '../test/pinFlow';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '../mocks/server';
import { problem } from '../mocks/problem';
import { renderWithProviders } from '../test/renderWithProviders';
import { settle } from '../test/settle';
import { StepUpModal } from '../features/auth';
import { TransferPage } from './TransferPage';
import { MAIN_ACCOUNT_ID, SAVINGS_ACCOUNT_ID, mockState, seedMockSession } from '../mocks/state';
import { ACCOUNT_CLOSED } from '../api/problemMessages';
import { COPY, emulateFocusFixup } from '../test/outage';

/**
 * PR-11 — the external transfer end to end, PIN collected in the page (ADR-0041/0042): pick an
 * account, confirm a recipient by exact AzureTag, enter an amount, review, PIN → the authorisation
 * is minted and spent on Send → receipt. Until ADR-0041 Send met a level-2 403 and the root
 * StepUpModal collected the PIN; it stays mounted below to prove it no longer appears.
 */

type RouterEntries = NonNullable<Parameters<typeof renderWithProviders>[1]>['routerEntries'];

/**
 * The entries end on the page: one before it is where the browser's Back leads. An entry may be
 * `{ pathname: '/', state }`, the state a navigation to the page carries.
 */
function renderTransfer(routerEntries: RouterEntries = ['/']) {
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
      <Route path="/transfer/internal" element={<div>BETWEEN MY ACCOUNTS</div>} />
    </Routes>,
    { routerEntries },
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

      Asserted on STATUS, as the page branches: any 429 here gets the same sentence.
    */
    server.use(
      http.get('*/api/users/:azureTag', () =>
        problem({
          status: 429,
          errorCode: 'RATE_LIMIT_EXCEEDED',
          detail: 'Too many requests. Please retry later.',
          headers: { 'Retry-After': '90' },
        }),
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

  const NO_ACCOUNT_SENTENCE =
    "That recipient can't receive money right now. Choose another recipient.";

  it.each([
    ['mint', '*/api/transfers/authorizations'],
    ['send', '*/api/transfers'],
  ])(
    'a payee with no open account refused at the %s reads our sentence, and it survives to the form',
    async (_phase, path) => {
      // The API's answer, held by TransferOrWithdrawalOnClosedAccountSqlServerTests.
      server.use(
        http.post(path, () =>
          problem({
            status: 422,
            errorCode: 'RECIPIENT_NO_ACCOUNT',
            detail: 'Recipient does not have an active account.',
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

      expect(await screen.findByText(NO_ACCOUNT_SENTENCE, { exact: false })).toBeInTheDocument();
      expect(screen.queryByText(/does not have an active account/)).not.toBeInTheDocument();

      // PIN step to review, review to form: the two Backs a visitor needs to change the payee.
      const pinBacks = screen.getAllByRole('button', { name: 'Back' });
      await userEvent.click(pinBacks[pinBacks.length - 1]);
      const reviewBacks = await screen.findAllByRole('button', { name: 'Back' });
      await userEvent.click(reviewBacks[reviewBacks.length - 1]);

      expect(await screen.findByLabelText('Recipient handle')).toBeInTheDocument();
      expect(screen.getByText(NO_ACCOUNT_SENTENCE, { exact: false })).toBeInTheDocument();
    },
  );

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

        /*
          Every authorisation presented under the key was refused, so nothing under it can land:
          "start over" is offered as soon as the listing has answered. The account is closed, so
          the listing is refused, and asking it again would be refused too.
        */
        const startOver = await screen.findByRole('button', {
          name: "It didn't go through — start over",
        });
        expect(screen.getByText(COPY.movementUnchecked)).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: 'Check again' })).not.toBeInTheDocument();
        await settle();
        await userEvent.click(startOver);
        const words = await screen.findByText(ACCOUNT_CLOSED);
        expect(words.closest('[role="alert"]')).not.toBeNull();
        expectTheChoiceCleared();
        expect(screen.getByRole('button', { name: 'Close' })).toBeEnabled();

        // The transfer made next is another one: it does not carry the key that was let go.
        await settle();
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
      expect(await screen.findByText(COPY.movementUnchecked)).toBeInTheDocument();

      await settle();
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

/*
  The Transfer button on an account's card navigates here with `{ fromAccountId }` in the
  history entry's state. That account comes before the primary while the form has never named
  a source; it is only ever one of the accounts the page received.
*/
describe('a transfer started from an account card', () => {
  function from(fromAccountId: unknown) {
    return [{ pathname: '/', state: { fromAccountId } }];
  }

  function expectChosen(name: 'Main Account' | 'Rainy Day') {
    const other = name === 'Main Account' ? 'Rainy Day' : 'Main Account';
    expect(screen.getByRole('button', { name: `From ${name}` })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    expect(screen.getByRole('button', { name: `From ${other}` })).toHaveAttribute(
      'aria-pressed',
      'false',
    );
  }

  it('chooses the account the state names, reviews it and sends from it', async () => {
    const sends: { fromAccountId: string }[] = [];
    server.use(
      http.post('*/api/transfers', async ({ request }) => {
        sends.push((await request.clone().json()) as { fromAccountId: string });
      }),
    );
    renderTransfer(from(SAVINGS_ACCOUNT_ID));
    await screen.findByText('Main Account');
    expectChosen('Rainy Day');

    await verifyRecipient('friend');
    await screen.findByText('A. Friend');
    await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
    await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
    expect(screen.getByText('From').parentElement).toHaveTextContent('FromRainy Day');

    await confirmWithPin();
    expect(await screen.findByText('Transfer Sent!')).toBeInTheDocument();
    expect(sends).toEqual([expect.objectContaining({ fromAccountId: SAVINGS_ACCOUNT_ID })]);
  });

  it('chooses the primary when the state names no account the page received', async () => {
    renderTransfer(from('019f7b3f-0000-7000-8000-0000000000ff'));
    await screen.findByText('Main Account');
    expectChosen('Main Account');
  });

  it('chooses the primary when the id in the state is not a string', async () => {
    renderTransfer(from(Number.NaN));
    await screen.findByText('Main Account');
    expectChosen('Main Account');
  });

  it('chooses the primary when the navigation carries no state', async () => {
    renderTransfer(['/']);
    await screen.findByText('Main Account');
    expectChosen('Main Account');
  });

  it("gives way to the visitor's own choice of another card", async () => {
    renderTransfer(from(SAVINGS_ACCOUNT_ID));
    await screen.findByText('Main Account');
    await userEvent.click(screen.getByRole('button', { name: 'From Main Account' }));
    expectChosen('Main Account');
  });

  it('does not stand in once the closed-account rule has cleared the choice', async () => {
    /*
      The mint answers ACCOUNT_NOT_FOUND and the list cannot be read again, so Rainy Day is
      still listed. The choice is cleared, and the account the state named does not come back.
    */
    renderTransfer(from(SAVINGS_ACCOUNT_ID));
    await screen.findByText('Main Account');
    await verifyRecipient('friend');
    await screen.findByText('A. Friend');
    await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
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

    expect(await screen.findByText(ACCOUNT_CLOSED)).toBeInTheDocument();
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
});

/*
  The wiring of the balance guard on this page. `balance-guard.test.tsx` proves the shared pieces
  against the withdraw dialog and `InternalTransferPage.test.tsx` holds the same wiring on the
  move between own accounts; this page had none, and it is the one where the money leaves.
*/
describe('the balance guard on a transfer to another person', () => {
  it('does not reach the PIN step when the server says the balance has moved', async () => {
    let reads = 0;
    let mints = 0;
    server.use(
      http.get('*/api/accounts', () => {
        reads += 1;
        // Read 1 draws the form with the seeded €1,250.50; from the gate's read onward the
        // account holds €10, so the €50 under review can no longer be sent.
        const balance = reads === 1 ? 1250.5 : 10;
        const [main, ...rest] = mockState.accounts;
        return HttpResponse.json({ data: [{ ...main, balance }, ...rest], message: null });
      }),
      http.post('*/api/transfers/authorizations', () => {
        mints += 1;
      }),
    );
    renderTransfer();
    await screen.findByText('Main Account');
    await verifyRecipient('friend');
    await screen.findByText('A. Friend');
    await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
    await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));

    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));

    const words = await screen.findByText(
      'Insufficient balance for this operation — €10.00 available.',
    );
    expect(words.closest('[role="alert"]')).not.toBeNull();
    // Back on the form, where the amount can be changed, and no PIN was asked for.
    expect(screen.getByLabelText('Transfer amount')).toBeInTheDocument();
    expect(screen.queryByLabelText('Digit 1 of 6')).not.toBeInTheDocument();
    expect(mints).toBe(0);
  });
});

/*
  What the form offers besides typing, and every way off the page. No test pressed any of these:
  a header button that skipped the review, or a shortcut that filled in another amount than it
  says, would have shipped green.
*/
describe('external transfer: the shortcuts of the form and the ways off the page', () => {
  /** What the send asked the server to move, read from the request and not from the screen. */
  function watchSends() {
    const sends: { amount: number; recipientAzureTag: string }[] = [];
    server.use(
      http.post('*/api/transfers', async ({ request }) => {
        const body = (await request.clone().json()) as {
          amount: number;
          recipientAzureTag: string;
        };
        sends.push({ amount: body.amount, recipientAzureTag: body.recipientAzureTag });
      }),
    );
    return sends;
  }

  /** The form filled in for the handle "friend", on the review step. */
  async function toReview(amount = '50', routerEntries?: string[]) {
    const view = renderTransfer(routerEntries);
    await screen.findByText('Main Account');
    await verifyRecipient('friend');
    await screen.findByText('A. Friend');
    await userEvent.type(screen.getByLabelText('Transfer amount'), amount);
    await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
    return view;
  }

  /** The review card's rows, as label and value. */
  const reviewed = (label: string) => screen.getByText(label).parentElement;

  it('a quick amount can be pressed up to the whole balance, and not past it', async () => {
    // The source holds exactly €50: the server accepts an amount up to the balance, so €50 stays.
    server.use(
      http.get('*/api/accounts', () => {
        const [main, ...rest] = mockState.accounts;
        return HttpResponse.json({ data: [{ ...main, balance: 50 }, ...rest], message: null });
      }),
    );
    renderTransfer();
    await screen.findByRole('button', { name: 'Use maximum, €50.00' });

    expect(screen.getByRole('button', { name: '€25' })).toBeEnabled();
    expect(screen.getByRole('button', { name: '€50' })).toBeEnabled();
    expect(screen.getByRole('button', { name: '€100' })).toBeDisabled();
  });

  it('"Use max" fills in the whole available balance, to the cent', async () => {
    const sends = watchSends();
    renderTransfer();
    await screen.findByText('Main Account');
    await verifyRecipient('friend');
    await screen.findByText('A. Friend');

    await userEvent.click(screen.getByRole('button', { name: 'Use maximum, €1,250.50' }));
    await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));

    expect(reviewed('Amount')).toHaveTextContent('€1,250.50');
    expect(reviewed('New balance')).toHaveTextContent('€0.00');
    await confirmWithPin();
    expect(await screen.findByText('Transfer Sent!')).toBeInTheDocument();
    expect(sends).toEqual([{ amount: 1250.5, recipientAzureTag: 'friend' }]);
  });

  it('the receipt offers the history', async () => {
    await toReview();
    await confirmWithPin();
    await screen.findByText('Transfer Sent!');

    await userEvent.click(await screen.findByRole('button', { name: 'View History' }));

    expect(await screen.findByText('HISTORY')).toBeInTheDocument();
  });

  it("the header's Back goes one step at a time, and from the form it leaves for the dashboard", async () => {
    const sends = watchSends();
    await toReview();
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
    await screen.findByLabelText('Digit 1 of 6');
    // The header's button is the first "Back" on the page; each step has its own below it.
    const headerBack = () => screen.getAllByRole('button', { name: 'Back' })[0];

    await userEvent.click(headerBack());
    expect(await screen.findByRole('heading', { name: 'Review Transfer' })).toBeInTheDocument();
    expect(screen.queryByLabelText('Digit 1 of 6')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Continue' })).toBeEnabled();

    await userEvent.click(headerBack());
    expect(await screen.findByRole('heading', { name: 'Send Money' })).toBeInTheDocument();
    // The form comes back as it was left: the recipient is still the one that was checked.
    expect(screen.getByText('A. Friend')).toBeInTheDocument();

    await userEvent.click(headerBack());
    expect(await screen.findByText('DASHBOARD')).toBeInTheDocument();
    expect(sends).toEqual([]);
  });

  it("the header's Close leaves for the dashboard and sends nothing", async () => {
    const sends = watchSends();
    await toReview();

    await userEvent.click(screen.getByRole('button', { name: 'Close' }));

    expect(await screen.findByText('DASHBOARD')).toBeInTheDocument();
    expect(sends).toEqual([]);
  });

  it('"Between your own accounts" leaves for the move between own accounts', async () => {
    renderTransfer();
    await screen.findByText('Main Account');

    await userEvent.click(screen.getByRole('button', { name: 'Between your own accounts' }));

    expect(await screen.findByText('BETWEEN MY ACCOUNTS')).toBeInTheDocument();
  });

  /*
    The browser's own Back while a send's outcome is not known. Every button of the page is
    disabled then; the browser's is not the page's to disable, so the page asks (ADR-0028). The
    hook's own suite holds the blocker; this holds that the page draws the question and that
    each answer does what it says.
  */
  it("asks before the browser's Back leaves a transfer whose outcome is not known", async () => {
    server.use(
      http.post('*/api/transfers', () =>
        problem({ status: 409, errorCode: 'IDEMPOTENCY_IN_FLIGHT', detail: 'Still processing.' }),
      ),
    );
    const { router } = await toReview('50', ['/dashboard', '/']);
    await confirmWithPin();
    await screen.findByText(/Still processing/);
    const question = () => screen.findByRole('alertdialog', { name: 'Leave without finishing?' });

    await act(() => router.navigate(-1));
    expect(await question()).toHaveTextContent('check your history before sending it again');
    await userEvent.click(
      within(await question()).getByRole('button', { name: 'Stay on this page' }),
    );

    // Still on the page, with the way to check the same transfer again.
    await waitFor(() => expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument());
    expect(screen.queryByText('DASHBOARD')).not.toBeInTheDocument();
    expect(await screen.findByRole('button', { name: 'Check again' })).toBeEnabled();

    await act(() => router.navigate(-1));
    await userEvent.click(within(await question()).getByRole('button', { name: 'Leave anyway' }));
    expect(await screen.findByText('DASHBOARD')).toBeInTheDocument();
  });
});

/*
  The receipt replaces the PIN step, and the PIN boxes were disabled for the send: a browser hands
  their focus to `body`, where a screen reader says nothing of the receipt. The title takes that
  lost focus, as the went-through sentence and the check view's title do.
*/
describe('external transfer: the receipt takes the focus the send lost', () => {
  let stopFocusFixup: () => void;
  beforeEach(() => {
    stopFocusFixup = emulateFocusFixup();
  });
  afterEach(() => {
    stopFocusFixup();
  });

  /** The form filled in for the handle "friend", on the PIN step before its first digit. */
  async function toPin() {
    renderTransfer();
    await screen.findByText('Main Account');
    await verifyRecipient('friend');
    await screen.findByText('A. Friend');
    await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
    await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
    await screen.findByLabelText('Digit 1 of 6');
  }

  it('the sixth digit of the PIN leaves focus on the receipt’s title, read once, and Tab goes on to View History', async () => {
    await toPin();
    await enterPin();

    const title = await screen.findByText('Transfer Sent!');
    await waitFor(() => expect(title).toHaveFocus());
    // A paragraph that takes focus without joining the Tab order, in no live region and named by
    // no button's description: focus is what reads it, and only once.
    expect(title.tagName).toBe('P');
    expect(title).toHaveAttribute('tabindex', '-1');
    expect(title.closest('[role="alert"], [role="status"], [aria-live]')).toBeNull();
    for (const button of screen.getAllByRole('button')) {
      expect(button).not.toHaveAttribute('aria-describedby');
    }

    await userEvent.tab();
    expect(screen.getByRole('button', { name: 'View History' })).toHaveFocus();
  });

  it('leaves focus where the visitor put it during the send', async () => {
    let answer = () => {};
    const held = new Promise<void>((resolve) => {
      answer = resolve;
    });
    let sent = false;
    server.use(
      // Returns nothing, so the send goes on to the mock's own handler once answered.
      http.post('*/api/transfers', async () => {
        sent = true;
        await held;
      }),
    );
    await toPin();
    await enterPin();
    await waitFor(() => expect(sent).toBe(true));

    const elsewhere = document.createElement('button');
    elsewhere.textContent = 'Somewhere else';
    document.body.append(elsewhere);
    try {
      elsewhere.focus();
      expect(elsewhere).toHaveFocus();

      answer();
      const title = await screen.findByText('Transfer Sent!');
      await settle();
      expect(elsewhere).toHaveFocus();
      expect(title).not.toHaveFocus();
    } finally {
      // The handler is parked on this promise: a failed assertion must not leave it hanging.
      answer();
      elsewhere.remove();
    }
  });
});
