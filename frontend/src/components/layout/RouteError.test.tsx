import { readFileSync } from 'node:fs';
import {
  createMemoryRouter,
  createRoutesFromElements,
  Route,
  RouterProvider,
} from 'react-router-dom';
import { render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ThemeProvider } from '../../theme/ThemeProvider';
import { RouteError } from './RouteError';
import { AppErrorBoundary } from './AppErrorBoundary';

/**
 * The safety net, and — just as importantly — where it STOPS.
 *
 * `errorElement` was added by the router migration (ADR-0028) and nothing in the app throws today,
 * which is exactly why it could have been wired wrongly and shipped unnoticed. So the pathless
 * parent that owns it is reproduced here verbatim and actually made to catch something.
 *
 * The second test is the one worth having. A data router catches errors in the ROUTE TREE only, and
 * this app renders four things — toaster, auth bootstrap, session warning, step-up modal — as
 * SIBLINGS above `RouterProvider` (App.tsx). `errorElement` does not see them, and pinning that
 * keeps the limit visible instead of leaving a reader to assume it covers the whole app.
 *
 * What has CHANGED: that used to mean a throw up there blanked the app, and this file asserted the
 * blanking. `AppErrorBoundary` now sits above everything (App.tsx), so the same throw is caught —
 * by the ROOT boundary, not this one. The test below therefore pins both halves at once: the route
 * boundary still does not reach the chrome, AND the chrome is no longer uncovered.
 */

/*
  Both render tests stub `console.error` — RouteError logs the raw error on purpose, and React
  itself shouts about the uncaught throw in the negative control.

  Restored HERE rather than at the end of each test, and the difference is not cosmetic: an
  in-line `mockRestore()` never runs if an assertion above it throws, so one failing test would
  leave `console.error` stubbed for everything after it in this file — turning a single red test
  into a silent one. Neither the vitest config nor the shared `setup.ts` restores mocks, so this
  file has to.
*/
afterEach(() => {
  vi.restoreAllMocks();
});

function Boom(): never {
  throw new Error('boom');
}

function mountWithErrorElement() {
  // The same shape as App.tsx: a pathless parent whose only job is to own `errorElement`.
  const router = createMemoryRouter(
    createRoutesFromElements(
      <Route errorElement={<RouteError />}>
        <>
          <Route path="/fine" element={<p>fine</p>} />
          <Route path="/throws" element={<Boom />} />
        </>
      </Route>,
    ),
    { initialEntries: ['/throws'] },
  );

  return render(
    <ThemeProvider>
      <RouterProvider router={router} />
    </ThemeProvider>,
  );
}

describe('RouteError', () => {
  it('catches a throwing route and shows this app’s page, not React Router’s', () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});

    mountWithErrorElement();

    expect(screen.getByRole('alert')).toBeInTheDocument();
    expect(screen.getByText('Something went wrong')).toBeInTheDocument();
    // The reassurance is the point of the copy: money already moved is not in doubt.
    expect(
      screen.getByText(/Your accounts and any completed transfers are unaffected/),
    ).toBeInTheDocument();
    // React Router's own default page, which this exists to replace.
    expect(screen.queryByText(/Unexpected Application Error/i)).toBeNull();
  });

  it('does NOT cover components above RouterProvider — the ROOT boundary does', () => {
    /*
      The scope control. `errorElement` is a ROUTE boundary: the toaster, auth bootstrap, session
      warning and step-up modal are siblings of `RouterProvider`, so a throw in any of them never
      reaches it.

      This test previously asserted that such a throw propagated out of `render` and blanked the
      app, with a note saying it should be replaced the day a boundary existed. That day is now:
      `AppErrorBoundary` wraps everything in App.tsx. So the assertion inverts — the throw is
      caught — and the interesting part is WHICH fallback appears. The two are deliberately
      distinguishable by copy ("The application could not be displayed" vs "This page…"), which is
      what lets this pin the boundary that actually handled it rather than merely "something did".
    */
    vi.spyOn(console, 'error').mockImplementation(() => {});

    const router = createMemoryRouter(
      createRoutesFromElements(
        <Route errorElement={<RouteError />}>
          <Route path="/" element={<p>fine</p>} />
        </Route>,
      ),
      { initialEntries: ['/'] },
    );

    render(
      <AppErrorBoundary>
        <ThemeProvider>
          <Boom />
          <RouterProvider router={router} />
        </ThemeProvider>
      </AppErrorBoundary>,
    );

    expect(screen.getByText(/The application could not be displayed/i)).toBeInTheDocument();
    // Not the route fallback: RouteError never saw this, and a test that accepted either would not
    // be testing the scope boundary at all.
    expect(screen.queryByText(/This page could not be displayed/i)).toBeNull();
  });

  it('is actually wired into App, not just into this file', () => {
    /*
      The gap the two tests above leave open, closed the way this repo already closes it.

      They build their OWN router, so deleting `errorElement` from `App.tsx` would not fail either
      of them — they would pin a structure the app no longer has. Importing App's router instead is
      not available: it is module-scope and unexported, and exporting it would trip
      `react-refresh/only-export-components`, which is an error here (a `.tsx` file exports
      components or helpers, never both).

      So the wiring is asserted against the SOURCE, like `brandFillUsage` and `iconProvenance` do.
      Crude, and it only proves the text is present — but it is the difference between a test that
      notices the wiring being deleted and one that does not.
    */
    const app = readFileSync('src/App.tsx', 'utf8');

    expect(app).toContain('errorElement={<RouteError />}');
    // The chrome sits ABOVE the router on purpose (ADR-0028), which is what the negative control
    // above is about. If this stops being true the scope of the safety net has changed.
    expect(app).toMatch(/<AuthBootstrap \/>[\s\S]*<RouterProvider router=\{router\} \/>/);
  });
});
