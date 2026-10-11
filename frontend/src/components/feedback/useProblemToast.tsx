import { useCallback } from 'react';
import { Toast, ToastBody, ToastTitle, useToastController } from '@fluentui/react-components';
import type { ApiProblem } from '../../api/problemBaseQuery';
import { ACCOUNT_CLOSED } from '../../api/problemMessages';

export const appToasterId = 'app-toaster';

/*
  The visitor's own account is closed or gone (404 `ACCOUNT_NOT_FOUND`), said in the app's
  sentence alone: the server's names the account by its identifier, and its title is "Not
  Found". No support code either: nothing failed that support could trace. It stays until it
  is dismissed, as every error here does.
*/
const accountClosedToast = (
  <Toast>
    <ToastTitle>{ACCOUNT_CLOSED}</ToastTitle>
  </Toast>
);
const untilDismissed = { intent: 'error', timeout: -1 } as const;

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
        dispatchToast(accountClosedToast, untilDismissed);
        return;
      }
      dispatchToast(
        <Toast>
          <ToastTitle>{problem.title ?? 'Something went wrong'}</ToastTitle>
          <ToastBody>
            {lead ? `${lead} ` : ''}
            {problem.detail ?? 'Please try again.'}
            {problem.traceId ? ` Support code: ${problem.traceId}` : ''}
          </ToastBody>
        </Toast>,
        untilDismissed,
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
    dispatchToast(accountClosedToast, untilDismissed);
  }, [dispatchToast]);
}
