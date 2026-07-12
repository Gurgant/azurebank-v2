import { useEffect, useRef } from 'react';
import { makeStyles, mergeClasses, Text, Button } from '@fluentui/react-components';
import { Warning24Regular, Dismiss24Regular } from '@fluentui/react-icons';
import { colors, zIndex, transitions, shadows } from '../../theme/tokens';

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
}

// ============================================
// STYLES
// ============================================

const useStyles = makeStyles({
  overlay: {
    position: 'fixed',
    inset: 0,
    backgroundColor: 'rgba(0, 0, 0, 0.5)',
    zIndex: zIndex.modal,
    opacity: 0,
    visibility: 'hidden',
    transition: `opacity ${transitions.normal}, visibility ${transitions.normal}`,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    padding: '16px',
  },
  overlayOpen: {
    opacity: 1,
    visibility: 'visible',
  },

  dialog: {
    backgroundColor: '#FFFFFF',
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
    backgroundColor: colors.brand[60],
    color: '#FFFFFF',
    border: 'none',
    ':hover': {
      backgroundColor: colors.brand[40],
    },
  },

  confirmButtonDanger: {
    backgroundColor: colors.semantic.error.main,
    color: '#FFFFFF',
    border: 'none',
    ':hover': {
      backgroundColor: '#C62828',
    },
  },
});

// ============================================
// COMPONENT
// ============================================

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

  // Focus trap
  useEffect(() => {
    if (isOpen && dialogRef.current) {
      const firstFocusable = dialogRef.current.querySelector<HTMLElement>(
        'button:not([disabled]), [href], input, select, textarea, [tabindex]:not([tabindex="-1"])'
      );
      firstFocusable?.focus();
    }
  }, [isOpen]);

  const handleOverlayClick = (e: React.MouseEvent) => {
    if (e.target === e.currentTarget && !isLoading) {
      onClose();
    }
  };

  return (
    <div
      className={mergeClasses(
        styles.overlay,
        isOpen && styles.overlayOpen
      )}
      onClick={handleOverlayClick}
      aria-hidden={!isOpen}
    >
      <div
        ref={dialogRef}
        className={mergeClasses(
          styles.dialog,
          isOpen && styles.dialogOpen
        )}
        role="alertdialog"
        aria-modal="true"
        aria-labelledby="confirm-dialog-title"
        aria-describedby="confirm-dialog-message"
      >
        {/* Header */}
        <div className={styles.header}>
          <div
            className={mergeClasses(
              styles.iconContainer,
              variant === 'danger' ? styles.iconDanger : styles.iconDefault
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
              variant === 'danger'
                ? styles.confirmButtonDanger
                : styles.confirmButtonDefault
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
