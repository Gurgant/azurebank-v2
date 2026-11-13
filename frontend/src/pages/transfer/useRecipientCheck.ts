import { useRef, useState } from 'react';
import { flushSync } from 'react-dom';
import { isServiceOutage, type ApiProblem } from '../../api/problemBaseQuery';
import { useLazyLookupRecipientQuery } from '../../features/api/apiSlice';
import { useAppDispatch, useAppSelector } from '../../app/hooks';
import { selectCurrentUser } from '../../features/auth/authSlice';
import { abortRunning, useWaitLanding, type StartedRead } from '../../hooks/useWaitLanding';
import { formatLockHorizon } from '../../utils/format';
import { normalizeAzureTag } from '../../forms/moneySchemas';
import { CONNECTION_FAILED, SERVICE_UNAVAILABLE } from '../../api/problemMessages';

// The API answers a handle of the wrong form with a 400 that has no sentence for a visitor, and its
// message per rule names either the length or the letters, never both: the page says the rule whole.
const HANDLE_FORMAT_SENTENCE =
  'A handle is 3 to 20 letters, numbers or underscores, and starts with a letter.';

export interface Recipient {
  azureTag: string;
  displayName: string;
}

/**
 * The recipient check of the transfer page: the lookup of a handle, what it found or said, and the
 * handle field's edit, which takes the answer down and stops the check that is running. It starts
 * no request but the lookup, and the wizard is the page's: `onBodyEdit` is its callback.
 */
export function useRecipientCheck(onBodyEdit: () => void) {
  const [lookup, lookupState] = useLazyLookupRecipientQuery();
  const dispatch = useAppDispatch();
  const user = useAppSelector(selectCurrentUser);

  /*
    The recipient check the visitor is waiting on, kept so that "Stop waiting" can find it: a lazy
    query has no argument on the page to name it by. Stopping it lands on Verify, which is where the
    check starts again. Its identity also keeps an answer from writing after an edit or a new check.
  */
  const runningLookup = useRef<StartedRead | null>(null);
  const lookupLanding = useWaitLanding<HTMLButtonElement>(
    lookupState.isFetching,
    lookupState.requestId,
  );
  const stopWaitingForLookup = () => {
    const running = runningLookup.current;
    if (running && abortRunning(dispatch, [running])) lookupLanding.arm();
  };
  const [recipient, setRecipient] = useState<Recipient | null>(null);
  const [recipientError, setRecipientError] = useState<string | null>(null);
  const [ownHandle, setOwnHandle] = useState(false);

  const handleVerifyRecipient = async (watchedTag: string) => {
    runningLookup.current = null;
    const tag = normalizeAzureTag(watchedTag);
    if (!tag) return;
    setRecipient(null);
    setRecipientError(null);
    setOwnHandle(false);
    onBodyEdit();
    if (user && tag === normalizeAzureTag(user.azureTag)) {
      // A lookup's wait commits the empty alert before its answer. Nothing waits here, and React
      // would fold the two into one render: the same sentence again would change nothing to say.
      flushSync(() => setRecipientError(null));
      setOwnHandle(true);
      setRecipientError(
        'That\'s your own handle. To move money between your own accounts, use "Between your own accounts" below.',
      );
      return;
    }
    const request = lookup(tag);
    runningLookup.current = request;
    try {
      const result = await request.unwrap();
      if (runningLookup.current !== request) return;
      if (result.exists) {
        setRecipient({ azureTag: result.azureTag, displayName: result.displayName });
      } else {
        setRecipientError(`We couldn't find @${tag}. Check the handle and try again.`);
      }
    } catch (caught) {
      if (runningLookup.current !== request) return;
      /*
        A bare catch here told every failure the same story — "check your connection" — and this
        endpoint has a DEDICATED rate limiter: /api/users/* runs a tight per-user sliding window
        (ADR-0014) and rejects with 429 + Retry-After. So the one failure a user is most likely to
        provoke, by tapping Verify repeatedly, sent them to check a connection that was fine.

        Branching on STATUS rather than on the code: any 429 here gets the same sentence.
        `retryAfterSeconds` is read from the Retry-After header the limiter always sets.
      */
      const problem = caught as ApiProblem;
      if (problem.status === 429) {
        setRecipientError(
          problem.retryAfterSeconds !== undefined
            ? `Too many lookups. Try again in ${formatLockHorizon(problem.retryAfterSeconds)}.`
            : 'Too many lookups. Please wait a moment and try again.',
        );
      } else if (isServiceOutage(problem)) {
        // Before the transport branch: a check with no answer in 65 s is `status: 'NETWORK'` too,
        // and "check your connection" would send the visitor after the wrong problem.
        setRecipientError(SERVICE_UNAVAILABLE);
      } else if (problem.status === 'NETWORK' || problem.status === 'PARSE') {
        setRecipientError(CONNECTION_FAILED);
      } else if (problem.errorCode === 'VALIDATION_ERROR') {
        // The only thing this route validates is the handle, and the answer carries no `detail`.
        setRecipientError(HANDLE_FORMAT_SENTENCE);
      } else {
        setRecipientError(problem.detail || "We couldn't check that handle. Please try again.");
      }
    }
  };

  /**
   * The handle field's edit: what the check holds goes, and a check that is running is stopped.
   * `write` is the form field's own `onChange`.
   */
  const editHandle = (value: string, write: (value: string) => void) => {
    const running = runningLookup.current;
    runningLookup.current = null;
    if (running) abortRunning(dispatch, [running]);
    write(value);
    setRecipient(null);
    setRecipientError(null);
    setOwnHandle(false);
    onBodyEdit();
  };

  return {
    lookupState,
    recipient,
    recipientError,
    ownHandle,
    handleVerifyRecipient,
    editHandle,
    stopWaitingForLookup,
    lookupLanding,
  };
}
