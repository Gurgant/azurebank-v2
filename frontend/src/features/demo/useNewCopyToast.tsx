import { useCallback } from 'react';
import { Toast, ToastTitle, useToastController } from '@fluentui/react-components';
import { appToasterId } from '../../components/feedback';
import { NEW_COPY_READY } from './demoWords';

/**
 * Says "You have a new copy." in the app's toast outlet, when called.
 *
 * A success, where the app's other toast is an error that stays until it is dismissed
 * (src/components/feedback/useProblemToast.tsx). This one names no timeout, so it goes by itself
 * after the toaster's own: nothing in it has to be read before the visitor can go on.
 *
 * The dialog that started over has closed by the time this is said, so the sentence cannot be in
 * the dialog. The outlet is mounted once, at the app's root (src/App.tsx), so it is there
 * whichever page the dialog was opened from.
 */
export function useNewCopyToast(): () => void {
  const { dispatchToast } = useToastController(appToasterId);

  return useCallback(() => {
    dispatchToast(
      <Toast>
        <ToastTitle>{NEW_COPY_READY}</ToastTitle>
      </Toast>,
      { intent: 'success' },
    );
  }, [dispatchToast]);
}
