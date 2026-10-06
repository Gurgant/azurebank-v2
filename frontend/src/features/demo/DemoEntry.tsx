import { useEffect, useId, useRef } from 'react';
import {
  Button,
  Link,
  Spinner,
  Text,
  makeStyles,
  mergeClasses,
  tokens,
} from '@fluentui/react-components';
import { WaitHint } from '../../components/feedback';
import { formatDateTime } from '../../utils/format';
import type { StoredDemoCopy } from './demoCopyStorage';
import {
  CONTINUE_WITH_MY_COPY,
  COPY_FORGOTTEN,
  DEMO_NOTICE_FIRST,
  DEMO_NOTICE_REST,
  FORGET_THIS_COPY,
  GET_A_NEW_COPY,
  TRY_THE_DEMO,
  rememberedCopy,
} from './demoWords';

const useStyles = makeStyles({
  block: {
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
  },

  // The sign-in form's own button, in size: they are the page's ways in.
  button: {
    width: '100%',
    height: '44px',
    fontWeight: 600,
  },

  // `display: block` because Fluent's `Text` renders inline whatever element it is asked for.
  remembered: {
    display: 'block',
    margin: 0,
  },

  // As wide as its words, and in the middle of the block, as the link under the form is off the
  // demo (src/components/layout/AuthLayout.tsx). Left to the column it would be as wide as the
  // block, and so would its press.
  forget: {
    alignSelf: 'center',
    fontSize: '14px',
  },

  status: {
    display: 'block',
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
    color: tokens.colorNeutralForeground2,
    textAlign: 'center',
  },
  // With nothing to say the region is out of the page's flow, still in the page and still read:
  // an empty box in a column earns the column's gap, and would move the notice
  // (src/components/feedback/WaitHint.tsx has the measurement, and the same rule).
  silent: {
    position: 'absolute',
    width: '1px',
    height: '1px',
    margin: '-1px',
    padding: 0,
    border: 0,
    overflow: 'hidden',
    clip: 'rect(0 0 0 0)',
    whiteSpace: 'nowrap',
  },

  notice: {
    display: 'block',
    margin: 0,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
    color: tokens.colorNeutralForeground2,
  },
});

interface DemoEntryProps {
  /** The copy this browser keeps and the page offers, or `null` for none. */
  copy: StoredDemoCopy | null;
  /**
   * The request this block sent and is waiting for, if any: a claim from "Try the demo", or the
   * sign-in "Continue with my copy" sent. The button that sent it spins and its hint runs.
   */
  pending: 'claim' | 'continue' | null;
  /**
   * Nothing in the block can be pressed, the link included. The page decides: while any request
   * of the page runs, this block's or not, and while the page has been told to wait.
   */
  disabled: boolean;
  /** The kept copy's sign-in is locked: "Continue with my copy" waits, and nothing else does. */
  continueLocked: boolean;
  /** "Forget this copy" was pressed on this page. It is said once the page offers no copy. */
  forgotten: boolean;
  /** "Try the demo" was pressed. */
  onTry: () => void;
  /** "Continue with my copy" was pressed. */
  onContinue: () => void;
  /** "Get a new copy" was pressed. */
  onGetNew: () => void;
  /** "Forget this copy" was pressed. */
  onForget: () => void;
}

