import { useId, type ReactNode } from 'react';
import { Controller, type Control, type FieldPath, type FieldValues } from 'react-hook-form';
import { mergeClasses, Text } from '@fluentui/react-components';
import { sanitizeAmountInput } from '../../forms/moneySchemas';

export interface AmountFieldProps<
  TFieldValues extends FieldValues,
  TContext,
  TTransformedValues extends FieldValues,
> {
  /** Three-generic Control: transforming Zod schemas make the form's OUTPUT ≠ INPUT. */
  control: Control<TFieldValues, TContext, TTransformedValues>;
  name: FieldPath<TFieldValues>;
  /** Per-form accessible name ("Deposit amount" / "Withdraw amount" / "Transfer amount"). */
  ariaLabel: string;
  disabled?: boolean;
  /** Key-rotation hook — called on every body-affecting change (the idempotency invariant). */
  onBodyEdit?: () => void;
  /** Rendered between the input and the error hint (the "New balance / Available" line). */
  belowSlot?: ReactNode;
  /** The host form's visual classes — each form keeps its exact legacy look. */
  classNames: {
    wrapper: string;
    currency: string;
    input: string;
    hint: string;
    /**
     * Applied to the numerals AND the € prefix while the field is in error, so the figure itself
     * turns red rather than only the sentence beneath it. Optional: deposit has no balance bound
     * and its only errors are the fixed min/max, so it opts out.
     */
    invalid?: string;
  };
}

/**
 * The shared money-amount input (plan G2): a Controller-driven sanitized STRING field with a
 * STATIC € prefix (never Fluent `contentBefore` — it closes modals on type, a known Fluent
 * gotcha) and the schema-driven error hint. The hint only shows once something is typed
 * (mirrors the legacy `amount > 0 &&` rule — an empty field disables the CTA silently).
 * The RHF ref lands on the native input so focus-on-first-error works.
 */
export function AmountField<
  TFieldValues extends FieldValues,
  TContext,
  TTransformedValues extends FieldValues,
>({
  control,
  name,
  ariaLabel,
  disabled,
  onBodyEdit,
  belowSlot,
  classNames,
}: AmountFieldProps<TFieldValues, TContext, TTransformedValues>) {
  const hintId = useId();
  return (
    <Controller
      control={control}
      name={name}
      render={({ field, fieldState }) => {
        const showHint = field.value !== '' && fieldState.error !== undefined;
        /*
          `mergeClasses`, not string concatenation. Griffel's atomic classes all carry the same
          specificity, so two classes declaring `color` are resolved by STYLESHEET INSERTION ORDER —
          concatenating happened to work only because `amountInvalid` is declared after
          `amountInput` in the styles object, and reordering those keys would silently un-red the
          figure. `mergeClasses` drops the losing atom outright and makes the last argument win.

          The prefix and the numerals take the SAME class on purpose: a red sentence under a black
          figure reads as a note about the form; a red figure reads as "this number".
        */
        const invalid = showHint ? classNames.invalid : undefined;
        return (
          <>
            <div className={classNames.wrapper}>
              <span className={mergeClasses(classNames.currency, invalid)}>€</span>
              <input
                type="text"
                inputMode="decimal"
                placeholder="0"
                aria-label={ariaLabel}
                className={mergeClasses(classNames.input, invalid)}
                ref={field.ref}
                name={field.name}
                value={field.value}
                disabled={disabled}
                /*
                  The hint used to be red text floating near an input that never referenced it, so
                  a screen reader announced the message once (role="alert") and then, on returning
                  to the field, said nothing about why it was wrong. `aria-invalid` carries the
                  state and `aria-describedby` carries the reason — including the exact figure.
                */
                aria-invalid={showHint || undefined}
                aria-describedby={showHint ? hintId : undefined}
                onBlur={field.onBlur}
                onChange={(e) => {
                  field.onChange(sanitizeAmountInput(e.target.value));
                  onBodyEdit?.();
                }}
              />
            </div>
            {belowSlot}
            {showHint && (
              <Text role="alert" id={hintId} className={classNames.hint}>
                {fieldState.error?.message}
              </Text>
            )}
          </>
        );
      }}
    />
  );
}
