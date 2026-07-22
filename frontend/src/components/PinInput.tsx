import { useRef, useState, type ClipboardEvent, type KeyboardEvent } from 'react';
import { makeStyles, mergeClasses } from '@fluentui/react-components';
import { Eye16Regular, EyeOff16Regular } from '@fluentui/react-icons';
import { colors, transitions } from '../theme/tokens';

// ============================================
// STYLES
// ============================================

const useStyles = makeStyles({
  wrapper: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '10px',
  },
  group: {
    display: 'flex',
    gap: '8px',
    justifyContent: 'center',
  },
  box: {
    width: '48px',
    height: '56px',
    textAlign: 'center',
    fontSize: '24px',
    fontWeight: 600,
    color: colors.neutral[800],
    // WCAG 1.4.11: the box border is the sole affordance on a white dialog, so it must be
    // ≥3:1 against white — neutral[500] (#6B7280 ≈ 4.8:1), not the too-faint neutral[300].
    border: `1px solid ${colors.neutral[500]}`,
    borderRadius: '12px',
    outline: 'none',
    backgroundColor: '#FFFFFF',
    transition: `all ${transitions.fast}`,
    caretColor: colors.brand[60],
    ':focus': {
      border: `2px solid ${colors.brand[60]}`,
    },
    ':disabled': {
      backgroundColor: colors.neutral[100],
      color: colors.neutral[400],
      cursor: 'not-allowed',
    },
  },
  boxError: {
    border: `2px solid ${colors.semantic.error.main}`,
    ':focus': {
      border: `2px solid ${colors.semantic.error.main}`,
    },
  },
  reveal: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '6px',
    background: 'none',
    border: 'none',
    cursor: 'pointer',
    fontSize: '13px',
    fontWeight: 500,
    color: colors.neutral[500],
    padding: '4px 8px',
    borderRadius: '6px',
    ':hover': { color: colors.brand[60] },
  },
});

// ============================================
// TYPES
// ============================================

export interface PinInputProps {
  /** Controlled value — the digits entered so far (a COMPACT 0..length string, no gaps). */
  value: string;
  /** Called with the full new pin string on every edit. */
  onChange: (pin: string) => void;
  /** Fires once the pin reaches `length` digits (an Enter-less submit affordance). */
  onComplete?: (pin: string) => void;
  length?: number;
  disabled?: boolean;
  /** Red-border error state (a wrong/locked PIN). No motion — matches the design spec. */
  error?: boolean;
  autoFocus?: boolean;
  /** Group label, e.g. "Enter your PIN" / "Confirm your PIN". */
  ariaLabel?: string;
  /** Id of the element describing the current error, linked to the group (SC 4.1.3). */
  ariaDescribedBy?: string;
  /**
   * Mask the digits by default (type=password) with a reveal toggle — a memorized banking
   * secret should not sit in plaintext on screen (OWASP ASVS 6.2.6 / NIST 800-63B). Set
   * false only where visibility is genuinely required.
   */
  masked?: boolean;
}

// ============================================
// COMPONENT
// ============================================

/**
 * Six single-digit boxes for a numeric PIN — fully controlled by `value`, which stays a
 * COMPACT digit string (no internal gaps), so the owning flow can submit it as-is once it
 * reaches `length`. Typing auto-advances (and never drops a keystroke, even when the parent
 * has just reset `value` on a wrong-PIN retry); Backspace clears/splices and steps back;
 * arrows navigate; a paste of "123456" distributes across the boxes. Masked by default
 * (dots) with a reveal toggle. The PIN never lives in component state beyond the boxes: the
 * owning flow holds it and clears it on a wrong-PIN error, so nothing here outlives the
 * surface.
 */
