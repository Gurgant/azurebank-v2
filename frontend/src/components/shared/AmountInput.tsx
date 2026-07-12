import { useState, useRef, useEffect } from 'react';
import { makeStyles, mergeClasses, Text } from '@fluentui/react-components';
import { colors, transitions } from '../../theme/tokens';

// ============================================
// TYPES
// ============================================

export interface AmountInputProps {
  /** Current amount value */
  value: number;
  /** Change handler */
  onChange: (value: number) => void;
  /** Currency symbol */
  currency?: string;
  /** Maximum allowed amount */
  maxAmount?: number;
  /** Minimum allowed amount */
  minAmount?: number;
  /** Placeholder when empty */
  placeholder?: string;
  /** Error message */
  error?: string;
  /** Disabled state */
  disabled?: boolean;
  /** Auto focus on mount */
  autoFocus?: boolean;
  /** Additional CSS class */
  className?: string;
}

// ============================================
// STYLES
// ============================================

const useStyles = makeStyles({
  container: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '8px',
    width: '100%',
    padding: '24px 16px',
  },

  inputWrapper: {
    display: 'flex',
    alignItems: 'baseline',
    justifyContent: 'center',
    gap: '4px',
    cursor: 'text',
  },

  currency: {
    fontSize: '32px',
    fontWeight: 600,
    color: colors.neutral[400],
    transition: `color ${transitions.fast}`,
  },

  currencyFocused: {
    color: colors.neutral[800],
  },

  amountContainer: {
    position: 'relative',
  },

  amount: {
    fontSize: '48px',
    fontWeight: 700,
    color: colors.neutral[800],
    lineHeight: 1,
    minWidth: '60px',
    textAlign: 'center',
  },

  amountPlaceholder: {
    color: colors.neutral[300],
  },

  // Hidden input for actual value
  hiddenInput: {
    position: 'absolute',
    opacity: 0,
    pointerEvents: 'none',
    width: '1px',
    height: '1px',
    left: 0,
    top: 0,
  },

  // Cursor blink animation
  cursor: {
    position: 'absolute',
    right: '-2px',
    top: '50%',
    transform: 'translateY(-50%)',
    width: '3px',
    height: '40px',
    backgroundColor: colors.brand[60],
    borderRadius: '2px',
    animation: 'blink 1s infinite',
  },

  error: {
    fontSize: '14px',
    color: colors.semantic.error.main,
    textAlign: 'center',
  },

  hint: {
    fontSize: '14px',
    color: colors.neutral[500],
    textAlign: 'center',
  },

  disabled: {
    opacity: 0.5,
    cursor: 'not-allowed',
  },
});

// ============================================
// HELPER FUNCTIONS
// ============================================

function formatDisplayAmount(value: number): string {
  if (value === 0) return '0';

  // Format with thousands separators
  return new Intl.NumberFormat('en-US', {
    minimumFractionDigits: 0,
    maximumFractionDigits: 2,
  }).format(value);
}

function parseInputToNumber(input: string): number {
  // Remove non-numeric characters except decimal point
  const cleaned = input.replace(/[^0-9.]/g, '');

  // Handle multiple decimal points
  const parts = cleaned.split('.');
  if (parts.length > 2) {
    return parseFloat(parts[0] + '.' + parts.slice(1).join(''));
  }

  // Limit to 2 decimal places
  if (parts.length === 2 && parts[1].length > 2) {
    return parseFloat(parts[0] + '.' + parts[1].slice(0, 2));
  }

  return parseFloat(cleaned) || 0;
}

// ============================================
// COMPONENT
// ============================================

export function AmountInput({
  value,
  onChange,
  currency = '$',
  maxAmount,
  minAmount: _minAmount = 0,
  placeholder = '0',
  error,
  disabled = false,
  autoFocus = false,
  className,
}: AmountInputProps) {
  // minAmount can be used for validation in the future
  void _minAmount;
  const styles = useStyles();
  const inputRef = useRef<HTMLInputElement>(null);
  const [isFocused, setIsFocused] = useState(false);
  const [internalValue, setInternalValue] = useState(value.toString());

  // Sync internal value with external value
  useEffect(() => {
    if (!isFocused) {
      setInternalValue(value === 0 ? '' : value.toString());
    }
  }, [value, isFocused]);

  // Auto focus on mount
  useEffect(() => {
    if (autoFocus && inputRef.current) {
      inputRef.current.focus();
    }
  }, [autoFocus]);

  const handleContainerClick = () => {
    if (!disabled && inputRef.current) {
      inputRef.current.focus();
    }
  };

  const handleChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const newInput = e.target.value;
    const numericValue = parseInputToNumber(newInput);

    // Validate against max
    if (maxAmount !== undefined && numericValue > maxAmount) {
      setInternalValue(maxAmount.toString());
      onChange(maxAmount);
      return;
    }

    setInternalValue(newInput);
    onChange(numericValue);
  };

  const handleFocus = () => {
    setIsFocused(true);
  };

  const handleBlur = () => {
    setIsFocused(false);
    // Clean up the display on blur
    if (value === 0) {
      setInternalValue('');
    } else {
      setInternalValue(value.toString());
    }
  };

  const displayValue = value === 0 && !isFocused
    ? placeholder
    : formatDisplayAmount(value);

  const showCursor = isFocused && !disabled;

  return (
    <div
      className={mergeClasses(
        styles.container,
        disabled && styles.disabled,
        className
      )}
    >
      <div
        className={styles.inputWrapper}
        onClick={handleContainerClick}
        role="button"
        tabIndex={-1}
      >
        <Text
          className={mergeClasses(
            styles.currency,
            isFocused && styles.currencyFocused
          )}
        >
          {currency}
        </Text>

        <div className={styles.amountContainer}>
          <Text
            className={mergeClasses(
              styles.amount,
              value === 0 && !isFocused && styles.amountPlaceholder
            )}
          >
            {displayValue}
          </Text>

          {showCursor && <div className={styles.cursor} />}

          {/* Hidden input for actual interaction */}
          <input
            ref={inputRef}
            type="text"
            inputMode="decimal"
            className={styles.hiddenInput}
            value={internalValue}
            onChange={handleChange}
            onFocus={handleFocus}
            onBlur={handleBlur}
            disabled={disabled}
            aria-label="Amount"
            aria-invalid={!!error}
          />
        </div>
      </div>

      {error && (
        <Text className={styles.error} role="alert">
          {error}
        </Text>
      )}

      {maxAmount && !error && (
        <Text className={styles.hint}>
          Available: {currency}{formatDisplayAmount(maxAmount)}
        </Text>
      )}
    </div>
  );
}

export default AmountInput;
