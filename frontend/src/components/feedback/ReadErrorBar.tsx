import type { Ref } from 'react';
import { Button, MessageBar, MessageBarActions, MessageBarBody } from '@fluentui/react-components';
import type { ApiProblem } from '../../api/problemBaseQuery';

interface ReadErrorBarProps {
  /** The failure of the page's read. */
  problem: ApiProblem;
  /** The page's own sentence, for a failure that carries no detail from the server. */
  fallback: string;
  onRetry: () => void;
  /** The landing of the page's wait (`useWaitLanding`): focus comes back to this Retry. */
  retryRef: Ref<HTMLButtonElement>;
}

/**
 * The bar a page shows when its read has failed: what the server said, or the page's own sentence
 * when it said nothing, then the support code when the failure has one (the bridge to the
 * server's trace, ADR-0016), and a Retry.
 *
 * It has no role of its own. It goes inside the page's `AlertSlot`, which is the live region; the
 * bar's root is a `group`, and given `role="alert"` it would be a region nested in that one. The
 * page keys it by the request that failed, so that each new failure puts a new bar into the slot
 * and is announced again.
 */
export function ReadErrorBar({ problem, fallback, onRetry, retryRef }: ReadErrorBarProps) {
  return (
    <MessageBar intent="error">
      <MessageBarBody>
        {problem.detail || fallback}
        {problem.traceId ? ` Support code: ${problem.traceId}` : ''}
      </MessageBarBody>
      <MessageBarActions>
        <Button ref={retryRef} appearance="transparent" onClick={onRetry}>
          Retry
        </Button>
      </MessageBarActions>
    </MessageBar>
  );
}
