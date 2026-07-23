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
import { useRenameAzureTagMutation } from '../../features/api/apiSlice';

// Mirrors the backend AzureTag rules (ValidationRules.AzureTagPattern): 3-20 chars, must start
// with a lowercase letter, then lowercase letters / digits / underscore.
const azureTagSchema = z.object({
  azureTag: z
    .string()
    .min(3, 'Handle must be 3-20 characters')
    .max(20, 'Handle must be 3-20 characters')
    .regex(
      /^[a-z][a-z0-9_]*$/,
      'Start with a letter; use only lowercase letters, numbers, and underscores',
    ),
});

type AzureTagFormData = z.infer<typeof azureTagSchema>;

export interface RenameAzureTagDialogProps {
  /** The caller's current handle — the parent mounts this dialog per open. */
  currentTag: string;
  onClose: () => void;
}

/**
 * Rename the caller's own public AzureTag handle (ADR-0015) — the @tag others use to send money,
 * NOT legal identity. Mounted ON open (fresh form seeded with the current tag). On success the
 * mutation invalidates Session, so getMe refetches and the settings page shows the new handle.
 */
export function RenameAzureTagDialog({ currentTag, onClose }: RenameAzureTagDialogProps) {
  const [renameAzureTag, { isLoading, error }] = useRenameAzureTagMutation();
  const problem = error as ApiProblem | undefined;

  const {
    register,
    handleSubmit,
    setError,
    formState: { errors },
  } = useForm<AzureTagFormData>({
    resolver: zodResolver(azureTagSchema),
    defaultValues: { azureTag: currentTag },
  });

  // "Taken" and field-validation land on the input; anything else renders from the bar below.
  const fieldOwnedCode =
    problem?.errorCode === 'AZURE_TAG_TAKEN' ||
    (problem?.errorCode === 'VALIDATION_ERROR' &&
      Object.keys(problem.errors ?? {}).some((key) => key.toLowerCase() === 'azuretag'));
  const showProblemBar = problem && !fieldOwnedCode;

  const onSubmit = async (data: AzureTagFormData) => {
    try {
      await renameAzureTag({ azureTag: data.azureTag }).unwrap();
      onClose();
    } catch (caught) {
      const rejected = caught as ApiProblem;
      if (rejected.errorCode === 'AZURE_TAG_TAKEN') {
        setError('azureTag', { type: 'server', message: 'That handle is already taken.' });
      } else if (rejected.errorCode === 'VALIDATION_ERROR' && rejected.errors) {
        for (const [key, messages] of Object.entries(rejected.errors)) {
          if (key.toLowerCase() === 'azuretag' && messages.length > 0) {
            setError('azureTag', { type: 'server', message: messages[0] });
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
            <DialogTitle>Change your handle</DialogTitle>
            <DialogContent>
              {showProblemBar && (
                <MessageBar intent="error">
                  <MessageBarBody>
                    {problem.detail || 'Could not change your handle. Please try again.'}
                  </MessageBarBody>
                </MessageBar>
              )}

              <Field
                label="Public handle (@)"
                hint="This is how other people find you to send money."
                validationState={errors.azureTag ? 'error' : 'none'}
                validationMessage={errors.azureTag?.message}
              >
                {/* defaultValue seeds the DOM input; RHF's defaultValues seeds the form store —
                    the dialog mounts per open, so both always agree. */}
                <Input defaultValue={currentTag} {...register('azureTag')} />
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