export function PinInput({
  value,
  onChange,
  onComplete,
  length = 6,
  disabled = false,
  error = false,
  autoFocus = false,
  ariaLabel = 'PIN',
  ariaDescribedBy,
  masked = true,
}: PinInputProps) {
  const styles = useStyles();
  const refs = useRef<Array<HTMLInputElement | null>>([]);
  const [revealed, setRevealed] = useState(false);

  const focusBox = (index: number) => {
    const clamped = Math.max(0, Math.min(length - 1, index));
    const el = refs.current[clamped];
    el?.focus();
    el?.select();
  };

  const emit = (next: string) => {
    onChange(next);
    if (next.length === length && /^\d+$/.test(next)) {
      onComplete?.(next);
    }
  };

  const handleChange = (index: number, raw: string) => {
    const digit = raw.replace(/\D/g, '').slice(-1);
    if (!digit) return;
    let next: string;
    let nextFocus: number;
    if (index < value.length) {
      // Overwrite the digit already in this box.
      next = value.slice(0, index) + digit + value.slice(index + 1);
      nextFocus = index + 1;
    } else {
      // At OR past the end — e.g. focus stranded on an empty box after the parent reset
      // `value` (a wrong-PIN retry). Append into the first empty slot rather than dropping
      // the keystroke, and move focus to follow the compact value.
      next = value + digit;
      nextFocus = next.length;
    }
    emit(next);
    focusBox(nextFocus);
  };

  const handleKeyDown = (index: number, e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Backspace') {
      e.preventDefault();
      if (value[index]) {
        // Clear the focused digit; splice keeps the string compact.
        const next = value.slice(0, index) + value.slice(index + 1);
        onChange(next);
        if (index >= next.length) {
          focusBox(index - 1);
        }
      } else if (index > 0) {
        // Empty box: remove the previous digit and step back.
        const next = value.slice(0, index - 1) + value.slice(index);
        onChange(next);
        focusBox(index - 1);
      }
    } else if (e.key === 'ArrowLeft') {
      e.preventDefault();
      focusBox(index - 1);
    } else if (e.key === 'ArrowRight') {
      e.preventDefault();
      focusBox(index + 1);
    }
  };

  const handlePaste = (index: number, e: ClipboardEvent<HTMLInputElement>) => {
    e.preventDefault();
    const pasted = e.clipboardData.getData('text').replace(/\D/g, '');
    if (!pasted) return;
    const next = (value.slice(0, index) + pasted + value.slice(index)).slice(0, length);
    emit(next);
    focusBox(next.length);
  };

  const inputType = masked && !revealed ? 'password' : 'text';

  return (
    <div className={styles.wrapper}>
      <div
        className={styles.group}
        role="group"
        aria-label={ariaLabel}
        aria-describedby={ariaDescribedBy}
      >
        {Array.from({ length }, (_, index) => (
          <input
            key={index}
            ref={(el) => {
              refs.current[index] = el;
            }}
            className={mergeClasses(styles.box, error && styles.boxError)}
            type={inputType}
            inputMode="numeric"
            pattern="[0-9]*"
            autoComplete="one-time-code"
            maxLength={1}
            disabled={disabled}
            autoFocus={autoFocus && index === 0}
            aria-label={`Digit ${index + 1} of ${length}`}
            aria-invalid={error || undefined}
            value={value[index] ?? ''}
            onChange={(e) => handleChange(index, e.target.value)}
            onKeyDown={(e) => handleKeyDown(index, e)}
            onPaste={(e) => handlePaste(index, e)}
            onFocus={(e) => e.target.select()}
          />
        ))}
      </div>
      {masked && (
        <button
          type="button"
          className={styles.reveal}
          onClick={() => setRevealed((r) => !r)}
          aria-pressed={revealed}
          aria-label={revealed ? 'Hide PIN' : 'Show PIN'}
        >
          {revealed ? <EyeOff16Regular /> : <Eye16Regular />}
          {revealed ? 'Hide' : 'Show'}
        </button>
      )}
    </div>
  );
}

export default PinInput;
