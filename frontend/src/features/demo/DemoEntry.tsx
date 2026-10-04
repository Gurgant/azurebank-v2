import { useId } from 'react';
import { Button, Spinner, Text, makeStyles, tokens } from '@fluentui/react-components';
import { WaitHint } from '../../components/feedback';
import { DEMO_NOTICE_FIRST, DEMO_NOTICE_REST, TRY_THE_DEMO } from './demoWords';

const useStyles = makeStyles({
  block: {
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
  },

  // The sign-in form's own button, in size: the two are the page's two ways in.
  button: {
    width: '100%',
    height: '44px',
    fontWeight: 600,
  },

  // `display: block` because Fluent's `Text` renders inline whatever element it is asked for.
  notice: {
    display: 'block',
    margin: 0,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
    color: tokens.colorNeutralForeground2,
  },
});

interface DemoEntryProps {
  /** The request this block sent and is waiting for, if any: its button spins and its hint runs. */
  pending: 'claim' | null;
  /**
   * Nothing in the block can be pressed. The page decides: while any request of the page runs,
   * this block's or not, and while the page has been told to wait.
   */
  disabled: boolean;
  /** "Try the demo" was pressed. */
  onTry: () => void;
}

/**
 * The demo's block of the sign-in page, for a browser that keeps no copy: the button that claims
 * one, and the notice that says what a claim gets and what it leaves behind.
 *
 * It sends nothing and keeps nothing. The page owns the claim, its refusals and where a new copy
 * lands; this draws what the page tells it and reports the press.
 *
 * In order: the button, the wait's hint, the notice.
 *
 * The button is described by the notice's first sentence and by that element alone, so a screen
 * reader says what the button gets before it is pressed, and not the four sentences after it on
 * every focus. The rest of the notice is read where it stands.
 *
 * The hint sits between the two and inside neither. Inside the element the button's
 * `aria-describedby` points at, its words would be read as part of the button's description
 * (src/components/feedback/WaitHint.tsx).
 *
 * While it waits the button keeps its words and shows its spinner beside them, as its icon. A
 * spinner in the words' place would leave the button with no name for as long as the wait lasts.
 */
export function DemoEntry({ pending, disabled, onTry }: DemoEntryProps) {
  const styles = useStyles();
  const noticeFirstId = useId();

  return (
    <div className={styles.block}>
      <Button
        appearance="primary"
        size="large"
        className={styles.button}
        disabled={disabled}
        icon={pending === 'claim' ? <Spinner size="tiny" /> : undefined}
        aria-describedby={noticeFirstId}
        onClick={onTry}
      >
        {TRY_THE_DEMO}
      </Button>
      {/* A write: a claim the server may already be making cannot be given up on by the page. */}
      <WaitHint active={pending === 'claim'} kind="write" />
      <Text as="p" className={styles.notice}>
        <span id={noticeFirstId}>{DEMO_NOTICE_FIRST}</span> {DEMO_NOTICE_REST}
      </Text>
    </div>
  );
}
