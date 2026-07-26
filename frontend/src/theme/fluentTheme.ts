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

/**
 * Light theme — the app's only active theme until U7.
 *
 * The three neutral overrides are a surface decision, not a brand one, and are unaffected by the
 * ramp: a plain white canvas, a faintly blue secondary surface, and a grey tertiary.
 */
export const azureBankLightTheme: Theme = {
  ...createLightTheme(brandRamp),
  colorNeutralBackground1: '#FFFFFF',
  colorNeutralBackground2: '#F0F7FF',
  colorNeutralBackground3: '#F3F4F6',
};

/**
 * Dark theme — defined, and deliberately not wired up yet.
 *
 * It is switched on at U7, once the hardcoded #FFFFFF foregrounds have been purged; turning it on
 * before then would only reveal them one screen at a time.
 */
export const azureBankDarkTheme: Theme = createDarkTheme(brandRamp);

export { brandRamp as azureBankBrand };
