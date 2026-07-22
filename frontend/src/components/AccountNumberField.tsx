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
  row: {
    display: 'flex',
    alignItems: 'center',
    gap: '4px',
  },
  number: {
    fontSize: '13px',
    fontWeight: 400,
    fontFamily: 'Consolas, "Courier New", monospace',
    color: colors.neutral[500],
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
  const [copied, setCopied] = useState(false);

  const hideTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const copiedTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);

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

  const hide = () => {
    clearTimeout(hideTimer.current);
    clearTimeout(copiedTimer.current);
    setFullNumber(null);
    setCopied(false);
    reset();
  };

  const reveal = async () => {
    try {
      const result = await revealAccountNumber(account.id).unwrap();
      setFullNumber(result.accountNumber);
      // Purge the unmasked number from the RTK Query store the instant we have it: it must live
      // only in this component's transient state (ASVS 14.3.1), never in Redux.
      reset();
      clearTimeout(hideTimer.current);
      hideTimer.current = setTimeout(hide, REVEAL_TIMEOUT_MS);
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
    try {
      await navigator.clipboard.writeText(fullNumber);
      setCopied(true);
      clearTimeout(copiedTimer.current);
      copiedTimer.current = setTimeout(() => setCopied(false), COPIED_TIMEOUT_MS);
    } catch {
      // Clipboard blocked (permissions / insecure context): the number is still on screen to copy
      // by hand, so fail silently rather than surface a scary error.
    }
  };

  return (
    <div className={styles.row}>
      <Text
        className={styles.number}
        // When masked, label the bullets so a screen reader says something useful; when revealed,
        // NO label — an aria-label would override the text node and hide the actual digits from AT.
        aria-label={revealed ? undefined : `Account ending in ${lastTwo}`}
      >
        {revealed ? fullNumber : maskAccountNumber(account.accountNumber)}
      </Text>

      {isLoading ? (
        <Spinner size="tiny" aria-label={`Revealing account number for ${account.name}`} />
      ) : (
        <Button
          appearance="subtle"
          size="small"
          icon={revealed ? <EyeOff16Regular /> : <Eye16Regular />}
          aria-pressed={revealed}
          aria-label={
            revealed
              ? `Hide account number for ${account.name}`
              : `Reveal full account number for ${account.name}`
          }
          onClick={revealed ? hide : reveal}
        />
      )}

      {revealed && (
        <Button
          appearance="subtle"
          size="small"
          icon={<Copy16Regular />}
          aria-label={`Copy account number for ${account.name}`}
          onClick={copy}
        />
      )}

      {copied && (
        <Text role="status" className={styles.copied}>
          Copied
        </Text>
      )}
    </div>
  );
}
