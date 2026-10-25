import { Button, makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { paintedIn } from './fluentStates';
import { renderWithProviders } from './renderWithProviders';

/** The ways a class can meet a Fluent button's own rules, which is what `paintedIn` must read. */
const useFixture = makeStyles({
  restOnly: { backgroundColor: tokens.colorPaletteRedBackground3 },
  pointerPressOnly: { ':hover:active': { backgroundColor: tokens.colorPaletteRedBackground3 } },
  bothPresses: {
    ':hover:active,:active:focus-visible': { backgroundColor: tokens.colorPaletteRedBackground3 },
  },
  insideForcedColours: {
    '@media (forced-colors: active)': { backgroundColor: tokens.colorPaletteRedBackground3 },
  },
});

function renderButton(pick: (styles: ReturnType<typeof useFixture>) => string, name = 'Probe') {
  function Probe() {
    const styles = useFixture();
    return (
      <Button appearance="primary" className={mergeClasses(pick(styles))}>
        {name}
      </Button>
    );
  }
  renderWithProviders(<Probe />);
  return screen.getByRole('button', { name });
}

describe('paintedIn', () => {
  it("reads Fluent's own colour where nothing overrides it", () => {
    const button = renderButton(() => '');
    expect(paintedIn(button, 'rest', 'background-color')).toBe(tokens.colorBrandBackground);
    expect(paintedIn(button, 'hover', 'background-color')).toBe(tokens.colorBrandBackgroundHover);
    expect(paintedIn(button, 'key press', 'background-color')).toBe(
      tokens.colorBrandBackgroundPressed,
    );
  });

  it("lets a state's own rule win over a rest that a class set", () => {
    const button = renderButton((styles) => styles.restOnly);
    expect(paintedIn(button, 'rest', 'background-color')).toBe(tokens.colorPaletteRedBackground3);
    expect(paintedIn(button, 'pointer press', 'background-color')).toBe(
      tokens.colorBrandBackgroundPressed,
    );
  });

  it('tells a press under the pointer from a press of a key', () => {
    const pointer = renderButton((styles) => styles.pointerPressOnly);
    expect(paintedIn(pointer, 'pointer press', 'background-color')).toBe(
      tokens.colorPaletteRedBackground3,
    );
    expect(paintedIn(pointer, 'key press', 'background-color')).toBe(
      tokens.colorBrandBackgroundPressed,
    );
  });

  it('gives the later of two equal rules, and reads nothing inside @media', () => {
    const both = renderButton((styles) => styles.bothPresses);
    expect(paintedIn(both, 'key press', 'background-color')).toBe(
      tokens.colorPaletteRedBackground3,
    );
    const forced = renderButton((styles) => styles.insideForcedColours, 'Forced');
    expect(paintedIn(forced, 'rest', 'background-color')).toBe(tokens.colorBrandBackground);
  });
});
