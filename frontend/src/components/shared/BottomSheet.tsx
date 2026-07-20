import { useEffect, useRef } from 'react';
import { makeStyles, mergeClasses, Text } from '@fluentui/react-components';
import { Dismiss24Regular } from '@fluentui/react-icons';
import { colors, zIndex, transitions, shadows } from '../../theme/tokens';

// ============================================
// TYPES
// ============================================

export interface BottomSheetProps {
  /** Whether the sheet is open */
  isOpen: boolean;
  /** Close handler */
  onClose: () => void;
  /** Sheet title */
  title?: string;
  /** Sheet subtitle */
  subtitle?: string;
  /** Show drag handle */
  showHandle?: boolean;
  /** Show close button */
  showCloseButton?: boolean;
  /** Sheet content */
  children: React.ReactNode;
  /** Footer content (buttons, etc.) */
  footer?: React.ReactNode;
  /** Additional CSS class for content */
  className?: string;
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
  },
  overlayOpen: {
    opacity: 1,
    visibility: 'visible',
  },

  sheet: {
    position: 'fixed',
    left: 0,
    right: 0,
    bottom: 0,
    backgroundColor: '#FFFFFF',
    borderRadius: '24px 24px 0 0',
    zIndex: zIndex.modal + 1,
    maxHeight: '90vh',
    display: 'flex',
    flexDirection: 'column',
    transform: 'translateY(100%)',
    transition: `transform ${transitions.slow}`,
    boxShadow: shadows.xl,
  },
  sheetOpen: {
    transform: 'translateY(0)',
  },

  handle: {
    width: '40px',
    height: '4px',
    backgroundColor: colors.neutral[300],
    borderRadius: '2px',
    margin: '12px auto 8px',
    flexShrink: 0,
  },

  header: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    padding: '8px 16px 16px',
    flexShrink: 0,
  },

  headerText: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
  },

  title: {
    fontSize: '20px',
    fontWeight: 600,
    color: colors.neutral[800],
  },

  subtitle: {
    fontSize: '14px',
    color: colors.neutral[500],
  },

  closeButton: {
    width: '32px',
    height: '32px',
    borderRadius: '50%',
    backgroundColor: colors.neutral[100],
    border: 'none',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    cursor: 'pointer',
    color: colors.neutral[600],
    transition: `all ${transitions.fast}`,
    flexShrink: 0,
    ':hover': {
      backgroundColor: colors.neutral[200],
    },
    ':focus-visible': {
      outline: `2px solid ${colors.brand[60]}`,
      outlineOffset: '2px',
    },
  },

  content: {
    flex: 1,
    overflowY: 'auto',
    padding: '0 16px 16px',
    '-webkit-overflow-scrolling': 'touch',
  },

  footer: {
    padding: '16px',
    paddingBottom: '32px', // Safe area padding
    borderTop: `1px solid ${colors.neutral[200]}`,
    flexShrink: 0,
    backgroundColor: '#FFFFFF',
  },
});

// ============================================
// COMPONENT
// ============================================

export function BottomSheet({
  isOpen,
  onClose,
  title,
  subtitle,
  showHandle = true,
  showCloseButton = true,
  children,
  footer,
  className,
}: BottomSheetProps) {
  const styles = useStyles();
  const sheetRef = useRef<HTMLDivElement>(null);

  // Handle escape key
  useEffect(() => {
    const handleEscape = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && isOpen) {
        onClose();
      }
    };

    document.addEventListener('keydown', handleEscape);
    return () => document.removeEventListener('keydown', handleEscape);
  }, [isOpen, onClose]);

  // Prevent body scroll when open
  useEffect(() => {
    if (isOpen) {
      document.body.style.overflow = 'hidden';
    } else {
      document.body.style.overflow = '';
    }
    return () => {
      document.body.style.overflow = '';
    };
  }, [isOpen]);

  // Focus trap
  useEffect(() => {
    if (isOpen && sheetRef.current) {
      const firstFocusable = sheetRef.current.querySelector<HTMLElement>(
        'button, [href], input, select, textarea, [tabindex]:not([tabindex="-1"])',
      );
      firstFocusable?.focus();
    }
  }, [isOpen]);

  return (
    <>
      {/* Overlay */}
      <div
        className={mergeClasses(styles.overlay, isOpen && styles.overlayOpen)}
        onClick={onClose}
        aria-hidden="true"
      />

      {/* Sheet */}
      <div
        ref={sheetRef}
        className={mergeClasses(styles.sheet, isOpen && styles.sheetOpen)}
        role="dialog"
        aria-modal="true"
        aria-labelledby={title ? 'bottom-sheet-title' : undefined}
      >
        {/* Handle */}
        {showHandle && <div className={styles.handle} />}

        {/* Header */}
        {(title || showCloseButton) && (
          <div className={styles.header}>
            <div className={styles.headerText}>
              {title && (
                <Text id="bottom-sheet-title" className={styles.title}>
                  {title}
                </Text>
              )}
              {subtitle && <Text className={styles.subtitle}>{subtitle}</Text>}
            </div>
            {showCloseButton && (
              <button
                className={styles.closeButton}
                onClick={onClose}
                aria-label="Close"
                type="button"
              >
                <Dismiss24Regular />
              </button>
            )}
          </div>
        )}

        {/* Content */}
        <div className={mergeClasses(styles.content, className)}>{children}</div>

        {/* Footer */}
        {footer && <div className={styles.footer}>{footer}</div>}
      </div>
    </>
  );
}

export default BottomSheet;
