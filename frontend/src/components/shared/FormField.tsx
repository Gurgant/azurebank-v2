import { makeStyles, mergeClasses, Text, Input } from '@fluentui/react-components';
import type { InputProps } from '@fluentui/react-components';
import { colors, transitions } from '../../theme/tokens';

// ============================================
// TYPES
// ============================================

export interface FormFieldProps extends Omit<InputProps, 'size'> {
  /** Field label */
  label: string;
  /** Field name (required for form handling) */
  name: string;
  /** Error message to display */
  error?: string;
  /** Helper text to display below input */
  hint?: string;
  /** Required field indicator */
  required?: boolean;
  /** Input size */
  inputSize?: 'small' | 'medium' | 'large';
  /** Additional CSS class for container */
  containerClassName?: string;
}

// ============================================
// STYLES
// ============================================

const useStyles = makeStyles({
  container: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    width: '100%',
  },

  labelContainer: {
    display: 'flex',
    alignItems: 'center',
    gap: '4px',
  },

  label: {
    fontSize: '14px',
    fontWeight: 500,
    color: colors.neutral[700],
  },

  required: {
    color: colors.semantic.error.main,
    fontSize: '14px',
  },

  input: {
    width: '100%',
    borderRadius: '12px',
    backgroundColor: colors.neutral[50],
    border: `1.5px solid ${colors.neutral[200]}`,
    transition: `all ${transitions.fast}`,

    ':hover': {
      border: `1.5px solid ${colors.neutral[300]}`,
    },
    ':focus-within': {
      border: `1.5px solid ${colors.brand[60]}`,
      backgroundColor: '#FFFFFF',
      outline: 'none',
    },
  },

  inputSmall: {
    height: '40px',
    fontSize: '14px',
  },
  inputMedium: {
    height: '48px',
    fontSize: '15px',
  },
  inputLarge: {
    height: '52px',
    fontSize: '16px',
  },

  inputError: {
    border: `1.5px solid ${colors.semantic.error.main}`,
    backgroundColor: colors.semantic.error.light,
    ':hover': {
      border: `1.5px solid ${colors.semantic.error.main}`,
    },
    ':focus-within': {
      border: `1.5px solid ${colors.semantic.error.main}`,
      backgroundColor: '#FFFFFF',
    },
  },

  hint: {
    fontSize: '12px',
    color: colors.neutral[500],
    marginTop: '2px',
  },

  error: {
    fontSize: '12px',
    color: colors.semantic.error.main,
    marginTop: '2px',
  },
});

// ============================================
// COMPONENT
// ============================================

export function FormField({
  label,
  name,
  error,
  hint,
  required = false,
  inputSize = 'medium',
  containerClassName,
  className,
  ...inputProps
}: FormFieldProps) {
  const styles = useStyles();

  const sizeClass = {
    small: styles.inputSmall,
    medium: styles.inputMedium,
    large: styles.inputLarge,
  }[inputSize];

  return (
    <div className={mergeClasses(styles.container, containerClassName)}>
      <div className={styles.labelContainer}>
        <label htmlFor={name} className={styles.label}>
          {label}
        </label>
        {required && <span className={styles.required}>*</span>}
      </div>

      <Input
        id={name}
        name={name}
        aria-invalid={!!error}
        aria-describedby={error ? `${name}-error` : hint ? `${name}-hint` : undefined}
        className={mergeClasses(styles.input, sizeClass, error && styles.inputError, className)}
        {...inputProps}
      />

      {error && (
        <Text id={`${name}-error`} className={styles.error} role="alert">
          {error}
        </Text>
      )}

      {hint && !error && (
        <Text id={`${name}-hint`} className={styles.hint}>
          {hint}
        </Text>
      )}
    </div>
  );
}

export default FormField;
