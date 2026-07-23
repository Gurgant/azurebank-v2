import { Controller, type Control, type FieldPath, type FieldValues } from 'react-hook-form';

export interface DescriptionFieldProps<
  TFieldValues extends FieldValues,
  TContext,
  TTransformedValues extends FieldValues,
> {
  /** Three-generic Control: transforming Zod schemas make the form's OUTPUT ≠ INPUT. */
  control: Control<TFieldValues, TContext, TTransformedValues>;
  name: FieldPath<TFieldValues>;
  disabled?: boolean;
  /** Key-rotation hook — the description is part of the idempotent body. */
  onBodyEdit?: () => void;
  /** The host form's input class — keeps the exact legacy look. */
  className: string;
}

/** The shared optional-description input (legacy contract: 100-char cap, plain text). */
export function DescriptionField<
  TFieldValues extends FieldValues,
  TContext,
  TTransformedValues extends FieldValues,
>({
  control,
  name,
  disabled,
  onBodyEdit,
  className,
}: DescriptionFieldProps<TFieldValues, TContext, TTransformedValues>) {
  return (
    <Controller
      control={control}
      name={name}
      render={({ field }) => (
        <input
          type="text"
          placeholder="Description (optional)"
          aria-label="Description"
          maxLength={100}
          className={className}
          ref={field.ref}
          name={field.name}
          value={field.value}
          disabled={disabled}
          onBlur={field.onBlur}
          onChange={(e) => {
            field.onChange(e.target.value);
            onBodyEdit?.();
          }}
        />
      )}
    />
  );
}
