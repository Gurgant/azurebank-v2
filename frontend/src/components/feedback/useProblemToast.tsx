import { useCallback } from 'react';
import { Toast, ToastBody, ToastTitle, useToastController } from '@fluentui/react-components';
import type { ApiProblem } from '../../api/problemBaseQuery';

export const appToasterId = 'app-toaster';

/**
 * The one pipeline for surfacing an ApiProblem OUTSIDE its owning form (D22): a
 * persistent error toast carrying the traceId as a support code (bare 32-hex — pastes
 * straight into Tempo). Flow-owned errors (validation, business rules, PIN, step-up)
 * render inline at their surface and never pass through here.
 *
 * `lead` is a sentence of the caller's that comes before the problem's own, for what the
 * problem cannot say: that a sign-out which failed left the visitor signed in.
 */
export function useProblemToast() {
  const { dispatchToast } = useToastController(appToasterId);

  return useCallback(
    (problem: ApiProblem, lead?: string) => {
      dispatchToast(
        <Toast>
          <ToastTitle>{problem.title ?? 'Something went wrong'}</ToastTitle>
          <ToastBody>
            {lead ? `${lead} ` : ''}
            {problem.detail ?? 'Please try again.'}
            {problem.traceId ? ` Support code: ${problem.traceId}` : ''}
          </ToastBody>
        </Toast>,
        { intent: 'error', timeout: -1 },
      );
    },
    [dispatchToast],
  );
}
