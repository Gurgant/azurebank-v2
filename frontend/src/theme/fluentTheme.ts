/**
 * FluentUI v9 theme configuration.
 *
 * Both themes derive from one ramp and carry no brand overrides. The three that used to sit here —
 * colorBrandBackground / Hover / Pressed — existed to paper over a mis-indexed ramp; with the brand
 * at Fluent's own primary index they are produced correctly and were deleted rather than updated.
 * See `brandRamp.ts` for the measurements.
 */

import { createLightTheme, createDarkTheme, type Theme } from '@fluentui/react-components';
import { brandRamp } from './brandRamp';
import { darkPalette } from './darkPalette';
import { lightPalette, type LightPalette } from './tokens';

/**
 * The neutral foregrounds a Fluent control draws its words with, every state of each.
 *
 * Left alone they are Fluent's warm greys, #242424, #424242 and #616161 in light, beside the
 * palette's cool #1F2937, #4B5563 and #626875 on the same card. They are given the palette's text
 * roles BY VALUE rather than by name, because Fluent's states do not keep to their family:
 * `colorNeutralForeground2Hover` is #242424, the value of Foreground1, which is how a subtle button
 * darkens under the pointer. Mapped by name, that hover would stay at the rest grey and the change
 * of state would vanish. Tokens that are not listed, such as `colorNeutralForeground2Link`, the
 * `…Brand…` states and Foreground4, keep Fluent's values.
 */
const NEUTRAL_FOREGROUNDS = [
  'colorNeutralForeground1',
  'colorNeutralForeground1Hover',
  'colorNeutralForeground1Pressed',
  'colorNeutralForeground1Selected',
  'colorNeutralForeground2',
  'colorNeutralForeground2Hover',
  'colorNeutralForeground2Pressed',
  'colorNeutralForeground2Selected',
  'colorNeutralForeground3',
  'colorNeutralForeground3Hover',
  'colorNeutralForeground3Pressed',
  'colorNeutralForeground3Selected',
] as const satisfies readonly (keyof Theme)[];

type NeutralForeground = (typeof NEUTRAL_FOREGROUNDS)[number];

/**
 * Fluent's three neutral greys, in order, and the text role each one becomes. Fluent writes the
 * dark ones in lower case, so the keys are compared in lower case. A state whose value is none of
 * the three keeps Fluent's: after an upgrade of Fluent that moved one, `fluentRoles.test.ts`,
 * which holds every state to its role, is what fails.
 */
function foregroundRoles(
  base: Theme,
  fluentGreys: readonly [string, string, string],
  text: LightPalette['text'],
): Pick<Theme, NeutralForeground> {
  const roles = [text.primary, text.secondary, text.tertiary];
  const byValue = new Map(fluentGreys.map((grey, index) => [grey.toLowerCase(), roles[index]]));
  return Object.fromEntries(
    NEUTRAL_FOREGROUNDS.map((name) => [name, byValue.get(base[name].toLowerCase()) ?? base[name]]),
  ) as Pick<Theme, NeutralForeground>;
}

const lightBase = createLightTheme(brandRamp);
const darkBase = createDarkTheme(brandRamp);

/**
 * Light theme.
 *
 * The three neutral backgrounds are a surface decision, not a brand one, and are unaffected by the
 * ramp: a plain white canvas, a faintly blue secondary surface, and a grey tertiary.
 */
export const azureBankLightTheme: Theme = {
  ...lightBase,
  colorNeutralBackground1: '#FFFFFF',
  colorNeutralBackground2: '#F0F7FF',
  colorNeutralBackground3: '#F3F4F6',
  ...foregroundRoles(lightBase, ['#242424', '#424242', '#616161'], lightPalette.text),
};

/**
 * Dark theme: Fluent's own, with the neutral foregrounds on the dark text roles. Fluent's #FFFFFF
 * becomes #EBEBEB and its #D6D6D6 becomes #C2C2C2; its #ADADAD is already `text.tertiary`.
 */
export const azureBankDarkTheme: Theme = {
  ...darkBase,
  ...foregroundRoles(darkBase, ['#FFFFFF', '#D6D6D6', '#ADADAD'], darkPalette.text),
};

export { brandRamp as azureBankBrand };
