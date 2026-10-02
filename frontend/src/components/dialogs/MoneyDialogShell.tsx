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
 *
 * **A press outside the dialog does not close it while `keepOnOutsidePress` is set.** The caller
 * sets it while the form that sent is replaced by words the visitor must read: that the payment
 * went through and its receipt cannot be shown, or that it could not be confirmed and is to be
 * checked first. Either view is shorter than the form, so the dialog shrinks under the pointer,
 * and the second press of a double click on the send button lands on the backdrop. Measured in
 * Chromium on the running stack, on each: the dialog was gone before anybody could read it, on
 * the first a tenth of a second after its sentence appeared, on the second with the tile that
 * opens the dialog again in front of the visitor. The press changes nothing then: it closes
 * nothing, and it leaves focus where it was. The X still closes the dialog, and so does Escape
 * from wherever in the dialog focus is, since nobody presses those by accident. The receipt of a
 * send that succeeded is not kept, and neither is the form: a press outside closes those as it
 * did. (Until 2026-10-02 only the first of the two views was kept.)
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
  /**
   * True while the went-through view or the check view stands in place of the form: a press on the
   * backdrop then closes nothing.
   */
  keepOnOutsidePress?: boolean;
  children: ReactNode;
}

export function MoneyDialogShell({
  open,
  title,
  icon,
  tone,
  onClose,
  closeDisabled = false,
  keepOnOutsidePress = false,
  children,
}: MoneyDialogShellProps) {
  const styles = useMoneyDialogStyles();

  return (
    <Dialog
      open={open}
      modalType="modal"
      onOpenChange={(_event, data) => {
        if (data.open) return;
        if (keepOnOutsidePress && data.type === 'backdropClick') return;
        onClose();
      }}
    >
      <DialogSurface
        className={styles.surface}
        aria-label={title}
        aria-describedby={undefined}
        // The press that closes nothing must take nothing either. A mouse press on the backdrop
        // moves focus to `body`, and from there Escape reaches no dialog: measured in Chromium,
        // the view stayed and Escape no longer closed it. Refusing the press's default keeps focus
        // where it was: on the went-through sentence, or on whatever the visitor had reached in
        // the check view, which lands no focus of its own.
        backdrop={
          keepOnOutsidePress ? { onMouseDown: (event) => event.preventDefault() } : undefined
        }
      >
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
            // Convention, not a live fix. A bare <button> is type="submit", but MEASURED here it
            // can never act as one: Fluent portals DialogSurface to document.body, so even with a
            // caller wrapping the whole shell in a <form> this button reports no form owner
            // (`close.form === null`, `closest('form') === null`). Kept because it costs nothing
            // and states the intent — NOT because deleting it breaks anything, and deliberately
            // not defended by a test, since any such test would pass with it removed.
            type="button"
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
