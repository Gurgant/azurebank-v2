import type { PropsWithChildren, ReactElement } from 'react';
import { configureStore } from '@reduxjs/toolkit';
import { Provider } from 'react-redux';
import { MemoryRouter } from 'react-router-dom';
import { FluentProvider } from '@fluentui/react-components';
import { render, type RenderOptions } from '@testing-library/react';
import { azureBankLightTheme } from '../theme/fluentTheme';
import { authReducer } from '../features/auth/authSlice';
import { sessionMiddleware } from '../features/auth/sessionMiddleware';
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
      getDefaultMiddleware().concat(apiSlice.middleware, sessionMiddleware),
  });
}

export type TestStore = ReturnType<typeof makeTestStore>;

type RouterEntry = string | { pathname: string; state?: unknown };

interface ProvidersOptions extends Omit<RenderOptions, 'wrapper'> {
  store?: TestStore;
  /** Initial history entries for MemoryRouter (default: ['/']); objects carry route state. */
  routerEntries?: RouterEntry[];
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
