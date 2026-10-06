import { makeStyles } from '@fluentui/react-components';
import { isServiceOutage, type ApiProblem } from '../../api/problemBaseQuery';
import { CONNECTION_FAILED, SERVICE_UNAVAILABLE } from '../../api/problemMessages';
import { WaitHint } from '../../components/feedback';
import { ConfirmDialog } from '../../components/shared/ConfirmDialog';
import { useClaimDemoCopyMutation } from '../api/apiSlice';
import {
  START_OVER_CANCEL,
  START_OVER_CONFIRM,
  START_OVER_DAILY_LIMIT,
  START_OVER_FAILED,
  START_OVER_MESSAGE,
  START_OVER_POOL_EMPTY,
  START_OVER_RATE_LIMITED,
  START_OVER_TITLE,
} from './demoWords';
import { useNewCopyToast } from './useNewCopyToast';

const useStyles = makeStyles({
  hint: { marginTop: '12px' },
});

/**
 * What to say when the claim was refused or got no answer.
 *
 * The three refusals a claim has of its own are worded here, by their code, and never in the
 * server's words. Two of them add that the visitor's copy is as it was, which the server's
 * sentence does not say and which is the first thing to know before pressing anything else. The
 * limiter's wait is said in words and not counted down: the dialog stays open, and "Start over"
 * can be pressed again.
 *
 * Then the outage before the transport, the order `isServiceOutage` asks for
 * (src/api/problemBaseQuery.ts): a request with no answer in 65 s is `NETWORK` too, and it is the
 * service that did not answer. Neither of those two says the copy is unchanged. After a claim
 * with no answer nobody knows.
 *
 * What is left is a refusal this dialog has no sentence for. It prints the server's, as the
 * sign-in page does for a sign-in (src/pages/LoginPage.tsx), and its own fallback where there is
 * none. That includes an answer the app itself would not accept, which is no `ApiProblem` at
 * all: it has no code, no status and no sentence, and ends on the fallback.
 */
function startOverMessage(problem: ApiProblem): string {
  if (problem.errorCode === 'DEMO_POOL_EMPTY') return START_OVER_POOL_EMPTY;
  if (problem.errorCode === 'DEMO_DAILY_LIMIT') return START_OVER_DAILY_LIMIT;
  if (problem.errorCode === 'RATE_LIMIT_EXCEEDED') return START_OVER_RATE_LIMITED;
  if (isServiceOutage(problem)) return SERVICE_UNAVAILABLE;
  if (problem.status === 'NETWORK' || problem.status === 'PARSE') return CONNECTION_FAILED;
  return problem.detail || START_OVER_FAILED;
}

interface StartOverDialogProps {
  isOpen: boolean;
  /** The dialog is done: the visitor kept the copy, or a new one was claimed. */
  onClose: () => void;
  /** A new copy was claimed. Called after `onClose`, once per claim that succeeded. */
  onStartedOver?: () => void;
}

/**
 * Asks before a new demo copy takes the place of the one this browser keeps, claims it, and says
 * what came of it. One component, so that the question, each refusal and the news of a new copy
 * are worded once, whichever page offers to start over.
 *
 * The page keeps it mounted and turns `isOpen`, which is how the dialog under it is used
 * (src/components/shared/ConfirmDialog.tsx): closed, it is in the page and hidden.
 *
 * The claim is all this sends. What a claim that succeeded does to the visitor is not done here.
 * The new owner is signed in by the auth slice (src/features/auth/authSlice.ts); the copy is kept
 * in the browser and the cache of the copy before is dropped where the claim's answer arrives
 * (src/features/auth/sessionMiddleware.ts), for every caller of the claim alike. Nothing here
 * reads the answer, which holds the copy's password: that the claim's promise resolved is all
 * this component goes on.
 *
 * A refusal is said in the dialog, which stays open. It is forgotten when the dialog is closed,
 * so the dialog opened again asks its question and does not repeat the answer to the last one.
 */
export function StartOverDialog({ isOpen, onClose, onStartedOver }: StartOverDialogProps) {
  const styles = useStyles();
  const [claim, { isLoading, error, reset }] = useClaimDemoCopyMutation();
  const sayNewCopy = useNewCopyToast();

  const startOver = async () => {
    try {
      await claim().unwrap();
    } catch {
      // Said in the dialog, from the mutation's error.
      return;
    }
    onClose();
    sayNewCopy();
    onStartedOver?.();
  };

  const close = () => {
    reset();
    onClose();
  };

  return (
    <ConfirmDialog
      isOpen={isOpen}
      onClose={close}
      onConfirm={() => void startOver()}
      title={START_OVER_TITLE}
      message={START_OVER_MESSAGE}
      confirmText={START_OVER_CONFIRM}
      cancelText={START_OVER_CANCEL}
      isLoading={isLoading}
      errorText={error ? startOverMessage(error as ApiProblem) : null}
    >
      {/* A write: a claim the server may already be making cannot be given up on by the page. */}
      <WaitHint active={isLoading} kind="write" className={styles.hint} />
    </ConfirmDialog>
  );
}
