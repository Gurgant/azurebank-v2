import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import { Provider } from 'react-redux';
import { store } from './app/store';
import { ThemeProvider } from './theme/ThemeProvider';
import { AppToaster } from './components/feedback';
import { AuthBootstrap, SessionExpiryWarning, StepUpModal } from './features/auth';
import { ProtectedRoute, ProtectedShell, ShellOrBare } from './components/layout';
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

function App() {
  return (
    <Provider store={store}>
      <ThemeProvider>
        <AppToaster />
        <AuthBootstrap />
        <SessionExpiryWarning />
        {/* The single root step-up (PIN elevation) modal — driven by the base-query interceptor. */}
        <StepUpModal />
        <BrowserRouter>
          <Routes>
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
          </Routes>
        </BrowserRouter>
      </ThemeProvider>
    </Provider>
  );
}

export default App;
