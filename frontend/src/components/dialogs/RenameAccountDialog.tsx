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
  Spinner,
} from '@fluentui/react-components';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import type { ApiProblem } from '../../api/problemBaseQuery';
import { useRenameAccountMutation } from '../../features/api/apiSlice';

// Mirrors the backend contract: rename touches the name ONLY, 2-100 chars.
const renameAccountSchema = z.object({
  name: z
    .string()
    .min(2, 'Account name must be at least 2 characters')
    .max(100, 'Account name must be at most 100 characters'),
});

type RenameAccountFormData = z.infer<typeof renameAccountSchema>;

export interface RenameAccountDialogProps {
  /** The account being renamed — the parent mounts this dialog per open. */
  account: { id: string; name: string };
  onClose: () => void;
}

/**
 * A5 — rename an account. Mounted ON open by the page (fresh form state seeded with
 * the current name; unmount clears everything). The list refreshes through the D7
 * {Account,id} invalidation — the row's tag is provided by the list query.
 */
export function RenameAccountDialog({ account, onClose }: RenameAccountDialogProps) {
  const [renameAccount, { isLoading, error }] = useRenameAccountMutation();
  const problem = error as ApiProblem | undefined;

  const {
    register,
    handleSubmit,
    setError,
    formState: { errors },
  } = useForm<RenameAccountFormData>({
    resolver: zodResolver(renameAccountSchema),
    defaultValues: { name: account.name },
  });

  // A VALIDATION_ERROR lands on the field; anything else renders from the bar below.
  const showProblemBar =
    problem &&
    (problem.errorCode !== 'VALIDATION_ERROR' ||
      !Object.keys(problem.errors ?? {}).some((key) => key.toLowerCase() === 'name'));

  const onSubmit = async (data: RenameAccountFormData) => {
    try {
      await renameAccount({ id: account.id, body: { name: data.name } }).unwrap();
      onClose();
    } catch (caught) {
      const rejected = caught as ApiProblem;
      if (rejected.errorCode === 'VALIDATION_ERROR' && rejected.errors) {
        for (const [key, messages] of Object.entries(rejected.errors)) {
          if (key.toLowerCase() === 'name' && messages.length > 0) {
            setError('name', { type: 'server', message: messages[0] });
          }
        }
      }
    }
  };

  return (
    <Dialog open onOpenChange={(_, data) => !data.open && onClose()}>
      <DialogSurface>
        <form
          onSubmit={(event) => {
            void handleSubmit(onSubmit)(event);
          }}
        >
          <DialogBody>
            <DialogTitle>Rename Account</DialogTitle>
            <DialogContent>
              {showProblemBar && (
                <MessageBar intent="error">
                  <MessageBarBody>
                    {problem.detail || 'Could not rename the account. Please try again.'}
                  </MessageBarBody>
                </MessageBar>
              )}

              <Field
                label="Account name"
                validationState={errors.name ? 'error' : 'none'}
                validationMessage={errors.name?.message}
              >
                {/* defaultValue seeds the DOM input; RHF's defaultValues seeds the form
                    store — the dialog mounts per open, so both always agree. */}
                <Input defaultValue={account.name} {...register('name')} />
              </Field>
            </DialogContent>
            <DialogActions>
              <Button appearance="secondary" onClick={onClose} type="button">
                Cancel
              </Button>
              <Button appearance="primary" type="submit" disabled={isLoading}>
                {isLoading ? <Spinner size="tiny" /> : 'Save'}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
