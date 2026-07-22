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
            <Route
              path="/profile"
              element={
                <ProtectedShell>
                  <SettingsPage />
                </ProtectedShell>
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
