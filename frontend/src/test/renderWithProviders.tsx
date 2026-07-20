import type { PropsWithChildren, ReactElement } from 'react';
import { configureStore } from '@reduxjs/toolkit';
import { Provider } from 'react-redux';
import { MemoryRouter } from 'react-router-dom';
import { FluentProvider } from '@fluentui/react-components';
import { render, type RenderOptions } from '@testing-library/react';
import { azureBankLightTheme } from '../theme/fluentTheme';
import { authReducer } from '../features/auth/authSlice';
import { apiSlice } from '../features/api/apiSlice';

/**
 * A FRESH store per test (never the app singleton — RTK Query caches would leak between
 * tests). Mirrors src/app/store.ts; if the app store gains a reducer, add it here too.
 */
export function makeTestStore() {
  return configureStore({
    reducer: {
      auth: authReducer,
      [apiSlice.reducerPath]: apiSlice.reducer,
    },
    middleware: (getDefaultMiddleware) =>
      getDefaultMiddleware({
        serializableCheck: { ignoredActions: ['auth/setCredentials'] },
      }).concat(apiSlice.middleware),
  });
}

export type TestStore = ReturnType<typeof makeTestStore>;

interface ProvidersOptions extends Omit<RenderOptions, 'wrapper'> {
  store?: TestStore;
  /** Initial history entries for MemoryRouter (default: ['/']). */
  routerEntries?: string[];
}

/**
 * Renders under the same providers the app composes (theme + store + router), with
 * test-friendly substitutions: a fresh store and a MemoryRouter.
 */
export function renderWithProviders(ui: ReactElement, options: ProvidersOptions = {}) {
  const { store = makeTestStore(), routerEntries = ['/'], ...renderOptions } = options;

  function Wrapper({ children }: PropsWithChildren) {
    return (
      <FluentProvider theme={azureBankLightTheme}>
        <Provider store={store}>
          <MemoryRouter initialEntries={routerEntries}>{children}</MemoryRouter>
        </Provider>
      </FluentProvider>
    );
  }

  return { store, ...render(ui, { wrapper: Wrapper, ...renderOptions }) };
}
