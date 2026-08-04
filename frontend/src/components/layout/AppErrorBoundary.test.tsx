import { readFileSync } from 'node:fs';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AppErrorBoundary } from './AppErrorBoundary';

/**
 * The root boundary, and the one property that matters: a throw anywhere below it produces a page
 * a person can act on instead of white nothing.
 *
 * React logs every caught error to console.error regardless of the boundary, so the spy is set per
 * test and restored globally — an in-line `mockRestore()` never runs if an assertion above it
 * throws, and one failing test would then silence console.error for every test after it.
 */
afterEach(() => {
  vi.restoreAllMocks();
});

function Boom(): never {
  throw new Error('boom');
}

describe('AppErrorBoundary', () => {
  it('renders children untouched when nothing throws', () => {
    render(
      <AppErrorBoundary>
        <p>all fine</p>
      </AppErrorBoundary>,
    );

    expect(screen.getByText('all fine')).toBeInTheDocument();
    // The fallback must not be rendered speculatively — it replaces the app, it does not accompany it.
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('catches a throw and offers a way forward', () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});

    render(
      <AppErrorBoundary>
        <Boom />
      </AppErrorBoundary>,
    );

    const alert = screen.getByRole('alert');
    expect(alert).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: /something went wrong/i })).toBeInTheDocument();
    // The reassurance is the point of the copy, not decoration: this screen is what a customer sees
    // when their bank blanks out, and the first question is whether their money moved.
    expect(screen.getByText(/accounts and any completed transfers are unaffected/i)).toBeVisible();
    expect(screen.getByRole('button', { name: /back to the dashboard/i })).toBeInTheDocument();
  });

  it('logs the error rather than showing it', () => {
    const logged = vi.spyOn(console, 'error').mockImplementation(() => {});

    render(
      <AppErrorBoundary>
        <Boom />
      </AppErrorBoundary>,
    );

    // Logged for whoever is debugging...
    expect(logged.mock.calls.some((args) => String(args[0]).includes('Application error'))).toBe(
      true,
    );
    // ...and NOT put on screen. A stack can carry request detail and this page is reachable by
    // anyone, so the same rule RouteError follows applies here.
    expect(screen.queryByText(/boom/)).toBeNull();
  });

  it('recovers with a full document load, not a router navigation', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    const assign = vi.fn();
    vi.spyOn(window, 'location', 'get').mockReturnValue({
      ...window.location,
      assign,
    } as unknown as Location);

    render(
      <AppErrorBoundary>
        <Boom />
      </AppErrorBoundary>,
    );
    await userEvent.click(screen.getByRole('button', { name: /back to the dashboard/i }));

    /*
      A full load matters here in a way it does not for a route error: the router is INSIDE the
      subtree that just failed, so a navigation would be asking the broken thing to repair itself.
    */
    expect(assign).toHaveBeenCalledWith('/');
  });

  it('does not depend on the theme it may have to apologise for', () => {
    /*
      The design constraint, asserted at the SOURCE because it cannot be observed from the rendered
      output: when this boundary catches, React has already unmounted ThemeProvider and the
      FluentProvider inside it. A fallback built from Fluent components or Griffel classes would be
      asking the broken thing to render the apology.

      The same source-assertion idiom the repo already uses for wiring (RouteError.test.tsx,
      brandFillUsage, iconProvenance). Crude, but it is the difference between noticing this being
      undone and not noticing.
    */
    /*
      Comments are STRIPPED before scanning, and that is not a detail: the first version matched
      raw source for the word "tokens" and failed on this very file — the docblock explaining why
      tokens must not be used contains the word "tokens". A guard that a truthful comment can break
      teaches people to stop writing comments. It matches code shapes now, not vocabulary.
    */
    const code = readFileSync('src/components/layout/AppErrorBoundary.tsx', 'utf8')
      .replace(/\/\*[\s\S]*?\*\//g, '')
      .replace(/\/\/.*$/gm, '');

    expect(code).not.toMatch(/from '@fluentui\/react-components'/);
    expect(code).not.toMatch(/\bmakeStyles\s*\(/);
    expect(code).not.toMatch(/\btokens\./);
  });

  it('is actually wired into App as the outermost element', () => {
    // Same reasoning as RouteError.test.tsx's wiring check: this file could pass every test above
    // while App renders nothing of the sort. Asserted against the source, and specifically that it
    // sits ABOVE Provider — below it, a store or theme failure would still blank the page.
    const app = readFileSync('src/App.tsx', 'utf8');

    expect(app).toMatch(/<AppErrorBoundary>[\s\S]*<Provider store=\{store\}>/);
    expect(app).toMatch(/<\/Provider>[\s\S]*<\/AppErrorBoundary>/);
  });
});
