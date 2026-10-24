import { StrictMode, type ReactNode } from 'react';
import { act, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { FluentProvider } from '@fluentui/react-components';
import { describe, expect, it } from 'vitest';
import { azureBankLightTheme } from '../../theme/fluentTheme';
import { setViewportWidth } from '../../test/viewport';
import { AppLayout } from './AppLayout';
import { BottomNav } from './BottomNav';

/**
 * The bar is fixed over the bottom of the page, and the browser scrolls a control that takes focus
 * into view against the viewport's bottom edge, which the bar covers. Measured in Chromium at
 * 320x568 before this: a Tab through Accounts stopped on three controls wholly under the bar. While
 * the bar is mounted, the root's scroll padding moves that edge up to the bar.
 *
 * jsdom does no layout and no scrolling, so what is held here is the declaration: its value is the
 * room the shell keeps for the bar, it is there exactly while the bar is, and StrictMode's second
 * run of the effect leaves it in place. That the browser then scrolls every stop clear of the bar
 * was measured in Chromium.
 */

const rootScrollPadding = () =>
  document.documentElement.style.getPropertyValue('scroll-padding-bottom');

/** StrictMode, as `main.tsx` renders the app, so the effect runs, is cleaned up and runs again. */
function renderStrict(ui: ReactNode) {
  return render(
    <StrictMode>
      <FluentProvider theme={azureBankLightTheme}>
        <MemoryRouter initialEntries={['/dashboard']}>{ui}</MemoryRouter>
      </FluentProvider>
    </StrictMode>,
  );
}

const renderShell = () =>
  renderStrict(
    <AppLayout onLogout={() => {}}>
      <p>Page</p>
    </AppLayout>,
  );

/**
 * The bottom padding the shell gives the page on a phone, read from the rule that applies to it:
 * Griffel emits one class for each property, so the page's box carries one padding-bottom class.
 */
function shellReservation(): string {
  const rules = [...document.styleSheets].flatMap((sheet) => [...sheet.cssRules]);
  const values = [...screen.getByRole('main').classList]
    .flatMap((cls) =>
      rules.filter(
        (rule): rule is CSSStyleRule =>
          rule instanceof CSSStyleRule && rule.selectorText === `.${cls}`,
      ),
    )
    .map((rule) => rule.style.getPropertyValue('padding-bottom'))
    .filter(Boolean);

  // None would make the comparison below pass against two empty strings.
  expect(values).toHaveLength(1);
  return values[0];
}

describe('BottomNav — the room the browser keeps for it when focus moves', () => {
  it('sets the root scroll padding to the bar, its gap and the home indicator', () => {
    renderStrict(<BottomNav />);

    expect(rootScrollPadding()).toBe('calc(72px + 10px + var(--ab-safe-bottom))');
  });

  it('keeps the same room the shell reserves for the bar under the page', () => {
    setViewportWidth(390);
    renderShell();

    expect(rootScrollPadding()).toBe(shellReservation());
  });

  it('clears it when the bar unmounts', () => {
    const { unmount } = renderStrict(<BottomNav />);
    expect(rootScrollPadding()).not.toBe('');

    unmount();

    expect(rootScrollPadding()).toBe('');
  });

  it('clears it when the window widens past 1024 px and the shell draws the sidebar', () => {
    setViewportWidth(390);
    renderShell();
    expect(rootScrollPadding()).not.toBe('');

    act(() => setViewportWidth(1280));

    // The bar is gone: More lives only in it.
    expect(screen.queryByRole('button', { name: 'More' })).toBeNull();
    expect(rootScrollPadding()).toBe('');
  });
});
