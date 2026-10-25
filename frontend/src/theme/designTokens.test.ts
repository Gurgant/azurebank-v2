import { describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import {
  buildDesignTokens,
  EXPORT_COMMAND,
  serialiseDesignTokens,
  toToken,
  type TokenLeaf,
  type TokenTree,
} from './designTokens';
import { darkPalette } from './darkPalette';
import { lightPalette } from './tokens';

/**
 * `design-tokens/tokens.json` is an export of the theme, and nothing in the build reads it, so
 * nothing else would notice it going stale. It is held to the code here, as `index.css` is held to
 * the palettes in `themeVariables.test.ts`.
 */

// Read from disk, from `frontend/` where Vitest runs, because the file lives beside the app and not
// inside it.
const TOKENS_JSON = readFileSync('../design-tokens/tokens.json', 'utf8');

const isLeaf = (node: unknown): node is TokenLeaf =>
  typeof node === 'object' && node !== null && '$value' in node;

/** Every leaf of the document with the dotted path that reaches it. */
function leaves(tree: TokenTree, prefix = ''): [path: string, leaf: TokenLeaf][] {
  return Object.entries(tree).flatMap(([key, node]) => {
    const path = prefix ? `${prefix}.${key}` : key;
    return isLeaf(node) ? [[path, node] as [string, TokenLeaf]] : leaves(node, path);
  });
}

const document = buildDesignTokens();
const groups: TokenTree = {};
for (const [name, group] of Object.entries(document)) {
  if (name !== '$description') groups[name] = group as TokenTree;
}
const allLeaves = leaves(groups);

describe('design tokens export', () => {
  it('is checked in exactly as the theme generates it', () => {
    // The working tree here checks JSON out with CRLF and CI with LF, so the comparison is made on
    // LF. Byte-identical otherwise: a hand edit to one value must fail, not pass as "close enough".
    const onDisk = TOKENS_JSON.replace(/\r\n/g, '\n');
    expect(
      onDisk,
      `design-tokens/tokens.json differs from frontend/src/theme. It is generated: run ` +
        `\`${EXPORT_COMMAND}\` from frontend/ to rewrite it, and commit the result.`,
    ).toBe(serialiseDesignTokens(document));
  });

  it('is JSON that reads back as the document, whatever the layout of its arrays', () => {
    expect(JSON.parse(TOKENS_JSON)).toEqual(JSON.parse(JSON.stringify(document)));
  });

  it('gives every leaf a $value and a $type from the format, and names the command at the root', () => {
    const types = new Set(['color', 'dimension', 'shadow', 'gradient', 'transition', 'number']);
    expect(allLeaves.length).toBeGreaterThan(150);
    for (const [path, leaf] of allLeaves) {
      expect(leaf.$value, path).toBeDefined();
      expect(types, path).toContain(leaf.$type);
    }
    expect(document.$description).toContain(EXPORT_COMMAND);
  });

  it('holds the same tokens in the light and the dark group, so no theme is missing one', () => {
    const names = (tree: TokenTree) =>
      leaves(tree)
        .map(([path]) => path)
        .sort();
    expect(names(document.dark)).toEqual(names(document.light));
    expect(names(document.light).length).toBeGreaterThan(60);
    // The same fact asked of the sources, so a failure here says which side lost the key.
    expect(Object.keys(darkPalette).sort()).toEqual(Object.keys(lightPalette).sort());
  });

  it('spells each value as its type does, not as CSS text', () => {
    for (const [path, { $type, $value }] of allLeaves) {
      const where = `${path} (${$type})`;
      if ($type === 'color') expectColour($value, where);
      if ($type === 'dimension') expectDimension($value, where);
      if ($type === 'number') expect(typeof $value, where).toBe('number');
      if ($type === 'shadow') {
        expect(Array.isArray($value), where).toBe(true);
        for (const layer of $value as Record<string, unknown>[]) {
          expectColour(layer.color, where);
          for (const part of ['offsetX', 'offsetY', 'blur', 'spread']) {
            expectDimension(layer[part], `${where} ${part}`);
          }
        }
      }
      if ($type === 'gradient') {
        const stops = $value as { color: unknown; position: number }[];
        expect(stops.length, where).toBeGreaterThanOrEqual(2);
        for (const stop of stops) {
          expectColour(stop.color, where);
          expect(stop.position, where).toBeGreaterThanOrEqual(0);
          expect(stop.position, where).toBeLessThanOrEqual(1);
        }
      }
      if ($type === 'transition') {
        const value = $value as Record<string, unknown>;
        expectDuration(value.duration, where);
        expectDuration(value.delay, where);
        expect(value.timingFunction as number[], where).toHaveLength(4);
      }
    }
  });

  it('carries the figures the code declares', () => {
    // Spot values read back from the objects the app imports, so a walk that kept the shape but
    // lost the numbers cannot pass on structure alone.
    expect(find('light.colors.brand.60')).toMatchObject({
      $type: 'color',
      $value: { hex: '#0077b6' },
    });
    expect(find('dark.colors.brand.60')).toMatchObject({ $value: { hex: '#4c95d1' } });
    expect(find('light.colors.onBrandPlate')).toMatchObject({
      $value: { hex: '#ffffff', alpha: 0.15 },
    });
    expect(find('breakpoints.lg').$value).toEqual({ value: 1024, unit: 'px' });
    expect(find('zIndex.modal')).toEqual({ $type: 'number', $value: 1300 });
    expect(find('transitions.fast').$value).toMatchObject({
      duration: { value: 150, unit: 'ms' },
    });
    expect(find('componentSizes.sidebar.width').$value).toEqual({ value: 240, unit: 'px' });
    expect(find('light.gradients.brand').$extensions).toBeDefined();
    expect((find('light.shadows.md').$value as unknown[]).length).toBe(2);
  });

  it('does not export safeArea, whose values are var() references to the device insets', () => {
    expect(allLeaves.filter(([path]) => path.split('.').includes('safeArea'))).toEqual([]);
    expect(Object.keys(document)).not.toContain('safeArea');
  });

  it('refuses a value it cannot give a type, rather than guessing one', () => {
    expect(() => toToken('rebeccapurple')).toThrow(/no design-token type/);
    expect(() => toToken('12')).toThrow(/is not a shadow/);
    expect(() => toToken(5)).toThrow(/does not say what a number means/);
    expect(() => toToken('linear-gradient(red, blue)')).toThrow(/no design-token type/);
    expect(() => toToken('linear-gradient(90deg, red 0%, blue 100%)')).toThrow(/not a hex or rgb/);
    expect(() => toToken('150ms bounce')).toThrow(/known easing/);
  });
});

function find(path: string): TokenLeaf {
  const hit = allLeaves.find(([candidate]) => candidate === path);
  if (!hit) throw new Error(`${path} is not in the export`);
  return hit[1];
}

function expectColour(value: unknown, where: string) {
  const colour = value as { colorSpace: string; components: number[]; alpha?: number; hex: string };
  expect(colour.colorSpace, where).toBe('srgb');
  expect(colour.components, where).toHaveLength(3);
  for (const component of colour.components) {
    expect(component, where).toBeGreaterThanOrEqual(0);
    expect(component, where).toBeLessThanOrEqual(1);
  }
  expect(colour.hex, where).toMatch(/^#[0-9a-f]{6}$/);
  if (colour.alpha !== undefined) {
    expect(colour.alpha, where).toBeGreaterThanOrEqual(0);
    expect(colour.alpha, where).toBeLessThan(1);
  }
  // The hex is the same colour as the components, to the rounding the components carry.
  const fromComponents = colour.components.map((c) => Math.round(c * 255));
  const fromHex = [1, 3, 5].map((at) => parseInt(colour.hex.slice(at, at + 2), 16));
  expect(fromComponents, where).toEqual(fromHex);
}

function expectDimension(value: unknown, where: string) {
  const dimension = value as { value: number; unit: string };
  expect(typeof dimension.value, where).toBe('number');
  expect(['px', 'rem'], where).toContain(dimension.unit);
}

function expectDuration(value: unknown, where: string) {
  const duration = value as { value: number; unit: string };
  expect(typeof duration.value, where).toBe('number');
  expect(['ms', 's'], where).toContain(duration.unit);
}
