import type { PropsWithChildren, ReactElement } from 'react';
import { configureStore } from '@reduxjs/toolkit';
import { Provider } from 'react-redux';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { render, type RenderOptions } from '@testing-library/react';
import { ThemeProvider } from '../theme/ThemeProvider';
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
    /*
      A DATA memory router, not `MemoryRouter`.

      The app moved to `createBrowserRouter` to reach `useBlocker` (ADR-0028), and that hook is
      Data-mode only — under a declarative router it does not degrade, it throws
      "useBlocker must be used within a data router". Thirty tests failed on exactly that the moment
      the money wizard gained a blocker, which is the useful version of this discovery: the helper
      has to render in the same MODE the app does, or the suite is testing a different application.

      The subject renders at a catch-all route so `initialEntries` still decides the location, which
      is what the `routerEntries` option has always been for.
    */
    const router = createMemoryRouter([{ path: '*', element: <>{children}</> }], {
      initialEntries: routerEntries,
    });

    return (
      <ThemeProvider>
        <Provider store={store}>
          <RouterProvider router={router} />
        </Provider>
      </ThemeProvider>
    );
  }

  return { store, ...render(ui, { wrapper: Wrapper, ...renderOptions }) };
}
