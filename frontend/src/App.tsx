import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import { FluentProvider } from '@fluentui/react-components';
import { Provider } from 'react-redux';
import { store } from './app/store';
import { azureBankLightTheme } from './theme';
import { AppToaster } from './components/feedback';
import { AuthBootstrap, SessionExpiryWarning, StepUpModal } from './features/auth';
import { ProtectedRoute, ProtectedShell } from './components/layout';
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
} from './pages';

// ============================================
// APP COMPONENT
// ============================================

function App() {
  return (
    <Provider store={store}>
      <FluentProvider theme={azureBankLightTheme}>
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
      </FluentProvider>
    </Provider>
  );
}

export default App;
