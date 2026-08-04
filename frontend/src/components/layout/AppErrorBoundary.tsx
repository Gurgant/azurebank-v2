import { Component, type ErrorInfo, type ReactNode } from 'react';

/**
 * The last thing between a render error and a blank page.
 *
 * `RouteError` (ADR-0028) is a ROUTE boundary: it sees the route tree and nothing else. The
 * toaster, auth bootstrap, session-expiry warning and step-up modal are siblings of
 * `RouterProvider`, and `Provider` and `ThemeProvider` sit above all of them — a throw anywhere in
 * that chrome went straight past the route boundary to the root, React unmounted the whole tree,
 * and the user was left looking at white nothing with no way forward but a manual reload. That gap
 * was known and written down (RouteError.tsx's docblock, and a negative control in
 * RouteError.test.tsx that asserted it); this closes it.
 *
 * It is a class because there is still no hook equivalent — `getDerivedStateFromError` and
 * `componentDidCatch` exist only on classes, in React 19 as before.
 *
 * THE FALLBACK DEPENDS ON NOTHING. That is the whole design constraint, and it is why this markup
 * is plain elements with inline styles rather than the Fluent components and Griffel classes used
 * everywhere else in the app. When this boundary catches, React has already unmounted everything
 * below it — including `ThemeProvider` and the `FluentProvider` inside it, which is what supplies
 * the page's background and text colours. A fallback built from those would be asking the broken
 * thing to render the apology. `body` in index.css sets no colours of its own, so the styles here
 * are absolute rather than inherited: this screen has to be legible on a bare document.
 */

/*
  eslint-disable no-restricted-syntax --
  The hardcoded-colour rule is right everywhere else and wrong here, which is the whole point of
  this component. It exists to render when the theme is gone: by the time this boundary paints,
  React has unmounted `ThemeProvider` and the `FluentProvider` inside it, so `tokens.*` resolves to
  CSS custom properties nothing is defining any more and the screen would come out unstyled — or
  invisible, if a colour landed on a same-coloured background. The values below are the brand
  `#0077b6` and Fluent's own neutral pair, deliberately frozen rather than referenced. A file-level
  disable rather than four inline ones because the constraint is a property of the whole file, and
  `AppErrorBoundary.test.tsx` asserts at the source that it imports no Fluent and uses no tokens.
*/

interface AppErrorBoundaryProps {
  children: ReactNode;
}

interface AppErrorBoundaryState {
  failed: boolean;
}

const container: React.CSSProperties = {
  minHeight: '100dvh',
  display: 'flex',
  flexDirection: 'column',
  alignItems: 'center',
  justifyContent: 'center',
  gap: '12px',
  padding: '24px',
  textAlign: 'center',
  // Absolute, not inherited — see the docblock. Fluent's theme is gone by the time this renders.
  background: '#ffffff',
  color: '#242424',
  fontFamily: "'Segoe UI', -apple-system, BlinkMacSystemFont, Roboto, sans-serif",
};

const heading: React.CSSProperties = { fontSize: '24px', fontWeight: 600, margin: 0 };
const body: React.CSSProperties = { fontSize: '14px', margin: 0, maxWidth: '42ch' };
const action: React.CSSProperties = {
  marginTop: '8px',
  padding: '8px 16px',
  fontSize: '14px',
  fontWeight: 600,
  color: '#ffffff',
  background: '#0077b6',
  border: 'none',
  borderRadius: '4px',
  cursor: 'pointer',
};

export class AppErrorBoundary extends Component<AppErrorBoundaryProps, AppErrorBoundaryState> {
  state: AppErrorBoundaryState = { failed: false };

  static getDerivedStateFromError(): AppErrorBoundaryState {
    return { failed: true };
  }

  componentDidCatch(error: Error, info: ErrorInfo): void {
    // Logged, never rendered — the same rule RouteError follows. A stack can carry request detail,
    // and this screen is reachable by anyone; the user gets a sentence they can act on instead.
    console.error('Application error', error, info.componentStack);
  }

  render(): ReactNode {
    if (!this.state.failed) return this.props.children;

    return (
      <div style={container} role="alert">
        <h1 style={heading}>Something went wrong</h1>
        <p style={body}>
          The application could not be displayed. Your accounts and any completed transfers are
          unaffected.
        </p>
        {/*
          A full document load, not a router navigation: the router is inside the subtree that just
          failed, and asking it to move is asking the broken thing to fix itself. Same recovery
          idiom and same wording as RouteError, so the app has one answer to this rather than two.
        */}
        <button type="button" style={action} onClick={() => window.location.assign('/')}>
          Back to the dashboard
        </button>
      </div>
    );
  }
}

export default AppErrorBoundary;
