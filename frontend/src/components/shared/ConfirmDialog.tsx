import { useEffect, useRef } from 'react';
import { makeStyles, mergeClasses, Text, Button, tokens } from '@fluentui/react-components';
import { Warning24Regular, Dismiss24Regular } from '@fluentui/react-icons';
import { colors, safeArea, zIndex, transitions, shadows } from '../../theme/tokens';

// ============================================
// TYPES
// ============================================

export interface ConfirmDialogProps {
  /** Whether the dialog is open */
  isOpen: boolean;
  /** Close handler */
  onClose: () => void;
  /** Confirm handler */
  onConfirm: () => void;
  /** Dialog title */
  title: string;
  /** Dialog message */
  message: string;
  /** Confirm button text */
  confirmText?: string;
  /** Cancel button text */
  cancelText?: string;
  /** Dialog variant */
  variant?: 'default' | 'danger';
  /** Loading state for confirm button */
  isLoading?: boolean;
  /**
   * Inline error shown under the message (D17: business-rule failures render at the
   * owning surface — the dialog stays open so the user reads WHY it was refused).
   */
  errorText?: string | null;
}

// ============================================
// STYLES
// ============================================

const useStyles = makeStyles({
  /**
   * The scrim, and this one was not merely un-tokenised — it disagreed.
   *
   * Six dialogs in the app use Fluent's `<Dialog>` and get `colorBackgroundOverlay`, which is
   * `rgba(0,0,0,0.4)` in light and `rgba(0,0,0,0.5)` in dark. This one is hand-rolled and hardcoded
   * 0.5 for both, so in LIGHT mode the destructive confirmation — delete an account — dimmed the
   * page harder than every other dialog in the app, and nothing said why. In dark the two happened
   * to coincide, which is why it never looked like a bug.
   */
  overlay: {
    position: 'fixed',
    inset: 0,
    backgroundColor: tokens.colorBackgroundOverlay,
    zIndex: zIndex.modal,
    opacity: 0,
    visibility: 'hidden',
    transition: `opacity ${transitions.normal}, visibility ${transitions.normal}`,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    // `max`, not `+`: this padding is a minimum gap, so on a device with no cutout it stays the
    // 16px it always was, and on one with a cutout it grows only on the side that has it. Adding
    // the inset instead would push the dialog 16px further in than intended on every edge.
    paddingTop: `max(16px, ${safeArea.top})`,
    paddingBottom: `max(16px, ${safeArea.bottom})`,
    paddingLeft: `max(16px, ${safeArea.left})`,
    paddingRight: `max(16px, ${safeArea.right})`,
  },
  overlayOpen: {
    opacity: 1,
    visibility: 'visible',
  },

  dialog: {
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: '16px',
    width: '100%',
    maxWidth: '340px',
    boxShadow: shadows.xl,
    transform: 'scale(0.95)',
    opacity: 0,
    transition: `transform ${transitions.normal}, opacity ${transitions.normal}`,
    overflow: 'hidden',
  },
  dialogOpen: {
    transform: 'scale(1)',
    opacity: 1,
  },

  header: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    padding: '20px 20px 0',
  },

  iconContainer: {
    width: '48px',
    height: '48px',
    borderRadius: '12px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    flexShrink: 0,
  },
  iconDefault: {
    backgroundColor: colors.brand[120],
    color: colors.brand[60],
  },
  iconDanger: {
    backgroundColor: colors.semantic.error.light,
    color: colors.semantic.error.main,
  },

  closeButton: {
    width: '32px',
    height: '32px',
    borderRadius: '50%',
    backgroundColor: 'transparent',
    border: 'none',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    cursor: 'pointer',
    color: colors.neutral[500],
    transition: `all ${transitions.fast}`,
    flexShrink: 0,
    ':hover': {
      backgroundColor: colors.neutral[100],
      color: colors.neutral[700],
    },
  },

  content: {
    padding: '16px 20px 24px',
  },

  title: {
    fontSize: '18px',
    fontWeight: 600,
    color: colors.neutral[800],
    marginBottom: '8px',
  },

  message: {
    fontSize: '14px',
    color: colors.neutral[600],
    lineHeight: '1.5',
  },

  errorText: {
    display: 'block',
    marginTop: '12px',
    fontSize: '13px',
    fontWeight: 500,
    color: colors.semantic.error.main,
  },

  footer: {
    display: 'flex',
    gap: '12px',
    padding: '0 20px 20px',
  },

  button: {
    flex: 1,
    height: '44px',
    borderRadius: '10px',
    fontWeight: 500,
    fontSize: '15px',
  },

  cancelButton: {
    backgroundColor: colors.neutral[100],
    color: colors.neutral[700],
    border: 'none',
    ':hover': {
      backgroundColor: colors.neutral[200],
    },
  },

  confirmButtonDefault: {
    backgroundColor: colors.brandFill.rest,
    color: tokens.colorNeutralForegroundOnBrand,
    border: 'none',
    ':hover': {
      backgroundColor: colors.brandFill.hover,
    },
    ':active': {
      backgroundColor: colors.brandFill.pressed,
    },
  },

  confirmButtonDanger: {
    backgroundColor: colors.semantic.error.main,
    color: tokens.colorNeutralForegroundStaticInverted,
    border: 'none',
    ':hover': {
      backgroundColor: colors.semantic.error.dark,
    },
  },
});

// ============================================
// COMPONENT
// ============================================

/**
 * Focusable descendants, in tab order.
 *
 * `:not([disabled])` matters more here than it looks: `isLoading` disables the close, cancel and
 * confirm buttons at once, so this legitimately returns an EMPTY list and both callers have to cope
 * with that rather than index into it.
 *
 * Shared by the open-effect and the Tab trap on purpose — two copies of this selector would drift,
 * and the failure mode of drift is a trap that cycles through a different set than the one focus
 * actually visits.
 */
