import { render, screen, within } from '@testing-library/react';
import { FluentProvider, makeStyles, webLightTheme } from '@fluentui/react-components';
import { describe, expect, it } from 'vitest';
import { ContactPage } from '../../pages/ContactPage';
import { renderWithProviders } from '../../test/renderWithProviders';
import { SectionTitle } from './SectionTitle';

const useOverride = makeStyles({ smaller: { fontSize: '14px', marginBottom: '8px' } });

function Overridden() {
  return (
    <SectionTitle id="t" className={useOverride().smaller}>
      Transaction Information
    </SectionTitle>
  );
}

/** What the title draws, as the browser computes it. */
function look(element: HTMLElement) {
  const style = getComputedStyle(element);
  return {
    display: style.display,
    marginTop: style.marginTop,
    marginBottom: style.marginBottom,
    fontSize: style.fontSize,
    fontWeight: style.fontWeight,
  };
}

/**
 * The title of a section or a card, one component for every page. A title drawn as a `<span>` is in
 * no heading list: a screen reader's heading navigation would find nothing under the page's `<h1>`.
 */
describe('SectionTitle', () => {
  it('is a level-2 heading, a block with no margin, 16 px at weight 600', () => {
    render(
      <FluentProvider theme={webLightTheme}>
        <SectionTitle id="profile-title">Profile</SectionTitle>
      </FluentProvider>,
    );

    const heading = screen.getByRole('heading', { level: 2, name: 'Profile' });
    expect({ tag: heading.tagName, id: heading.id, look: look(heading) }).toStrictEqual({
      tag: 'H2',
      id: 'profile-title',
      look: {
        display: 'block',
        marginTop: '0px',
        marginBottom: '0px',
        fontSize: '16px',
        fontWeight: '600',
      },
    });
  });

  it("takes a page's class after its own, so that what the page adds wins", () => {
    render(
      <FluentProvider theme={webLightTheme}>
        <Overridden />
      </FluentProvider>,
    );

    const heading = screen.getByRole('heading', { level: 2, name: 'Transaction Information' });
    expect(look(heading)).toStrictEqual({
      display: 'block',
      marginTop: '0px',
      marginBottom: '8px',
      fontSize: '14px',
      fontWeight: '600',
    });
  });
});

describe("Contact's two cards", () => {
  it("are level-2 headings under the page's <h1>, each naming its card", () => {
    renderWithProviders(<ContactPage />, { routerEntries: ['/contact'] });

    const tree = screen
      .getAllByRole('heading')
      .map((heading) => `${heading.tagName}:${heading.textContent}`);
    // A card is a column with the small vertical gap between its children, the title included,
    // so the title keeps no margin of its own: one under it would add to the gap, and the title
    // would sit twice as far from its first paragraph as on every other card (16 px, measured).
    const cards = ['Vladislav Aleshaev', 'About this project'].map((name) => {
      const region = screen.getByRole('region', { name });
      return {
        name,
        heading: within(region).getByRole('heading', { level: 2 }).textContent,
        marginBottom: getComputedStyle(within(region).getByRole('heading')).marginBottom,
        gap: getComputedStyle(region).getPropertyValue('gap'),
      };
    });

    const gap = 'var(--spacingVerticalS)';
    expect({ tree, cards }).toStrictEqual({
      tree: ['H1:Contact', 'H2:Vladislav Aleshaev', 'H2:About this project'],
      cards: [
        { name: 'Vladislav Aleshaev', heading: 'Vladislav Aleshaev', marginBottom: '0px', gap },
        { name: 'About this project', heading: 'About this project', marginBottom: '0px', gap },
      ],
    });
  });
});
