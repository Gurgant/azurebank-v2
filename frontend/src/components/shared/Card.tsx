import { makeStyles, mergeClasses } from '@fluentui/react-components';
import { colors, shadows, componentSizes, gradients, transitions } from '../../theme/tokens';

// ============================================
// TYPES
// ============================================

export type CardVariant = 'default' | 'elevated' | 'outlined' | 'gradient';
export type CardPadding = 'none' | 'small' | 'medium' | 'large';

export interface CardProps {
  /** Visual style variant */
  variant?: CardVariant;
  /** Internal padding */
  padding?: CardPadding;
  /** Take full width of container */
  fullWidth?: boolean;
  /** Clickable card */
  onClick?: () => void;
  /** Additional CSS class */
  className?: string;
  /** Card content */
  children: React.ReactNode;
}

// ============================================
// STYLES
// ============================================

const useStyles = makeStyles({
  base: {
    backgroundColor: colors.neutral[50],
    borderRadius: componentSizes.card.radius,
    transition: `all ${transitions.normal}`,
  },

  // Variants
  default: {
    backgroundColor: '#FFFFFF',
    boxShadow: shadows.lg,
  },
  elevated: {
    backgroundColor: '#FFFFFF',
    boxShadow: shadows.xl,
  },
  outlined: {
    backgroundColor: '#FFFFFF',
    border: `1px solid ${colors.neutral[200]}`,
    boxShadow: 'none',
  },
  gradient: {
    background: gradients.brand,
    boxShadow: '0px 8px 24px rgba(0, 109, 226, 0.3)',
    color: '#FFFFFF',
  },

  // Padding
  paddingNone: {
    padding: 0,
  },
  paddingSmall: {
    padding: '16px',
    '@media (min-width: 1024px)': {
      padding: '20px',
    },
  },
  paddingMedium: {
    padding: '24px',
    '@media (min-width: 1024px)': {
      padding: '32px',
    },
  },
  paddingLarge: {
    padding: '24px',
    '@media (min-width: 1024px)': {
      padding: '48px',
    },
  },

  // Full width
  fullWidth: {
    width: '100%',
  },

  // Clickable
  clickable: {
    cursor: 'pointer',
    ':hover': {
      transform: 'translateY(-2px)',
      boxShadow: shadows.xl,
    },
    ':active': {
      transform: 'translateY(0)',
    },
  },

  // Outlined clickable hover
  outlinedClickable: {
    ':hover': {
      border: `1px solid ${colors.brand[60]}`,
      backgroundColor: colors.brand[130],
    },
  },
});

// ============================================
// COMPONENT
// ============================================

export function Card({
  variant = 'default',
  padding = 'medium',
  fullWidth = false,
  onClick,
  className,
  children,
}: CardProps) {
  const styles = useStyles();

  const paddingClass = {
    none: styles.paddingNone,
    small: styles.paddingSmall,
    medium: styles.paddingMedium,
    large: styles.paddingLarge,
  }[padding];

  const isClickable = !!onClick;
  const isOutlinedClickable = isClickable && variant === 'outlined';

  return (
    <div
      className={mergeClasses(
        styles.base,
        styles[variant],
        paddingClass,
        fullWidth && styles.fullWidth,
        isClickable && styles.clickable,
        isOutlinedClickable && styles.outlinedClickable,
        className,
      )}
      onClick={onClick}
      role={isClickable ? 'button' : undefined}
      tabIndex={isClickable ? 0 : undefined}
      onKeyDown={
        isClickable
          ? (e) => {
              if (e.key === 'Enter' || e.key === ' ') {
                e.preventDefault();
                onClick?.();
              }
            }
          : undefined
      }
    >
      {children}
    </div>
  );
}

export default Card;
