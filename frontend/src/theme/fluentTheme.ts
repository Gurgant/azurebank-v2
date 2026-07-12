/**
 * FluentUI v9 Theme Configuration
 * Customizes the default FluentUI theme with AzureBank brand colors
 */

import {
  createLightTheme,
  createDarkTheme,
  type BrandVariants,
  type Theme,
} from '@fluentui/react-components';

/**
 * AzureBank brand color palette
 * Based on the primary blue #006DE2
 */
const azureBankBrand: BrandVariants = {
  10: '#001D3D',
  20: '#002D5E',
  30: '#003D7F',
  40: '#004DA0',
  50: '#005DC1',
  60: '#006DE2', // Primary brand
  70: '#1A7FE8',
  80: '#4D99ED',
  90: '#80B3F2',
  100: '#B3CDF7',
  110: '#CCE0FA',
  120: '#E6F0FC',
  130: '#F0F6FE',
  140: '#F5FAFF',
  150: '#FAFCFF',
  160: '#FFFFFF',
};

/**
 * Light theme - default for AzureBank
 */
export const azureBankLightTheme: Theme = {
  ...createLightTheme(azureBankBrand),
  // Override specific tokens if needed
  colorNeutralBackground1: '#FFFFFF',
  colorNeutralBackground2: '#F0F7FF', // Light blue background
  colorNeutralBackground3: '#F3F4F6',
  colorBrandBackground: '#006DE2',
  colorBrandBackgroundHover: '#004DA0',
  colorBrandBackgroundPressed: '#003D7F',
};

/**
 * Dark theme - for future dark mode support
 */
export const azureBankDarkTheme: Theme = {
  ...createDarkTheme(azureBankBrand),
};

// Re-export brand variants
export { azureBankBrand };
