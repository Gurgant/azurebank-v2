import { useCallback } from 'react';
import {
  Link,
  Toast,
  ToastBody,
  ToastTitle,
  ToastTrigger,
  useToastController,
} from '@fluentui/react-components';
import type { ApiProblem } from '../../api/problemBaseQuery';
import { ACCOUNT_CLOSED } from '../../api/problemMessages';

export const appToasterId = 'app-toaster';

/*
  Closes the toast it is in: Fluent's own pattern, a link in the toast's title. An error toast here
  has no timeout, and this is the one way to close it that the toast shows: Escape closes every
  toast, but only once focus is inside the toaster.
*/
const dismissAction = (
  <ToastTrigger>
    <Link>Dismiss</Link>
  </ToastTrigger>
);

/*
  The visitor's own account is closed or gone (404 `ACCOUNT_NOT_FOUND`), said in the app's
  sentence alone: the server's names the account by its identifier, and its title is "Not
  Found". No support code either: nothing failed that support could trace. It stays until it
  is dismissed, as every error here does.
*/
const accountClosedToast = (
  <Toast>
    <ToastTitle action={dismissAction}>{ACCOUNT_CLOSED}</ToastTitle>
  </Toast>
);

/*
  The error toasts said here that a toaster holds, by id, so that `dismissProblemToasts` can take
  them back. An id goes in when a toaster takes the toast ("queued") and out when the visitor
  closes it ("dismissed": Dismiss, or Escape in the toaster). Not on "unmounted": under StrictMode,
  as the app runs in development, Fluent says it for every toast as it first mounts. Each id is
  new: Fluent drops a toast whose id is already in its toaster, and names a toast dispatched with
  none "0", "1" and on, which is why these carry a prefix.
*/
const heldProblemToasts = new Set<string>();
let problemToastCount = 0;

/** The options of one error toast: no timeout, and an id of its own. */
function untilDismissed() {
  problemToastCount += 1;
  const toastId = `problem-toast-${problemToastCount}`;
  return {
    intent: 'error',
    timeout: -1,
    toastId,
    onStatusChange: (_: unknown, { status }: { status: string }) => {
      if (status === 'queued') heldProblemToasts.add(toastId);
      if (status === 'dismissed') heldProblemToasts.delete(toastId);
    },
  } as const;
}

/**
 * Takes back every error toast said here that a toaster still holds, through that toaster's
 * controller, and forgets them: `AppToaster` calls it when the session they were about has ended,
 * and when it unmounts, taking its toasts with it. Only these: a toast said by anything else, such
 * as the demo's "You have a new copy.", stays.
 */
export function dismissProblemToasts(dismissToast: (toastId: string) => void): void {
  for (const toastId of heldProblemToasts) dismissToast(toastId);
  heldProblemToasts.clear();
}

/**
 * The one pipeline for surfacing an ApiProblem OUTSIDE its owning form (CONVENTIONS.md, "Data layer"): a
 * persistent error toast carrying the traceId as a support code (bare 32-hex — pastes
 * straight into Tempo). Flow-owned errors (validation, business rules, PIN, step-up)
 * render inline at their surface and never pass through here.
 *
 * `lead` is a sentence of the caller's that comes before the problem's own, for what the
 * problem cannot say: that a sign-out which failed left the visitor signed in.
 *
 * One code is worded here and not by the server: ACCOUNT_NOT_FOUND, which reaches this pipeline
 * from two callers, each for an account that is closed: setting it as the primary account, and
 * revealing its full number (`accountClosedToast`).
 */
export function useProblemToast() {
  const { dispatchToast } = useToastController(appToasterId);

  return useCallback(
    (problem: ApiProblem, lead?: string) => {
      if (problem.errorCode === 'ACCOUNT_NOT_FOUND') {
        dispatchToast(accountClosedToast, untilDismissed());
        return;
      }
      dispatchToast(
        <Toast>
          <ToastTitle action={dismissAction}>{problem.title ?? 'Something went wrong'}</ToastTitle>
          <ToastBody>
            {lead ? `${lead} ` : ''}
            {problem.detail ?? 'Please try again.'}
            {problem.traceId ? ` Support code: ${problem.traceId}` : ''}
          </ToastBody>
        </Toast>,
        untilDismissed(),
      );
    },
    [dispatchToast],
  );
}

/**
 * Says that the account is closed, for a dialog that closes on that answer: a dialog does not
 * outlive its account, so the sentence cannot be in it. The outlet is at the app's root and is
 * there whichever page the dialog was opened from.
 */
export function useAccountClosedToast(): () => void {
  const { dispatchToast } = useToastController(appToasterId);

  return useCallback(() => {
    dispatchToast(accountClosedToast, untilDismissed());
  }, [dispatchToast]);
}
