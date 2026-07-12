import { makeStyles, mergeClasses, Text } from '@fluentui/react-components';
import { colors } from '../../theme/tokens';

// ============================================
// TYPES
// ============================================

export type DividerSpacing = 'small' | 'medium' | 'large';
export type DividerThickness = 'thin' | 'thick';
export type DividerColor = 'light' | 'medium' | 'dark';

export interface DividerProps {
  /** Text to display in the middle of the divider */
  text?: string;
  /** Vertical spacing around the divider */
  spacing?: DividerSpacing;
  /** Line thickness */
  thickness?: DividerThickness;
  /** Line color */
  color?: DividerColor;
  /** Additional CSS class */
  className?: string;
}

// ============================================
// STYLES
// ============================================

const useStyles = makeStyles({
  container: {
    display: 'flex',
    alignItems: 'center',
    width: '100%',
  },

  // Spacing
  spacingSmall: {
    marginTop: '16px',
    marginBottom: '16px',
  },
  spacingMedium: {
    marginTop: '24px',
    marginBottom: '24px',
  },
  spacingLarge: {
    marginTop: '32px',
    marginBottom: '32px',
  },

  // Line
  line: {
    flex: 1,
  },
  lineThin: {
    height: '1px',
  },
  lineThick: {
    height: '8px',
  },
  lineLight: {
    backgroundColor: colors.neutral[100],
  },
  lineMedium: {
    backgroundColor: colors.neutral[200],
  },
  lineDark: {
    backgroundColor: colors.neutral[300],
  },

  // Text in divider
  text: {
    padding: '0 16px',
    color: colors.neutral[400],
    fontSize: '13px',
  },
});

// ============================================
// COMPONENT
// ============================================

export function Divider({
  text,
  spacing = 'medium',
  thickness = 'thin',
  color = 'light',
  className,
}: DividerProps) {
  const styles = useStyles();

  const spacingClass = {
    small: styles.spacingSmall,
    medium: styles.spacingMedium,
    large: styles.spacingLarge,
  }[spacing];

  const thicknessClass = {
    thin: styles.lineThin,
    thick: styles.lineThick,
  }[thickness];

  const colorClass = {
    light: styles.lineLight,
    medium: styles.lineMedium,
    dark: styles.lineDark,
  }[color];

  const lineClass = mergeClasses(styles.line, thicknessClass, colorClass);

  if (text) {
    return (
      <div className={mergeClasses(styles.container, spacingClass, className)}>
        <div className={lineClass} />
        <Text className={styles.text}>{text}</Text>
        <div className={lineClass} />
      </div>
    );
  }

  return (
    <div
      className={mergeClasses(lineClass, spacingClass, className)}
      role="separator"
    />
  );
}

export default Divider;
