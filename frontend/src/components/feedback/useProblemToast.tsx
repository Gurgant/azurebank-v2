import { useCallback } from 'react';
import { Toast, ToastBody, ToastTitle, useToastController } from '@fluentui/react-components';
import type { ApiProblem } from '../../api/problemBaseQuery';

export const appToasterId = 'app-toaster';

/**
 * The one pipeline for surfacing an ApiProblem OUTSIDE its owning form (D22): a
 * persistent error toast carrying the traceId as a support code (bare 32-hex — pastes
 * straight into Tempo). Flow-owned errors (validation, business rules, PIN, step-up)
 * render inline at their surface and never pass through here.
 */
export function useProblemToast() {
  const { dispatchToast } = useToastController(appToasterId);

  return useCallback(
    (problem: ApiProblem) => {
      dispatchToast(
        <Toast>
          <ToastTitle>{problem.title ?? 'Something went wrong'}</ToastTitle>
          <ToastBody>
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
