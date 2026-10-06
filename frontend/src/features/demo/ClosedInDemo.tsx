import type { ReactNode } from 'react';
import { Navigate } from 'react-router-dom';
import { isDemoMode } from './demoMode';

/**
 * Closes a page on the demo: there its address leads to the sign-in page, and off the demo the
 * page is drawn as it always was.
 *
 * On the demo a visitor gets an account by claiming a copy, and the sign-in page offers no
 * registration (src/pages/LoginPage.tsx). This is the other half: the registration page's own
 * address leads to the sign-in page. The page behind is never rendered there, so none of its hooks
 * run and it sends nothing.
 *
 * A wrapper around the route's page, and not a redirect written into the route: the route keeps
 * its path and its title in src/App.tsx either way, and what changes with the demo is only what it
 * draws. `ProtectedRoute` redirects from inside a titled route in the same way.
 *
 * `replace`, so that Back from the sign-in page leads to where the visitor was before, and not to
 * the closed address, which would send them forward again.
 *
 * The demo is asked for here, in the component, and not when this module loads: the page says
 * whether it is the demo with a tag, and a test puts that tag on the page after the modules are in
 * (src/features/demo/demoMode.ts).
 */
export function ClosedInDemo({ children }: { children: ReactNode }) {
  return isDemoMode() ? <Navigate to="/login" replace /> : <>{children}</>;
}
