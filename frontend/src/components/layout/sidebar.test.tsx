import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { FluentProvider } from '@fluentui/react-components';
import { describe, expect, it } from 'vitest';
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
