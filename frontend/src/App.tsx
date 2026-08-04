import {
  createBrowserRouter,
  createRoutesFromElements,
  Route,
  RouterProvider,
  Navigate,
} from 'react-router-dom';
import { RouteError } from './components/layout/RouteError';
import { Provider } from 'react-redux';
import { store } from './app/store';
import { ThemeProvider } from './theme/ThemeProvider';
import { AppToaster } from './components/feedback';
import { AuthBootstrap, SessionExpiryWarning, StepUpModal } from './features/auth';
import { AppErrorBoundary, ProtectedRoute, ProtectedShell, ShellOrBare } from './components/layout';
import {
  LoginPage,
  RegisterPage,
  PinSetupPage,
  DashboardPage,
  AccountsPage,
  HistoryPage,
  TransactionDetailPage,
  TransferPage,
  InternalTransferPage,
  SettingsPage,
  AboutPage,
} from './pages';

// ============================================
// APP COMPONENT
// ============================================

/**
 * A DATA router, and it buys exactly one thing.
 *
 * `useBlocker` is the only way to intercept the browser's Back button, and it is Data-mode only:
 * the availability table in `react-router/docs/start/modes.md` ticks it for Framework and Data and
 * leaves Declarative blank. `useMoneyWizard` has carried a comment deferring this "with the router
 * migration" since it was extracted; this is that migration, and nothing more. No `loader`, no
 * `action`, no `fetcher` — the data layer stays RTK Query (ADR-0028).
 *
 * The route JSX is unchanged. `createRoutesFromElements` takes the same elements: the tree is flat
 * `<Route path element>` with no index routes and no descendant `<Routes>`, and both `<Navigate>`
 * uses are available in every mode.
 *
 * Built at MODULE scope because a router owns history subscriptions and its own state; constructing
 * it inside the component would rebuild the whole thing on every render and drop the current
 * location with it.
 */
const router = createBrowserRouter(
  createRoutesFromElements(
    /*
      A pathless parent that exists only to own `errorElement`.

      A data router CATCHES render errors instead of letting them reach the nearest React error
      boundary, and with no `errorElement` it renders its own default page — React Router's, not
      this app's. That is a regression the migration would have introduced silently, since nothing
      throws today. The route has no `element`, so it renders an `<Outlet />` and every child below
      renders exactly as it did.
    */
    <Route errorElement={<RouteError />}>
      <>
        {/* Public Routes */}
        <Route path="/login" element={<LoginPage />} />
        <Route path="/register" element={<RegisterPage />} />

        {/* Protected routes inside the shared app shell */}
        <Route
          path="/"
          element={
            <ProtectedShell>
              <DashboardPage />
            </ProtectedShell>
          }
        />
        <Route
          path="/dashboard"
          element={
            <ProtectedShell>
              <DashboardPage />
            </ProtectedShell>
          }
        />
        <Route
          path="/accounts"
          element={
            <ProtectedShell>
              <AccountsPage />
            </ProtectedShell>
          }
        />
        <Route
          path="/history"
          element={
            <ProtectedShell>
              <HistoryPage />
            </ProtectedShell>
          }
        />
        <Route
          path="/transactions/:id"
          element={
            <ProtectedShell>
              <TransactionDetailPage />
            </ProtectedShell>
          }
        />
        <Route
          path="/settings"
          element={
            <ProtectedShell>
              <SettingsPage />
            </ProtectedShell>
          }
        />
        {/* /profile is this app's old name for the settings page. ONE canonical URL now: the
            nav label, the page heading and the address bar must never disagree again — the last
            time they did, mobile lit "Profile" on /settings while desktop lit nothing at all.
            `replace` so the back button does not bounce off the redirect. */}
        <Route path="/profile" element={<Navigate to="/settings" replace />} />

        {/* Public on purpose, and shell-wrapped only when signed in — see ShellOrBare. */}
        <Route
          path="/about"
          element={
            <ShellOrBare>
              <AboutPage />
            </ShellOrBare>
          }
        />

        {/* Full-screen wizards: deliberately NO app shell */}
        <Route
          path="/transfer"
          element={
            <ProtectedRoute>
              <TransferPage />
            </ProtectedRoute>
          }
        />
        <Route
          path="/transfer/internal"
          element={
            <ProtectedRoute>
              <InternalTransferPage />
            </ProtectedRoute>
          }
        />
        <Route
          path="/pin-setup"
          element={
            <ProtectedRoute>
              <PinSetupPage />
            </ProtectedRoute>
          }
        />

        {/* Fallback - Redirect unknown routes to dashboard */}
        <Route path="*" element={<Navigate to="/" replace />} />
      </>
    </Route>,
  ),
);

function App() {
  return (
    /*
      OUTERMOST on purpose, above `Provider` and `ThemeProvider` rather than beside the chrome.
      `RouteError` is a ROUTE boundary and sees only the route tree; everything here — the toaster,
      the auth bootstrap, the session warning, the step-up modal, and the two providers themselves —
      is outside it, and a throw in any of them used to unmount the whole app to a blank page. Put
      lower down, this would leave the providers uncovered, which is where a theme or store failure
      would come from.
    */
    <AppErrorBoundary>
      <Provider store={store}>
        <ThemeProvider>
          <AppToaster />
          <AuthBootstrap />
          <SessionExpiryWarning />
          {/* The single root step-up (PIN elevation) modal — driven by the base-query interceptor. */}
          <StepUpModal />
          <RouterProvider router={router} />
        </ThemeProvider>
      </Provider>
    </AppErrorBoundary>
  );
}

export default App;
