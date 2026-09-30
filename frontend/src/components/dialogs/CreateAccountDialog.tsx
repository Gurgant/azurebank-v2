import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  Select,
  Spinner,
} from '@fluentui/react-components';
import { useDispatch } from 'react-redux';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { isServiceOutage, type ApiProblem } from '../../api/problemBaseQuery';
import { ACCOUNT_OUTCOME_UNKNOWN } from '../../api/problemMessages';
import type { AccountType } from '../../api/enums';
import { toFieldName } from '../../api/validationErrors';
import { apiSlice, useCreateAccountMutation } from '../../features/api/apiSlice';

// Mirrors the backend contract: name 2-100 chars; type is the shared PascalCase enum.
const createAccountSchema = z.object({
  name: z
    .string()
    .min(2, 'Account name must be at least 2 characters')
    .max(100, 'Account name must be at most 100 characters'),
  type: z.enum(['Checking', 'Savings', 'Investment']),
});

type CreateAccountFormData = z.infer<typeof createAccountSchema>;

const ACCOUNT_TYPES: AccountType[] = ['Checking', 'Savings', 'Investment'];

export interface CreateAccountDialogProps {
  open: boolean;
  onClose: () => void;
}

/**
 * A4 — the first real CREATE in the app. No idempotency key: account creation is not a
 * monetary operation (the contract keys only deposit/withdraw/transfers). Success closes
 * the dialog; the new account appears through the D7 tag invalidation ({Account,'LIST'}),
 * never through a hand-patched cache.
 */
// The server keys validation errors PascalCase (model-state) or camelCase (FluentValidation)
// depending on which layer rejected the request; `toFieldName` reconciles both. See its docblock
// for the measured responses.
const isOurField = (key: string) => {
  const field = toFieldName(key);
  return field === 'name' || field === 'type';
};

export function CreateAccountDialog({ open, onClose }: CreateAccountDialogProps) {
  const dispatch = useDispatch();
  const [createAccount, { isLoading, error, reset: resetMutation }] = useCreateAccountMutation();
  const problem = error as ApiProblem | undefined;

  // A VALIDATION_ERROR normally lands on a field; if the server keys NONE of our
  // fields, fall back to the bar — a dead-silent submit is worse than a generic error.
  const showProblemBar =
    problem &&
    (problem.errorCode !== 'VALIDATION_ERROR' ||
      !Object.keys(problem.errors ?? {}).some(isOurField));

  const {
    register,
    handleSubmit,
    reset,
    setError,
    formState: { errors },
  } = useForm<CreateAccountFormData>({
    resolver: zodResolver(createAccountSchema),
    defaultValues: { name: '', type: 'Checking' },
  });

  const close = () => {
    // Belt-and-braces: the page mounts this dialog per open (fresh state by
    // construction), but clearing the form and the mutation here keeps the
    // component correct even under a persistent parent.
    reset();
    resetMutation();
    onClose();
  };

  const onSubmit = async (data: CreateAccountFormData) => {
    try {
      await createAccount({ name: data.name, type: data.type }).unwrap();
      close();
    } catch (caught) {
      const rejected = caught as ApiProblem;
      if (isServiceOutage(rejected)) {
        // The bar below tells the visitor to check their accounts, but the mutation refreshes the
        // list only on success (apiSlice.ts), and the page behind this dialog keeps its list
        // subscribed: an account that was opened anyway would stay off the list, inviting a
        // second Create. Read the list again, as DeleteAccountDialog does for ACCOUNT_NOT_FOUND.
        dispatch(apiSlice.util.invalidateTags([{ type: 'Account', id: 'LIST' }]));
      }
      if (rejected.errorCode === 'VALIDATION_ERROR' && rejected.errors) {
        for (const [key, messages] of Object.entries(rejected.errors)) {
          if (isOurField(key) && messages.length > 0) {
            setError(toFieldName(key) as 'name' | 'type', {
              type: 'server',
              message: messages[0],
            });
          }
        }
      }
      // Everything else renders from the mutation error state below.
    }
  };

  return (
    <Dialog open={open} onOpenChange={(_, data) => !data.open && close()}>
      <DialogSurface>
        <form
          onSubmit={(event) => {
            void handleSubmit(onSubmit)(event);
          }}
        >
          <DialogBody>
            <DialogTitle>Add New Account</DialogTitle>
            <DialogContent>
              {showProblemBar && (
                // role="alert", as RenameAccountDialog's bar: a MessageBar alone is announced by nothing.
                <MessageBar intent="error" role="alert">
                  <MessageBarBody>
                    {/* An outage (a 503, or no answer in 65 s) may have opened the account
                        anyway, and with no key a second Create opens a second one: say so, and
                        send the visitor to look before trying again. */}
                    {isServiceOutage(problem)
                      ? ACCOUNT_OUTCOME_UNKNOWN
                      : problem.detail || 'Could not create the account. Please try again.'}
                  </MessageBarBody>
                </MessageBar>
              )}

              <Field
                label="Account name"
                validationState={errors.name ? 'error' : 'none'}
                validationMessage={errors.name?.message}
              >
                <Input placeholder="e.g. Holiday Fund" {...register('name')} />
              </Field>

              <Field
                label="Account type"
                validationState={errors.type ? 'error' : 'none'}
                validationMessage={errors.type?.message}
              >
                <Select {...register('type')}>
                  {ACCOUNT_TYPES.map((type) => (
                    <option key={type} value={type}>
                      {type}
                    </option>
                  ))}
                </Select>
              </Field>
            </DialogContent>
            <DialogActions>
              <Button appearance="secondary" onClick={close} type="button">
                Cancel
              </Button>
              <Button appearance="primary" type="submit" disabled={isLoading}>
                {isLoading ? <Spinner size="tiny" /> : 'Create Account'}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
