import { fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { FluentProvider } from '@fluentui/react-components';
import { describe, expect, it, vi } from 'vitest';
import { COPY, advance } from '../../test/outage';
import { azureBankLightTheme } from '../../theme/fluentTheme';
import { Sidebar } from './Sidebar';

/**
 * The sidebar stays in view while a desktop page scrolls.
 *
 * jsdom lays nothing out, so where the sidebar ends up on a scrolled page was measured in
 * Chromium. What this holds is the declarations that result rests on, read back through
 * `getComputedStyle` from the rules Griffel inserted: drop any one of them and the sidebar
 * scrolls away with the page again, or squeezes or clips its rows in a short window.
 * `align-self` is the one that is easy to lose: a flex item stretched to its container is as
 * tall as the page, and a sticky box that tall has nowhere to stick.
 */
function renderSidebar() {
  render(
    <FluentProvider theme={azureBankLightTheme}>
      <MemoryRouter initialEntries={['/dashboard']}>
        <Sidebar userName="Demo User" onLogout={() => {}} />
      </MemoryRouter>
    </FluentProvider>,
  );
  return screen.getByRole('complementary');
}

describe('Sidebar on a page that scrolls', () => {
  it('sticks to the top of the window instead of stretching to the page', () => {
    const style = getComputedStyle(renderSidebar());

    expect(style.position).toBe('sticky');
    expect(style.top).toBe('0px');
    expect(style.alignSelf).toBe('flex-start');
  });

  it('scrolls by itself in a short window and keeps a focused row whole', () => {
    const sidebar = renderSidebar();
    const style = getComputedStyle(sidebar);

    // As tall as the window, not the page: Sign out sits at its bottom, and a window shorter than
    // the rows is what gives the sidebar something to scroll.
    expect(style.height).toBe('100dvh');
    expect(style.overflowY).toBe('auto');
    // A row's ring is a 2px outline 2px outside it: four pixels of padding keep it inside the
    // sidebar when focus scrolls the row into view.
    expect(style.getPropertyValue('scroll-padding-block')).toBe('4px');
    // The logo's row has a fixed height, and a flex item gives way before its neighbours do.
    expect(getComputedStyle(sidebar.firstElementChild!).flexShrink).toBe('0');
  });
});

/**
 * A sign-out that takes a while says so on the button that started it, and cannot be asked twice.
 * Measured on main in Chromium with the sign-out held 8 s: the button stayed enabled at 1.5 s, no
 * word came at 5.7 s, and a second press sent a second sign-out. The button keeps its words and
 * its focus while it waits: `disabled` would drop focus to the body.
 */
describe('Sidebar while a sign-out is under way', () => {
  function renderSigningOut(onLogout: () => void) {
    const ui = (signingOut: boolean) => (
      <FluentProvider theme={azureBankLightTheme}>
        <MemoryRouter initialEntries={['/dashboard']}>
          <Sidebar userName="Demo User" onLogout={onLogout} signingOut={signingOut} />
        </MemoryRouter>
      </FluentProvider>
    );
    const view = render(ui(true));
    return { rerender: (signingOut: boolean) => view.rerender(ui(signingOut)) };
  }

  it('keeps its words and focus, says it is busy and takes no second press', () => {
    const onLogout = vi.fn();
    renderSigningOut(onLogout);
    const signOut = screen.getByRole('button', { name: 'Sign out' });
    signOut.focus();

    fireEvent.click(signOut);

    expect(onLogout).not.toHaveBeenCalled();
    expect(signOut).toHaveAttribute('aria-busy', 'true');
    expect(signOut).toHaveAttribute('aria-disabled', 'true');
    expect(signOut).toBeEnabled();
    expect(signOut).toHaveFocus();
    // The spinner takes the icon's place.
    expect(signOut.querySelector('.fui-Spinner')).not.toBeNull();
  });

  it('says "Taking longer than usual…" under it from 5 s, and takes a press again after the answer', async () => {
    vi.useFakeTimers();
    const onLogout = vi.fn();
    const { rerender } = renderSigningOut(onLogout);

    await advance(4_999);
    expect(screen.getByRole('status')).toHaveTextContent('');
    await advance(1);
    expect(screen.getByRole('status')).toHaveTextContent(COPY.slow);

    rerender(false);
    const signOut = screen.getByRole('button', { name: 'Sign out' });
    expect(screen.queryByRole('status')).toBeNull();
    expect(signOut).not.toHaveAttribute('aria-busy');
    expect(signOut).not.toHaveAttribute('aria-disabled');
    expect(signOut.querySelector('.fui-Spinner')).toBeNull();
    fireEvent.click(signOut);
    expect(onLogout).toHaveBeenCalledTimes(1);
  });
});
