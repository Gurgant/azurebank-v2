import { makeStyles, mergeClasses, Text } from '@fluentui/react-components';
import { ChevronLeft24Regular, MoreHorizontal24Regular } from '@fluentui/react-icons';
import { colors, componentSizes, zIndex, transitions } from '../../theme/tokens';

// ============================================
// TYPES
// ============================================

export interface HeaderProps {
  /** Page title */
  title?: string;
  /** Show back button */
  showBack?: boolean;
  /** Back button handler */
  onBack?: () => void;
  /** Show more options button */
  showMore?: boolean;
  /** More options handler */
  onMore?: () => void;
  /** Right side content (custom actions) */
  rightContent?: React.ReactNode;
  /** Left side content (custom content instead of back button) */
  leftContent?: React.ReactNode;
  /** Header variant */
  variant?: 'default' | 'transparent';
  /** Additional CSS class */
  className?: string;
}

// ============================================
// STYLES
// ============================================

const useStyles = makeStyles({
  header: {
    position: 'sticky',
    top: 0,
    left: 0,
    right: 0,
    height: componentSizes.header.height,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    padding: `0 ${componentSizes.header.paddingX}`,
    zIndex: zIndex.header,
    transition: `background-color ${transitions.fast}`,
  },

  headerDefault: {
    backgroundColor: '#FFFFFF',
    borderBottom: `1px solid ${colors.neutral[100]}`,
  },

  headerTransparent: {
    backgroundColor: 'transparent',
    borderBottom: 'none',
  },

  leftSection: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    minWidth: '60px',
  },

  centerSection: {
    flex: 1,
    display: 'flex',
    justifyContent: 'center',
    textAlign: 'center',
  },

  rightSection: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    minWidth: '60px',
    justifyContent: 'flex-end',
  },

  title: {
    fontSize: '17px',
    fontWeight: 600,
    color: colors.neutral[800],
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },

  iconButton: {
    width: '40px',
    height: '40px',
    borderRadius: '50%',
    backgroundColor: 'transparent',
    border: 'none',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    cursor: 'pointer',
    color: colors.neutral[700],
    transition: `all ${transitions.fast}`,

    ':hover': {
      backgroundColor: colors.neutral[100],
    },
    ':active': {
      backgroundColor: colors.neutral[200],
    },
    ':focus-visible': {
      outline: `2px solid ${colors.brand[60]}`,
      outlineOffset: '2px',
    },
  },
});

// ============================================
// COMPONENT
// ============================================

export function Header({
  title,
  showBack = false,
  onBack,
  showMore = false,
  onMore,
  rightContent,
  leftContent,
  variant = 'default',
  className,
}: HeaderProps) {
  const styles = useStyles();

  return (
    <header
      className={mergeClasses(
        styles.header,
        variant === 'transparent' ? styles.headerTransparent : styles.headerDefault,
        className,
      )}
    >
      {/* Left Section */}
      <div className={styles.leftSection}>
        {leftContent ? (
          leftContent
        ) : showBack ? (
          <button className={styles.iconButton} onClick={onBack} aria-label="Go back" type="button">
            <ChevronLeft24Regular />
          </button>
        ) : null}
      </div>

      {/* Center Section - Title */}
      <div className={styles.centerSection}>
        {title && <Text className={styles.title}>{title}</Text>}
      </div>

      {/* Right Section */}
      <div className={styles.rightSection}>
        {rightContent ? (
          rightContent
        ) : showMore ? (
          <button
            className={styles.iconButton}
            onClick={onMore}
            aria-label="More options"
            type="button"
          >
            <MoreHorizontal24Regular />
          </button>
        ) : null}
      </div>
    </header>
  );
}

export default Header;
