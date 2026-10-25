import { useId, useRef, useState, type ClipboardEvent, type KeyboardEvent } from 'react';
import { Text, makeStyles, mergeClasses, shorthands, tokens } from '@fluentui/react-components';
import { Eye16Regular, EyeOff16Regular } from '@fluentui/react-icons';
import { isDemoMode } from '../features/demo/demoMode';
import { DEMO_PIN } from '../features/demo/demoPin';
import { demoPinHint } from '../features/demo/demoWords';
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
  // Six boxes of 48 px and five gaps of 8 px: the row is 328 px where there is room. In a
  // narrower container, a dialog or the PIN setup card on a 320 px screen, the boxes share what it
  // has instead of running past both of its edges.
  group: {
    display: 'flex',
    gap: '8px',
    justifyContent: 'center',
    width: '100%',
    maxWidth: '328px',
  },
  box: {
    flexGrow: 1,
    flexShrink: 1,
    flexBasis: 0,
    // Not for the box's own width, which the flex line sets: an input with a percentage width
    // asks for no room of its own when a container measures its narrowest content. Most callers
    // centre the boxes, and a centred container is never narrower than that content: with a
    // width of auto, the step-up dialog, the withdrawal and delete-account PIN steps and the PIN
    // setup page keep the row at 328 px.
    width: '100%',
    minWidth: 0,
    maxWidth: '48px',
    height: '56px',
    textAlign: 'center',
    fontSize: '24px',
    fontWeight: 600,
    color: colors.neutral[800],
    // WCAG 1.4.11: the box border is the sole affordance on a white dialog, so it must be
    // ≥3:1 against white — neutral[500] (5.59:1), not the too-faint neutral[300].
    ...shorthands.borderWidth('1px'),
    ...shorthands.borderStyle('solid'),
    ...shorthands.borderColor(colors.neutral[500]),
    // The flex line shares the row from a basis of 0, so each box keeps its border and its
    // horizontal padding on top of its share. A 2 px border, on focus or on an error, takes 1 px
    // of this padding, so that every box keeps the same width and the row does not slide as
    // focus moves along it. 2 px is what the browser gives an input of its own.
    paddingLeft: '2px',
    paddingRight: '2px',
    borderRadius: '12px',
    outline: 'none',
    backgroundColor: tokens.colorNeutralBackground1,
    // Colours only. The browser draws a border in whole pixels, so a border width eased from 1 to
    // 2 px stays at 1 px while the padding eases from 2 to 1 px: for those 150 ms the box is
    // narrower and the row slides. Width and padding change at once and together.
    transition: ['border-color', 'background-color', 'color']
      .map((property) => `${property} ${transitions.fast}`)
      .join(', '),
    caretColor: colors.brand[60],
    ':focus': {
      ...shorthands.borderWidth('2px'),
      ...shorthands.borderColor(colors.brand[60]),
      paddingLeft: '1px',
      paddingRight: '1px',
    },
    ':disabled': {
      backgroundColor: colors.neutral[100],
      color: colors.neutral[400],
      cursor: 'not-allowed',
    },
  },
  boxError: {
    ...shorthands.borderWidth('2px'),
    ...shorthands.borderColor(colors.semantic.error.main),
    paddingLeft: '1px',
    paddingRight: '1px',
    ':focus': {
      ...shorthands.borderColor(colors.semantic.error.main),
    },
  },
  // The demo's line under the boxes: words to read, in the size of the button below it, centred
  // under the boxes like everything else in the column.
  hint: {
    fontSize: '13px',
    color: colors.neutral[600],
    textAlign: 'center',
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
    // brand[70], not brand[60]: on a transfer page this button sits on the canvas, where
    // brand[60] is 4.42:1 in the light theme.
    ':hover': { color: colors.brand[70] },
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
  /**
   * What the boxes ask for: a PIN the user already has (`existing`, the default), or one that is
   * being chosen (`new`).
   *
   * On the demo, boxes that ask for an existing PIN have a line under them that prints the PIN
   * every demo copy starts with. That does not undo the masking above. What the boxes mask is
   * what somebody typed, a secret they keep in their head. What the line prints is the public
   * starting PIN of a demo copy, the same on every copy, which the server says is no secret: a
   * copy is kept private by its owner's sign-in, never by its PIN
   * (backend/src/AzureBank.Shared/Constants/DemoCopyDefaults.cs). A visitor is handed a copy
   * with a PIN they never chose, and the line says it at the place where it is asked for.
   *
   * Where a PIN is being chosen the line is left out: there is nothing to remind anyone of, and
   * the starting digits under those boxes would read as the PIN to choose.
   */
  purpose?: 'existing' | 'new';
}

