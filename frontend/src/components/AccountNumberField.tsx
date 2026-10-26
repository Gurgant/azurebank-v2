import { useEffect, useRef, useState } from 'react';
import { Button, makeStyles, Spinner, Text } from '@fluentui/react-components';
import { Copy16Regular, Eye16Regular, EyeOff16Regular } from '@fluentui/react-icons';
import { colors } from '../theme/tokens';
import type { ApiProblem } from '../api/problemBaseQuery';
import type { AccountResponse } from '../features/api/apiSlice';
import { useRevealAccountNumberMutation } from '../features/api/apiSlice';
import { maskAccountNumber } from '../utils/format';
import { useProblemToast } from './feedback';

/** How long a revealed number stays on screen before it auto-rehides (ADR-0020 / ASVS 14.3.1). */
export const REVEAL_TIMEOUT_MS = 20_000;
const COPIED_TIMEOUT_MS = 2_000;

const useStyles = makeStyles({
  // Wraps, so that where the card has no room for the number and its buttons on one line, the
  // buttons go under the number: the gap is the space between the two lines too.
  row: {
    display: 'flex',
    flexWrap: 'wrap',
    alignItems: 'center',
    gap: '4px',
  },
  // The eye and copy, kept on one line: where the row wraps they move as a pair, never one beside
  // the number and the other under it.
  buttons: {
    display: 'flex',
    alignItems: 'center',
    gap: '4px',
  },
  // One line, always: left to wrap, a phone breaks the number at its hyphens, into as many as four
  // lines at 320 px, and a number in pieces reads as several numbers.
  number: {
    fontSize: '13px',
    fontWeight: 400,
    fontFamily: 'Consolas, "Courier New", monospace',
    color: colors.neutral[500],
    whiteSpace: 'nowrap',
  },
  // The masked number's words for a screen reader: out of the flow and clipped to nothing, still
  // in the accessibility tree, as `AlertSlot` keeps its empty slot.
  spoken: {
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
  copied: {
    fontSize: '12px',
    color: colors.neutral[600],
  },
});

/**
 * Masked-by-default account number with a PIN-gated reveal (ADR-0020). The eye button fires the
 * `revealAccountNumber` mutation; because its path is level-2 gated, an un-elevated call 403s and
 * rides `baseQueryWithStepUp` into the shared PIN modal, which replays the request on elevation —
 * so this component needs no step-up code of its own. The unmasked value lives ONLY in transient
 * component state (never the RTK Query store: we `reset()` it away) and auto-rehides on a timer or
 * on unmount (navigation).
 */
export function AccountNumberField({ account }: { account: AccountResponse }) {
  const styles = useStyles();
  const showProblem = useProblemToast();
  const [revealAccountNumber, { isLoading, reset }] = useRevealAccountNumberMutation();
  const [fullNumber, setFullNumber] = useState<string | null>(null);
  // 'copied' for the two seconds after a copy; 'hiddenAgain' only after the timer re-hides.
  const [notice, setNotice] = useState<'copied' | 'hiddenAgain' | null>(null);

  const hideTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const copiedTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  // Counts the copies started and the times the number was hidden, so that a clipboard write which
  // finishes after either says nothing over a newer notice.
  const copyEpoch = useRef(0);

  // Unmount (e.g. navigating away) counts as an auto-rehide: drop both timers so nothing fires
  // against a gone component and no revealed value lingers.
  useEffect(
    () => () => {
      clearTimeout(hideTimer.current);
      clearTimeout(copiedTimer.current);
    },
    [],
  );

  const revealed = fullNumber !== null;
  const lastTwo = account.accountNumber.slice(-2);

  // Hide passes null: the button says nothing. The timer passes 'hiddenAgain', and that is the
  // only path that does. Either clears a "Copied" that was still showing.
  const conceal = (after: 'hiddenAgain' | null) => {
    clearTimeout(hideTimer.current);
    clearTimeout(copiedTimer.current);
    copyEpoch.current += 1;
    setFullNumber(null);
    setNotice(after);
    reset();
  };

  const hide = () => conceal(null);

  const reveal = async () => {
    setNotice(null);
    try {
      const result = await revealAccountNumber(account.id).unwrap();
      setFullNumber(result.accountNumber);
      // Purge the unmasked number from the RTK Query store the instant we have it: it must live
      // only in this component's transient state (ASVS 14.3.1), never in Redux.
      reset();
      clearTimeout(hideTimer.current);
      hideTimer.current = setTimeout(() => conceal('hiddenAgain'), REVEAL_TIMEOUT_MS);
    } catch (error) {
      // A cancelled PIN step-up is a benign no-op — stay masked, no toast. Anything else is real.
      const problem = error as ApiProblem;
      if (problem?.errorCode !== 'STEP_UP_CANCELLED') {
        showProblem(problem);
      }
    }
  };

  const copy = async () => {
    if (fullNumber === null) return;
    setNotice(null);
    clearTimeout(copiedTimer.current);
    copyEpoch.current += 1;
    const thisCopy = copyEpoch.current;
    try {
      await navigator.clipboard.writeText(fullNumber);
      if (copyEpoch.current !== thisCopy) return;
      setNotice('copied');
      copiedTimer.current = setTimeout(() => setNotice(null), COPIED_TIMEOUT_MS);
    } catch {
      // Clipboard blocked (permissions / insecure context): the number is still on screen to copy
      // by hand, so fail silently rather than surface a scary error.
    }
  };

  return (
    <div className={styles.row}>
      {/* Masked, the words beside the bullets are read in their place, and the bullets are hidden
          from assistive technology. Words and not an aria-label: ARIA allows no name on a span
          with no role, as this one is, and under such a label the bullets stayed in the text a
          screen reader goes through. Revealed, the digits are the text, with no label over
          them. */}
      <Text className={styles.number}>
        {revealed ? (
          fullNumber
        ) : (
          <>
            <span aria-hidden="true">{maskAccountNumber(account.accountNumber)}</span>
            <span className={styles.spoken}>{`Account ending in ${lastTwo}`}</span>
          </>
        )}
      </Text>

      {/* One button through the wait, with the spinner in it: while it waits it says so, takes no
          press and keeps focus. The PIN dialog a reveal can bring up gives focus back to the
          control that had it when the dialog opened (`useReturnFocus`), and a spinner in the
          button's place took the button, and focus with it, off the page before the dialog came.
          (Until 2026-10-06 the spinner took the button's place while the reveal waited.)

          The spinner is hidden from assistive technology: it is the button's picture, and the
          button's name is what says the wait. Left exposed it is a progress bar with no name of
          its own, where the spinner it replaced had one: axe reported it in this state
          (`aria-progressbar-name`, serious; Chromium, 2026-10-06), and with it hidden, nothing. */}
      <div className={styles.buttons}>
        <Button
          appearance="subtle"
          size="small"
          icon={
            isLoading ? (
              <Spinner size="tiny" aria-hidden="true" />
            ) : revealed ? (
              <EyeOff16Regular />
            ) : (
              <Eye16Regular />
            )
          }
          // The name says the state; aria-pressed as well read "Hide account number …, pressed".
          aria-label={
            isLoading
              ? `Revealing account number for ${account.name}`
              : revealed
                ? `Hide account number for ${account.name}`
                : `Reveal full account number for ${account.name}`
          }
          disabledFocusable={isLoading}
          onClick={revealed ? hide : reveal}
        />

        {revealed && (
          <Button
            appearance="subtle"
            size="small"
            icon={<Copy16Regular />}
            aria-label={`Copy account number for ${account.name}`}
            onClick={copy}
          />
        )}
      </div>

      {/* One status for the row, there from the first render. "Copied" is on screen for two
          seconds. The re-hide sentence uses the clipped style, and only the timer says it:
          Reveal, Copy and Hide clear it. Empty, it is out of the flow. */}
      <Text role="status" className={notice === 'copied' ? styles.copied : styles.spoken}>
        {notice === 'copied'
          ? 'Copied'
          : notice === 'hiddenAgain'
            ? 'Account number hidden again.'
            : null}
      </Text>
    </div>
  );
}