/**
 * The demo's block of the sign-in page: the way in for a visitor who has no copy's email and
 * password to type.
 *
 * It sends nothing, keeps nothing and reads nothing. The page owns the requests, their refusals,
 * the kept copy and where a visitor lands; this draws what the page tells it and reports each
 * press.
 *
 * FOR A BROWSER THAT KEEPS NO COPY, in order: "Try the demo", the wait's hint, the status, the
 * notice. The button is described by the notice's first sentence and by that element alone, so a
 * screen reader says what the button gets before it is pressed, and not the four sentences after
 * it on every focus. The rest of the notice is read where it stands.
 *
 * FOR A BROWSER THAT KEEPS ONE, in order: what it keeps and until when, "Continue with my copy",
 * "Get a new copy", the wait's hint, "Forget this copy", the status, the notice. "Try the demo" is
 * not there: a second copy is never claimed by a press meant for the first. The notice is shown
 * here too, as words to read and no button's description: whoever claims through "Get a new copy"
 * on a browser somebody else used reads "Don't enter real personal data." and what is kept of
 * their network address before they do.
 *
 * "Forget this copy" looks like a link and is a button: it goes nowhere. It is disabled with the
 * buttons. Pressed while "Continue with my copy" is on its way, it would remove the copy that
 * request is signing in to.
 *
 * The hint sits under the buttons and inside nothing else. Inside the element a button's
 * `aria-describedby` points at, its words would be read as part of that button's description
 * (src/components/feedback/WaitHint.tsx).
 *
 * THE STATUS is the block's own polite region, and it is on the page, empty, from the block's
 * first render, in both states: a polite region has to exist before its words change, or the
 * change is not read. It is one element at one place among the block's children, so the region
 * that was there beside the copy is the region that says the copy was forgotten.
 *
 * While it waits a button keeps its words and shows its spinner beside them, as its icon. A
 * spinner in the words' place would leave the button with no name for as long as the wait lasts.
 */
export function DemoEntry({
  copy,
  pending,
  disabled,
  continueLocked,
  forgotten,
  onTry,
  onContinue,
  onGetNew,
  onForget,
}: DemoEntryProps) {
  const styles = useStyles();
  const noticeFirstId = useId();
  const tryTheDemo = useRef<HTMLButtonElement>(null);

  // The wait this block's own button started: a claim with no copy, a sign-in with one.
  const waiting = copy ? pending === 'continue' : pending === 'claim';
  // Said only while the page offers no copy. A copy that came back since is not a forgotten one.
  const saysForgotten = forgotten && copy === null;

  // "Forget this copy" goes from the page under its own press, and the focus it had would fall to
  // the page with it. It goes to the button that took the block's place.
  useEffect(() => {
    if (saysForgotten) tryTheDemo.current?.focus();
  }, [saysForgotten]);

  return (
    <div className={styles.block}>
      {copy ? (
        <>
          <Text as="p" className={styles.remembered}>
            {rememberedCopy(formatDateTime(copy.expiresAt))}
          </Text>
          <Button
            appearance="primary"
            size="large"
            className={styles.button}
            disabled={disabled || continueLocked}
            icon={waiting ? <Spinner size="tiny" /> : undefined}
            onClick={onContinue}
          >
            {CONTINUE_WITH_MY_COPY}
          </Button>
          <Button
            appearance="secondary"
            size="large"
            className={styles.button}
            disabled={disabled}
            onClick={onGetNew}
          >
            {GET_A_NEW_COPY}
          </Button>
        </>
      ) : (
        <Button
          ref={tryTheDemo}
          appearance="primary"
          size="large"
          className={styles.button}
          disabled={disabled}
          icon={waiting ? <Spinner size="tiny" /> : undefined}
          aria-describedby={noticeFirstId}
          onClick={onTry}
        >
          {TRY_THE_DEMO}
        </Button>
      )}
      {/* A write: a claim or a sign-in the server may already be making cannot be given up on by
          the page. */}
      <WaitHint active={waiting} kind="write" />
      {copy && (
        <Link as="button" className={styles.forget} disabled={disabled} onClick={onForget}>
          {FORGET_THIS_COPY}
        </Link>
      )}
      <Text role="status" className={mergeClasses(styles.status, !saysForgotten && styles.silent)}>
        {saysForgotten ? COPY_FORGOTTEN : ''}
      </Text>
      <Text as="p" className={styles.notice}>
        <span id={noticeFirstId}>{DEMO_NOTICE_FIRST}</span> {DEMO_NOTICE_REST}
      </Text>
    </div>
  );
}
