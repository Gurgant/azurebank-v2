import type { ReactNode } from 'react';
import { Controller, type Control, type FieldPath, type FieldValues } from 'react-hook-form';
import { Text } from '@fluentui/react-components';
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
  return (
    <Controller
      control={control}
      name={name}
      render={({ field, fieldState }) => {
        const showHint = field.value !== '' && fieldState.error !== undefined;
        return (
          <>
            <div className={classNames.wrapper}>
              <span className={classNames.currency}>€</span>
              <input
                type="text"
                inputMode="decimal"
                placeholder="0"
                aria-label={ariaLabel}
                className={classNames.input}
                ref={field.ref}
                name={field.name}
                value={field.value}
                disabled={disabled}
                onBlur={field.onBlur}
                onChange={(e) => {
                  field.onChange(sanitizeAmountInput(e.target.value));
                  onBodyEdit?.();
                }}
              />
            </div>
            {belowSlot}
            {showHint && (
              <Text role="alert" className={classNames.hint}>
                {fieldState.error?.message}
              </Text>
            )}
          </>
        );
      }}
    />
  );
}
