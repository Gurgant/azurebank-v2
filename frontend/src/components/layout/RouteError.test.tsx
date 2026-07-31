import { readFileSync } from 'node:fs';
import {
  createMemoryRouter,
  createRoutesFromElements,
  Route,
  RouterProvider,
} from 'react-router-dom';
import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { ThemeProvider } from '../../theme/ThemeProvider';
import { RouteError } from './RouteError';

/**
 * The safety net, and — just as importantly — where it STOPS.
 *
 * `errorElement` was added by the router migration (ADR-0028) and nothing in the app throws today,
 * which is exactly why it could have been wired wrongly and shipped unnoticed. So the pathless
 * parent that owns it is reproduced here verbatim and actually made to catch something.
 *
 * The second test is the one worth having. A data router catches errors in the ROUTE TREE only, and
 * this app renders four things — toaster, auth bootstrap, session warning, step-up modal — as
 * SIBLINGS above `RouterProvider` (App.tsx). Nothing catches a throw from those: the app has no
 * React error boundary anywhere. Pinning that keeps the limit visible instead of leaving a reader
 * to assume `errorElement` covers the whole app.
 */

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
    // RouteError logs the raw error on purpose; keep the suite output clean without hiding it.
    const logged = vi.spyOn(console, 'error').mockImplementation(() => {});

    mountWithErrorElement();

    expect(screen.getByRole('alert')).toBeInTheDocument();
    expect(screen.getByText('Something went wrong')).toBeInTheDocument();
    // The reassurance is the point of the copy: money already moved is not in doubt.
    expect(
      screen.getByText(/Your accounts and any completed transfers are unaffected/),
    ).toBeInTheDocument();
    // React Router's own default page, which this exists to replace.
    expect(screen.queryByText(/Unexpected Application Error/i)).toBeNull();

    logged.mockRestore();
  });

  it('does NOT cover components rendered above RouterProvider', () => {
    // The negative control, and the reason it matters: `errorElement` is a ROUTE boundary. The
    // toaster, auth bootstrap, session warning and step-up modal are siblings of `RouterProvider`,
    // so a throw in any of them escapes to the root and blanks the app — there is no React error
    // boundary in this codebase to stop it. If that ever changes, this test should fail and be
    // replaced by one asserting the new boundary.
    const logged = vi.spyOn(console, 'error').mockImplementation(() => {});

    const router = createMemoryRouter(
      createRoutesFromElements(
        <Route errorElement={<RouteError />}>
          <Route path="/" element={<p>fine</p>} />
        </Route>,
      ),
      { initialEntries: ['/'] },
    );

    expect(() =>
      render(
        <ThemeProvider>
          <Boom />
          <RouterProvider router={router} />
        </ThemeProvider>,
      ),
    ).toThrow('boom');

    logged.mockRestore();
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
