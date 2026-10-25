import {
  createDarkTheme,
  createLightTheme,
  type Theme,
  tokens,
  webLightTheme,
} from '@fluentui/react-components';
import { describe, expect, it } from 'vitest';
import { brandRamp } from './brandRamp';
import { darkPalette } from './darkPalette';
import { azureBankDarkTheme, azureBankLightTheme } from './fluentTheme';
import { lightPalette } from './tokens';
import { typography } from './typography';

/**
 * Fluent's neutral foregrounds wear the palette's text roles, every state of each.
 *
 * The table is written out rather than derived from `fluentTheme.ts`, so a state left out there,
 * or mapped by its family's name instead of its value, is a row that fails here. The roles follow
 * Fluent's values: `colorNeutralForeground2Hover` is Fluent's darkest grey, not its middle one, so
 * it takes `primary`, and a subtle button still darkens under the pointer.
 */
const ROLE_OF: Record<string, 'primary' | 'secondary' | 'tertiary'> = {
  colorNeutralForeground1: 'primary',
  colorNeutralForeground1Hover: 'primary',
  colorNeutralForeground1Pressed: 'primary',
  colorNeutralForeground1Selected: 'primary',
  colorNeutralForeground2: 'secondary',
  colorNeutralForeground2Hover: 'primary',
  colorNeutralForeground2Pressed: 'primary',
  colorNeutralForeground2Selected: 'primary',
  colorNeutralForeground3: 'tertiary',
  colorNeutralForeground3Hover: 'secondary',
  colorNeutralForeground3Pressed: 'secondary',
  colorNeutralForeground3Selected: 'secondary',
};

/** Fluent's own grey for each role, which is what the mapping by value reads. */
const FLUENT_GREY = {
  light: { primary: '#242424', secondary: '#424242', tertiary: '#616161' },
  dark: { primary: '#ffffff', secondary: '#d6d6d6', tertiary: '#adadad' },
} as const;

/** Tokens a Fluent control also draws with, left as Fluent has them. */
const LEFT_TO_FLUENT = [
  'colorNeutralForeground2Link',
  'colorNeutralForeground4',
  'colorNeutralForegroundDisabled',
  'colorNeutralStrokeAccessible',
  'colorNeutralForeground2BrandHover',
] as const satisfies readonly (keyof Theme)[];

describe.each([
  ['light', azureBankLightTheme, createLightTheme(brandRamp), lightPalette],
  ['dark', azureBankDarkTheme, createDarkTheme(brandRamp), darkPalette],
] as const)('the %s Fluent theme', (name, theme, fluent, palette) => {
  it.each(Object.entries(ROLE_OF))('draws %s with text.%s', (token, role) => {
    expect(theme[token as keyof Theme]).toBe(palette.text[role]);
  });

  it.each(Object.entries(ROLE_OF))(
    'replaces %s, which Fluent draws with the grey of text.%s',
    (token, role) => {
      expect(String(fluent[token as keyof Theme]).toLowerCase()).toBe(FLUENT_GREY[name][role]);
    },
  );

  it.each(LEFT_TO_FLUENT)('leaves %s as Fluent has it', (token) => {
    expect(theme[token]).toBe(fluent[token]);
  });

  it('takes its three greys from the neutral ramp, and its hairline from the surfaces', () => {
    expect(palette.text.primary).toBe(palette.colors.neutral[800]);
    expect(palette.text.secondary).toBe(palette.colors.neutral[600]);
    expect(palette.text.tertiary).toBe(palette.colors.neutral[500]);
    expect(palette.stroke.subtle).toBe(palette.surfaces.border);
  });
});

/**
 * A type role takes Fluent's length wherever Fluent has one, as `radius` does: only the dashboard's
 * balance, which grows with the viewport, is written out.
 */
describe('the type roles', () => {
  const sized = Object.entries(typography).filter(
    ([name, role]) => 'fontSize' in role && name !== 'amountDisplay',
  ) as [string, { fontSize: string; lineHeight: string }][];

  it.each(sized)('%s takes its size and line height from Fluent', (_name, role) => {
    expect(role.fontSize).toMatch(/^var\(--fontSize/);
    expect(role.lineHeight).toMatch(/^var\(--lineHeight/);
  });

  it("sets amountInput at 40px on 52px, Fluent's Hero900 step, in bold", () => {
    const length = (ref: string) => webLightTheme[ref.slice(6, -1) as keyof Theme];
    expect(length(typography.amountInput.fontSize)).toBe('40px');
    expect(length(typography.amountInput.lineHeight)).toBe('52px');
    expect(typography.amountInput.fontWeight).toBe(tokens.fontWeightBold);
  });
});
