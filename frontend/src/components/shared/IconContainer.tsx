import { makeStyles, mergeClasses } from '@fluentui/react-components';
import { colors, componentSizes, gradients } from '../../theme/tokens';

// ============================================
// TYPES
// ============================================

export type IconContainerVariant =
  | 'deposit'
  | 'withdrawal'
  | 'transfer-out'
  | 'transfer-in'
  | 'primary'
  | 'neutral';

export type IconContainerSize = 'sm' | 'md' | 'lg';

export interface IconContainerProps {
  /** Color variant based on transaction type */
  variant: IconContainerVariant;
  /** Size variant */
  size?: IconContainerSize;
  /** Use gradient background */
  gradient?: boolean;
  /** Icon element */
  children: React.ReactNode;
  /** Additional CSS class */
  className?: string;
}

// ============================================
// STYLES
// ============================================

const useStyles = makeStyles({
  base: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    flexShrink: 0,
  },

  // Sizes
  sm: {
    width: componentSizes.iconContainer.sm.size,
    height: componentSizes.iconContainer.sm.size,
    borderRadius: componentSizes.iconContainer.sm.radius,
    fontSize: componentSizes.iconContainer.sm.iconSize,
    '& svg': {
      width: componentSizes.iconContainer.sm.iconSize,
      height: componentSizes.iconContainer.sm.iconSize,
    },
  },
  md: {
    width: componentSizes.iconContainer.md.size,
    height: componentSizes.iconContainer.md.size,
    borderRadius: componentSizes.iconContainer.md.radius,
    fontSize: componentSizes.iconContainer.md.iconSize,
    '& svg': {
      width: componentSizes.iconContainer.md.iconSize,
      height: componentSizes.iconContainer.md.iconSize,
    },
  },
  lg: {
    width: componentSizes.iconContainer.lg.size,
    height: componentSizes.iconContainer.lg.size,
    borderRadius: componentSizes.iconContainer.lg.radius,
    fontSize: componentSizes.iconContainer.lg.iconSize,
    '& svg': {
      width: componentSizes.iconContainer.lg.iconSize,
      height: componentSizes.iconContainer.lg.iconSize,
    },
  },

  // Variants - solid backgrounds
  deposit: {
    backgroundColor: colors.transaction.deposit.background,
    color: colors.transaction.deposit.icon,
  },
  withdrawal: {
    backgroundColor: colors.transaction.withdrawal.background,
    color: colors.transaction.withdrawal.icon,
  },
  'transfer-out': {
    backgroundColor: colors.transaction.transferOut.background,
    color: colors.transaction.transferOut.icon,
  },
  'transfer-in': {
    backgroundColor: colors.transaction.transferIn.background,
    color: colors.transaction.transferIn.icon,
  },
  primary: {
    backgroundColor: colors.brand[120],
    color: colors.brand[60],
  },
  neutral: {
    backgroundColor: colors.neutral[100],
    color: colors.neutral[500],
  },

  // Gradient variants
  depositGradient: {
    background: gradients.success,
  },
  withdrawalGradient: {
    background: gradients.error,
  },
  'transfer-outGradient': {
    background: gradients.warning,
  },
  'transfer-inGradient': {
    background: gradients.info,
  },
  primaryGradient: {
    background: gradients.primary,
  },
  neutralGradient: {
    background: `linear-gradient(135deg, ${colors.neutral[100]} 0%, ${colors.neutral[200]} 100%)`,
  },
});

// ============================================
// COMPONENT
// ============================================

export function IconContainer({
  variant,
  size = 'md',
  gradient = false,
  children,
  className,
}: IconContainerProps) {
  const styles = useStyles();

  // Get gradient class if needed
  const gradientClass = gradient
    ? styles[`${variant}Gradient` as keyof typeof styles]
    : undefined;

  return (
    <div
      className={mergeClasses(
        styles.base,
        styles[size],
        styles[variant],
        gradientClass,
        className
      )}
      aria-hidden="true"
    >
      {children}
    </div>
  );
}

export default IconContainer;
