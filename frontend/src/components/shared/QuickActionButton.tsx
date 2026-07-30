import { makeStyles, mergeClasses, Text, tokens } from '@fluentui/react-components';
import { colors, shadows, transitions } from '../../theme/tokens';

// ============================================
// TYPES
// ============================================

export type QuickActionVariant = 'deposit' | 'withdraw' | 'transfer' | 'default';

export interface QuickActionButtonProps {
  /** Visual style variant */
  variant: QuickActionVariant;
  /** Button label */
  label: string;
  /** Icon to display */
  icon: React.ReactNode;
  /** Click handler */
  onClick: () => void;
  /** Highlighted/primary action */
  highlighted?: boolean;
  /** Disabled state */
  disabled?: boolean;
  /** Additional CSS class */
  className?: string;
}

// ============================================
// STYLES
// ============================================

const useStyles = makeStyles({
  button: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    gap: '8px',
    flex: 1,
    height: '80px',
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: '12px',
    border: 'none',
    cursor: 'pointer',
    transition: `all ${transitions.normal}`,
    boxShadow: shadows.sm,

    ':hover': {
      transform: 'translateY(-2px)',
      boxShadow: shadows.md,
    },
    ':active': {
      transform: 'translateY(0)',
    },
    ':disabled': {
      opacity: 0.5,
      cursor: 'not-allowed',
      transform: 'none',
    },
  },

  // Highlighted (primary action)
  highlighted: {
    backgroundColor: colors.brandFill.rest,
    ':hover': {
      backgroundColor: colors.brandFill.hover,
    },
    ':active': {
      backgroundColor: colors.brandFill.pressed,
    },
  },

  // Icon container
  iconContainer: {
    width: '40px',
    height: '40px',
    borderRadius: '10px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    '& svg': {
      width: '24px',
      height: '24px',
    },
  },

  // Icon variants
  iconDeposit: {
    backgroundColor: colors.semantic.success.light,
    color: colors.semantic.success.main,
  },
  iconWithdraw: {
    backgroundColor: colors.semantic.error.light,
    color: colors.semantic.error.main,
  },
  iconTransfer: {
    backgroundColor: colors.brand[120],
    color: colors.brand[60],
  },
  iconDefault: {
    backgroundColor: colors.neutral[100],
    color: colors.neutral[600],
  },

  // Highlighted icon. Was `rgba(255,255,255,0.2)`, one twentieth apart from the auth panel's plate
  // for no recorded reason; `onBrandPlate` is that role, named once.
  iconHighlighted: {
    backgroundColor: colors.onBrandPlate,
    color: tokens.colorNeutralForegroundOnBrand,
  },

  // Label
  label: {
    fontSize: '12px',
    fontWeight: 500,
    color: colors.neutral[700],
  },
  labelHighlighted: {
    color: tokens.colorNeutralForegroundOnBrand,
  },
});

// ============================================
// COMPONENT
// ============================================

export function QuickActionButton({
  variant,
  label,
  icon,
  onClick,
  highlighted = false,
  disabled = false,
  className,
}: QuickActionButtonProps) {
  const styles = useStyles();

  const iconVariantClass = {
    deposit: styles.iconDeposit,
    withdraw: styles.iconWithdraw,
    transfer: styles.iconTransfer,
    default: styles.iconDefault,
  }[variant];

  return (
    <button
      className={mergeClasses(styles.button, highlighted && styles.highlighted, className)}
      onClick={onClick}
      disabled={disabled}
      type="button"
    >
      <div
        className={mergeClasses(
          styles.iconContainer,
          highlighted ? styles.iconHighlighted : iconVariantClass,
        )}
      >
        {icon}
      </div>
      <Text className={mergeClasses(styles.label, highlighted && styles.labelHighlighted)}>
        {label}
      </Text>
    </button>
  );
}

export default QuickActionButton;
