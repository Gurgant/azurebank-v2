import type { ReactNode } from 'react';
import { Dialog, DialogSurface, mergeClasses } from '@fluentui/react-components';
import { Dismiss24Regular } from '@fluentui/react-icons';
import { useMoneyDialogStyles } from './moneyDialogStyles';

/**
 * The frame a money dialog draws around itself: the surface, the header, the one way out.
 *
 * Deposit and withdraw had this twice, character for character apart from the title, the glyph and
 * the icon's colour. Extracting it is worth more than the thirty lines it saves, because the header
 * is where the two invariants live that a copy can silently lose.
 *
 * **The title is the accessible name.** Both dialogs set `aria-label` to the same string they
 * render, which is right and which two copies can drift apart the moment one title is reworded. It
 * is one value here.
 *
 * **The close button honours `closeDisabled`, and it must.** In both dialogs that flag is
 * `keyLive` — the anti-double-spend guard. Closing while an idempotency key is in flight abandons
 * a request whose result the user still needs to see, so the exit is barred deliberately. A shell
 * that decided for itself when it could be dismissed would walk straight through that, and nothing
 * would fail until somebody's money moved twice.
 *
 * `modalType="modal"` is likewise not decoration: it is what makes Fluent trap focus and bind
 * Escape. `onOpenChange` routes BOTH of those back through the caller's own close handler rather
 * than closing the surface directly, so the guard applies to the keyboard too.
 */

export interface MoneyDialogShellProps {
  open: boolean;
  /** Rendered in the header AND used as the surface's accessible name — one string, not two. */
  title: string;
  icon: ReactNode;
  /** `credit` is money arriving, `debit` money leaving. The only thing the two dialogs disagreed on. */
  tone: 'credit' | 'debit';
  /** The caller's own close path, so its guards apply to the button, Escape and the backdrop alike. */
  onClose: () => void;
  /** True while an idempotency key is live. Bars every exit, and looks barred. */
  closeDisabled?: boolean;
  children: ReactNode;
}

export function MoneyDialogShell({
  open,
  title,
  icon,
  tone,
  onClose,
  closeDisabled = false,
  children,
}: MoneyDialogShellProps) {
  const styles = useMoneyDialogStyles();

  return (
    <Dialog
      open={open}
      modalType="modal"
      onOpenChange={(_event, data) => {
        if (!data.open) {
          onClose();
        }
      }}
    >
      <DialogSurface className={styles.surface} aria-label={title} aria-describedby={undefined}>
        <div className={styles.header}>
          <div className={styles.headerTitle}>
            <div
              className={mergeClasses(
                styles.headerIcon,
                tone === 'credit' ? styles.headerIconCredit : styles.headerIconDebit,
              )}
            >
              {icon}
            </div>
            {title}
          </div>
          <button
            className={styles.closeButton}
            aria-label="Close"
            onClick={onClose}
            disabled={closeDisabled}
          >
            <Dismiss24Regular />
          </button>
        </div>

        {children}
      </DialogSurface>
    </Dialog>
  );
}

export default MoneyDialogShell;
