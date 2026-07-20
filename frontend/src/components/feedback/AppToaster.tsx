import { Toaster } from '@fluentui/react-components';
import { appToasterId } from './useProblemToast';

/** Mounted exactly once in App, inside FluentProvider. */
export function AppToaster() {
  return <Toaster toasterId={appToasterId} position="top-end" pauseOnHover />;
}
