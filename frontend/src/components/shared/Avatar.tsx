import { makeStyles, mergeClasses } from '@fluentui/react-components';
import { colors, componentSizes } from '../../theme/tokens';

// ============================================
// TYPES
// ============================================

export type AvatarSize = 'sm' | 'md' | 'lg' | 'xl' | '2xl';
export type AvatarVariant = 'primary' | 'success' | 'warning' | 'info' | 'neutral';

export interface AvatarProps {
  /** Two-letter initials to display */
  initials?: string;
  /** Full name - will extract initials automatically */
  name?: string;
  /** Image URL */
  src?: string;
  /** Size variant */
  size?: AvatarSize;
  /** Color variant */
  variant?: AvatarVariant;
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
    borderRadius: '50%',
    fontWeight: 600,
    flexShrink: 0,
    overflow: 'hidden',
    userSelect: 'none',
  },

  // Sizes
  sm: {
    width: componentSizes.avatar.sm.size,
    height: componentSizes.avatar.sm.size,
    fontSize: componentSizes.avatar.sm.fontSize,
  },
  md: {
    width: componentSizes.avatar.md.size,
    height: componentSizes.avatar.md.size,
    fontSize: componentSizes.avatar.md.fontSize,
  },
  lg: {
    width: componentSizes.avatar.lg.size,
    height: componentSizes.avatar.lg.size,
    fontSize: componentSizes.avatar.lg.fontSize,
  },
  xl: {
    width: componentSizes.avatar.xl.size,
    height: componentSizes.avatar.xl.size,
    fontSize: componentSizes.avatar.xl.fontSize,
  },
  '2xl': {
    width: componentSizes.avatar['2xl'].size,
    height: componentSizes.avatar['2xl'].size,
    fontSize: componentSizes.avatar['2xl'].fontSize,
  },

  // Variants
  primary: {
    backgroundColor: colors.brand[120],
    color: colors.brand[60],
  },
  success: {
    backgroundColor: colors.semantic.success.light,
    color: colors.semantic.success.main,
  },
  warning: {
    backgroundColor: colors.semantic.warning.light,
    color: colors.semantic.warning.main,
  },
  info: {
    backgroundColor: colors.semantic.info.light,
    color: colors.semantic.info.main,
  },
  neutral: {
    backgroundColor: colors.neutral[100],
    color: colors.neutral[500],
  },

  // Image
  image: {
    width: '100%',
    height: '100%',
    objectFit: 'cover',
  },
});

// ============================================
// HELPER FUNCTIONS
// ============================================

/**
 * Extract initials from a full name
 */
function getInitials(name: string): string {
  const parts = name.trim().split(/\s+/);
  if (parts.length === 0) return '';
  if (parts.length === 1) return parts[0].charAt(0).toUpperCase();
  return (parts[0].charAt(0) + parts[parts.length - 1].charAt(0)).toUpperCase();
}

// ============================================
// COMPONENT
// ============================================

export function Avatar({
  initials,
  name,
  src,
  size = 'md',
  variant = 'primary',
  className,
}: AvatarProps) {
  const styles = useStyles();

  // Determine initials to display
  const displayInitials = initials ?? (name ? getInitials(name) : '');

  return (
    <div
      className={mergeClasses(
        styles.base,
        styles[size],
        styles[variant],
        className
      )}
      role="img"
      aria-label={name ?? displayInitials}
    >
      {src ? (
        <img
          src={src}
          alt={name ?? displayInitials}
          className={styles.image}
          onError={(e) => {
            // Hide image on error, show initials instead
            (e.target as HTMLImageElement).style.display = 'none';
          }}
        />
      ) : (
        displayInitials
      )}
    </div>
  );
}

export default Avatar;