// ============================================
// COMPONENT
// ============================================

/**
 * Six single-digit boxes for a numeric PIN — fully controlled by `value`, which stays a
 * COMPACT digit string (no internal gaps), so the owning flow can submit it as-is once it
 * reaches `length`. Typing auto-advances (and never drops a keystroke, even when the parent
 * has just reset `value` on a wrong-PIN retry); Backspace clears/splices and steps back;
 * arrows navigate; a paste of "123456" distributes across the boxes, and a paste or autofill of a
 * whole PIN replaces whatever was there, from whichever box has focus. Masked by default
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
  purpose = 'existing',
}: PinInputProps) {
  const styles = useStyles();
  const refs = useRef<Array<HTMLInputElement | null>>([]);
  const [revealed, setRevealed] = useState(false);
  const hintId = useId();
  // Asked here, in the component, and not when this module loads: the page says whether it is the
  // demo with a tag (src/features/demo/demoMode.ts).
  const showDemoPin = purpose === 'existing' && isDemoMode();

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
    const digits = raw.replace(/\D/g, '');
    if (!digits) return;
    if (digits.length > 1) {
      // A multi-digit value in a single input event is an OTP / password-manager AUTOFILL
      // (browsers do NOT fire a paste event for it) — distribute from this box instead of
      // dropping all but the last digit. A whole PIN replaces the value outright: spliced in at
      // box 2 of "123456", "987654" used to read "198765".
      const next =
        digits.length >= length
          ? digits.slice(0, length)
          : (value.slice(0, index) + digits).slice(0, length);
      emit(next);
      focusBox(next.length);
      return;
    }
    let next: string;
    let nextFocus: number;
    if (index < value.length) {
      // Overwrite the digit already in this box.
      next = value.slice(0, index) + digits + value.slice(index + 1);
      nextFocus = index + 1;
    } else {
      // At OR past the end — e.g. focus stranded on an empty box after the parent reset
      // `value` (a wrong-PIN retry). Append into the first empty slot rather than dropping
      // the keystroke, and move focus to follow the compact value.
      next = value + digits;
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
    /*
      The guard belongs HERE too, not only on the input's `disabled` attribute.

      A real browser refuses to focus a disabled input and so never delivers this event, which is
      why the omission was invisible. jsdom does deliver it, and that is not merely a test artefact
      — it means the only thing standing between a parked request and a second submit was a
      presentational attribute. For a component whose entire job is a credential, the handler
      should refuse on its own terms. Found by re-aiming the anti-double-spend test at the input
      after the Send button was removed (ADR-0042).
    */
    if (disabled) return;
    const pasted = e.clipboardData.getData('text').replace(/\D/g, '');
    if (!pasted) return;
    // A whole PIN replaces what is there, wherever the caret is; a shorter paste goes in at the box.
    // Spliced, "123456" pasted at box 3 after "12" used to read "121234".
    const next =
      pasted.length >= length
        ? pasted.slice(0, length)
        : (value.slice(0, index) + pasted + value.slice(index)).slice(0, length);
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
        // The caller's description first, as the caller wrote it, then the demo's line, and that
        // one only while it is on the page: an id that names nothing describes nothing. With
        // neither, no attribute at all.
        aria-describedby={
          [ariaDescribedBy, showDemoPin ? hintId : undefined].filter(Boolean).join(' ') || undefined
        }
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
      {showDemoPin && (
        <Text id={hintId} className={styles.hint}>
          {demoPinHint(DEMO_PIN)}
        </Text>
      )}
      {masked && (
        <button
          type="button"
          className={styles.reveal}
          onClick={() => setRevealed((r) => !r)}
          // The name says the state. aria-pressed as well announced "Hide PIN, pressed" once the
          // PIN was showing, which reads as hidden. (Until 2026-09-17 it carried both.)
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
