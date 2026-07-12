import { makeStyles, mergeClasses } from '@fluentui/react-components';
import { colors, transitions } from '../../theme/tokens';

// ============================================
// TYPES
// ============================================

export type BadgeVariant = 'primary' | 'success' | 'error' | 'warning' | 'info' | 'neutral';
export type BadgeSize = 'small' | 'medium';

export interface BadgeProps {
  /** Color variant */
  variant?: BadgeVariant;
  /** Size variant */
  size?: BadgeSize;
  /** Show status dot before text */
  dot?: boolean;
  /** Icon to show before text */
  icon?: React.ReactNode;
  /** Badge content */
  children: React.ReactNode;
  /** Additional CSS class */
  className?: string;
}

// ============================================
// STYLES
// ============================================

const useStyles = makeStyles({
  base: {
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    gap: '6px',
    fontWeight: 500,
    whiteSpace: 'nowrap',
    transition: `all ${transitions.fast}`,
  },

  // Sizes
  small: {
    padding: '4px 8px',
    fontSize: '11px',
    borderRadius: '10px',
  },
  medium: {
    padding: '4px 10px',
    fontSize: '12px',
    borderRadius: '12px',
  },

  // Variants
  primary: {
    backgroundColor: colors.brand[120],
    color: colors.brand[60],
  },
  success: {
    backgroundColor: colors.semantic.success.light,
    color: colors.semantic.success.dark,
  },
  error: {
    backgroundColor: colors.semantic.error.light,
    color: colors.semantic.error.dark,
  },
  warning: {
    backgroundColor: colors.semantic.warning.light,
    color: colors.semantic.warning.dark,
  },
  info: {
    backgroundColor: colors.semantic.info.light,
    color: colors.semantic.info.dark,
  },
  neutral: {
    backgroundColor: colors.neutral[100],
    color: colors.neutral[500],
  },

  // Status dot
  dot: {
    width: '8px',
    height: '8px',
    borderRadius: '50%',
    flexShrink: 0,
  },
  dotPrimary: {
    backgroundColor: colors.brand[60],
  },
  dotSuccess: {
    backgroundColor: colors.semantic.success.main,
  },
  dotError: {
    backgroundColor: colors.semantic.error.main,
  },
  dotWarning: {
    backgroundColor: colors.semantic.warning.main,
  },
  dotInfo: {
    backgroundColor: colors.semantic.info.main,
  },
  dotNeutral: {
    backgroundColor: colors.neutral[400],
  },

  // Icon container
  icon: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    fontSize: '12px',
  },
});

// ============================================
// COMPONENT
// ============================================

export function Badge({
  variant = 'neutral',
  size = 'medium',
  dot = false,
  icon,
  children,
  className,
}: BadgeProps) {
  const styles = useStyles();

  // Get dot color class based on variant
  const dotColorClass = {
    primary: styles.dotPrimary,
    success: styles.dotSuccess,
    error: styles.dotError,
    warning: styles.dotWarning,
    info: styles.dotInfo,
    neutral: styles.dotNeutral,
  }[variant];

  return (
    <span
      className={mergeClasses(
        styles.base,
        styles[size],
        styles[variant],
        className
      )}
    >
      {dot && <span className={mergeClasses(styles.dot, dotColorClass)} />}
      {icon && <span className={styles.icon}>{icon}</span>}
      {children}
    </span>
  );
}

export default Badge;
