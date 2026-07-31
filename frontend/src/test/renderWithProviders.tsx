import {
  createContext,
  useContext,
  type PropsWithChildren,
  type ReactElement,
  type ReactNode,
} from 'react';
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
 * test-friendly substitutions: a fresh store and a memory router.
 */
export function renderWithProviders(ui: ReactElement, options: ProvidersOptions = {}) {
  const { store = makeTestStore(), routerEntries = ['/'], ...renderOptions } = options;

  /*
    The subject under test, reached through context rather than through the route element.

    The route tree is baked in at `createMemoryRouter` time, so it cannot close over a `children`
    prop that changes later. Passing the subject down a context keeps the ROUTER fixed while still
    letting `rerender` swap what is being tested — which is the whole point of the indirection.

    Declared HERE rather than at module scope so this file keeps exporting helpers only:
    `react-refresh/only-export-components` is an error in this repo, and a `.tsx` file exports
    components or helpers, never both. Per-CALL is the right lifetime anyway — it matches the
    router's.
  */
  const SubjectContext = createContext<ReactNode>(null);

  function Subject() {
    return <>{useContext(SubjectContext)}</>;
  }

  /*
    A DATA memory router, not `MemoryRouter`.

    The app moved to `createBrowserRouter` to reach `useBlocker` (ADR-0028), and that hook is
    Data-mode only — under a declarative router it does not degrade, it throws
    "useBlocker must be used within a data router". Thirty tests failed on exactly that the moment
    the money wizard gained a blocker, which is the useful version of this discovery: the helper has
    to render in the same MODE the app does, or the suite is testing a different application.

    Built ONCE per `renderWithProviders` call, deliberately, and NOT inside the wrapper component.
    A data router owns its history; rebuilding it on every render throws that history away. The
    failure that causes is nastier than it sounds, because the visible symptom is absent — RTL's
    `rerender` re-invokes the wrapper, and `RouterProvider` seeds its state with a LAZY `useState`,
    so the location on screen stays frozen at the old router's while `useNavigate` silently drives
    the new one. Asserting the location after a rerender therefore PASSES while the two are already
    out of sync; only a divergent history exposes it, which is what the accompanying test does.

    The subject renders at a catch-all route so `initialEntries` still decides the location, which
    is what the `routerEntries` option has always been for.
  */
  const router = createMemoryRouter([{ path: '*', element: <Subject /> }], {
    initialEntries: routerEntries,
  });

  function Wrapper({ children }: PropsWithChildren) {
    return (
      <ThemeProvider>
        <Provider store={store}>
          <SubjectContext.Provider value={children}>
            <RouterProvider router={router} />
          </SubjectContext.Provider>
        </Provider>
      </ThemeProvider>
    );
  }

  return { store, router, ...render(ui, { wrapper: Wrapper, ...renderOptions }) };
}
