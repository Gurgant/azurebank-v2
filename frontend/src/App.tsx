import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import { FluentProvider } from '@fluentui/react-components';
import { Provider } from 'react-redux';
import { store } from './app/store';
import { azureBankLightTheme } from './theme';
import { AppToaster } from './components/feedback';
import { ProtectedRoute } from './components/layout';
import {
  LoginPage,
  RegisterPage,
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
        <BrowserRouter>
          <Routes>
            {/* Public Routes */}
            <Route path="/login" element={<LoginPage />} />
            <Route path="/register" element={<RegisterPage />} />

            {/* Protected Routes - Require Authentication */}
            <Route
              path="/"
              element={
                <ProtectedRoute>
                  <DashboardPage />
                </ProtectedRoute>
              }
            />
            <Route
              path="/dashboard"
              element={
                <ProtectedRoute>
                  <DashboardPage />
                </ProtectedRoute>
              }
            />
            <Route
              path="/accounts"
              element={
                <ProtectedRoute>
                  <AccountsPage />
                </ProtectedRoute>
              }
            />
            <Route
              path="/history"
              element={
                <ProtectedRoute>
                  <HistoryPage />
                </ProtectedRoute>
              }
            />
            <Route
              path="/transactions/:id"
              element={
                <ProtectedRoute>
                  <TransactionDetailPage />
                </ProtectedRoute>
              }
            />
            <Route
              path="/transfer"
              element={
                <ProtectedRoute>
                  <TransferPage />
                </ProtectedRoute>
              }
            />
            <Route
              path="/settings"
              element={
                <ProtectedRoute>
                  <SettingsPage />
                </ProtectedRoute>
              }
            />
            <Route
              path="/profile"
              element={
                <ProtectedRoute>
                  <SettingsPage />
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