const FOCUSABLE_SELECTOR =
  'button:not([disabled]), [href], input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

function focusableWithin(root: HTMLElement): HTMLElement[] {
  return Array.from(root.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR));
}

export function ConfirmDialog({
  isOpen,
  onClose,
  onConfirm,
  title,
  message,
  confirmText = 'Confirm',
  cancelText = 'Cancel',
  variant = 'default',
  isLoading = false,
  errorText = null,
}: ConfirmDialogProps) {
  const styles = useStyles();
  const dialogRef = useRef<HTMLDivElement>(null);
  const previousActiveElement = useRef<HTMLElement | null>(null);

  // Handle escape key
  useEffect(() => {
    const handleEscape = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && isOpen && !isLoading) {
        onClose();
      }
    };

    document.addEventListener('keydown', handleEscape);
    return () => document.removeEventListener('keydown', handleEscape);
  }, [isOpen, onClose, isLoading]);

  // Prevent body scroll when open
  useEffect(() => {
    if (isOpen) {
      previousActiveElement.current = document.activeElement as HTMLElement;
      document.body.style.overflow = 'hidden';
    } else {
      document.body.style.overflow = '';
      previousActiveElement.current?.focus();
    }
    return () => {
      document.body.style.overflow = '';
    };
  }, [isOpen]);

  // Move focus IN on open. The dialog element itself is the fallback: while `isLoading` every
  // control is disabled, so there is nothing focusable inside and focus would otherwise stay on
  // whatever is behind — which is also how Tab escaped, since a keydown outside this subtree never
  // reaches the handler below.
  useEffect(() => {
    if (isOpen && dialogRef.current) {
      (focusableWithin(dialogRef.current)[0] ?? dialogRef.current).focus();
    }
  }, [isOpen]);

  /*
    CONTAIN Tab, which is the half that was missing.

    The element declares `role="alertdialog"` and `aria-modal="true"` — a promise that the rest of
    the page is unreachable — and the effect above only ever moved focus in ONCE. Nothing watched
    Tab, so focus walked straight out into the page behind, which for this dialog is the delete
    confirmation or one of the two transfer confirmations: a money surface with live controls.

    Every other dialog in the app is a Fluent `Dialog` and gets this from tabster. This one is
    hand-rolled — deliberately, for the scrim and safe-area behaviour documented in the styles — so
    it has to do the containment itself.

    Three cases, and the third is the one a naive first/last implementation gets wrong:
      - on the last element going forward, or the first going backward, wrap round;
      - on the dialog container itself (tabIndex -1, so reachable only programmatically), send Tab
        to whichever end the direction calls for, rather than letting the browser step OUT of the
        subtree backwards;
      - with NO focusable children at all — `isLoading` disables all three buttons at once — there
        is nothing to cycle to, so simply refuse the keystroke and leave focus on the container.
  */
  const handleKeyDown = (event: React.KeyboardEvent<HTMLDivElement>) => {
    if (event.key !== 'Tab' || !dialogRef.current) return;

    const focusable = focusableWithin(dialogRef.current);
    if (focusable.length === 0) {
      event.preventDefault();
      return;
    }

    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    const active = document.activeElement;
    const onContainer = active === dialogRef.current;

    if (event.shiftKey && (active === first || onContainer)) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && (active === last || onContainer)) {
      event.preventDefault();
      first.focus();
    }
  };

  const handleOverlayClick = (e: React.MouseEvent) => {
    if (e.target === e.currentTarget && !isLoading) {
      onClose();
    }
  };

  return (
    <div
      className={mergeClasses(styles.overlay, isOpen && styles.overlayOpen)}
      onClick={handleOverlayClick}
      aria-hidden={!isOpen}
    >
      <div
        ref={dialogRef}
        className={mergeClasses(styles.dialog, isOpen && styles.dialogOpen)}
        role="alertdialog"
        aria-modal="true"
        aria-labelledby="confirm-dialog-title"
        aria-describedby="confirm-dialog-message"
        // -1, so it is focusable programmatically but never lands in the tab order itself.
        tabIndex={-1}
        onKeyDown={handleKeyDown}
      >
        {/* Header */}
        <div className={styles.header}>
          <div
            className={mergeClasses(
              styles.iconContainer,
              variant === 'danger' ? styles.iconDanger : styles.iconDefault,
            )}
          >
            <Warning24Regular />
          </div>
          <button
            className={styles.closeButton}
            onClick={onClose}
            aria-label="Close"
            type="button"
            disabled={isLoading}
          >
            <Dismiss24Regular />
          </button>
        </div>

        {/* Content */}
        <div className={styles.content}>
          <Text id="confirm-dialog-title" as="h2" className={styles.title}>
            {title}
          </Text>
          <Text id="confirm-dialog-message" className={styles.message}>
            {message}
          </Text>
          {errorText && (
            <Text role="alert" className={styles.errorText}>
              {errorText}
            </Text>
          )}
        </div>

        {/* Footer */}
        <div className={styles.footer}>
          <Button
            className={mergeClasses(styles.button, styles.cancelButton)}
            onClick={onClose}
            disabled={isLoading}
          >
            {cancelText}
          </Button>
          <Button
            className={mergeClasses(
              styles.button,
              variant === 'danger' ? styles.confirmButtonDanger : styles.confirmButtonDefault,
            )}
            onClick={onConfirm}
            disabled={isLoading}
          >
            {isLoading ? 'Loading...' : confirmText}
          </Button>
        </div>
      </div>
    </div>
  );
}

export default ConfirmDialog;
