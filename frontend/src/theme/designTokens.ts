import { lightPalette, componentSizes, transitions, zIndex } from './tokens';
import { darkPalette } from './darkPalette';
import { breakpoints } from './breakpoints';

/**
 * The design tokens as a document in the Design Tokens Community Group format (the 2025.10
 * edition), built from the objects the app itself imports.
 *
 * `design-tokens/tokens.json` is this function's output and nothing else. A token added to a palette
 * reaches the file with no edit in this module, because the trees are walked the way
 * `cssVariables.ts` walks them to derive the stylesheet.
 *
 * One function builds the document, and the export script and the test that holds the file to the
 * code both call it, so there is no second list to keep in step.
 *
 * The format has no field for some of what the code spells as CSS text, so each such value is split
 * into the parts it does have: a shadow list into shadow objects, a gradient into colour stops, a
 * `150ms ease` transition into a duration and a cubic Bézier. The one thing it cannot hold, a
 * gradient's angle, goes in `$extensions`.
 */

/** One token: its `$type`, and the `$value` that type calls for. */
export interface TokenLeaf {
  $type: string;
  $value: unknown;
  $extensions?: Record<string, unknown>;
}

export interface TokenTree {
  [name: string]: TokenLeaf | TokenTree;
}

export interface DesignTokensDocument {
  $description: string;
  light: TokenTree;
  dark: TokenTree;
  componentSizes: TokenTree;
  transitions: TokenTree;
  breakpoints: TokenTree;
  zIndex: TokenTree;
}

/** Where the format has no place for a value, the export keeps it under this key. */
export const EXTENSION_KEY = 'io.github.gurgant.azurebank';

/** The command that rewrites `design-tokens/tokens.json`, named wherever a failure points at it. */
export const EXPORT_COMMAND = 'node scripts/export-design-tokens.mjs';

const DESCRIPTION =
  'The design tokens of AzureBank in the Design Tokens Community Group format (2025.10). ' +
  'Generated from frontend/src/theme by `' +
  EXPORT_COMMAND +
  '`, run from frontend/, and never edited by hand: frontend/src/theme/designTokens.test.ts ' +
  'fails when this file differs from that output. The groups light and dark hold the same ' +
  'tokens for each theme; the other groups are the same in both. A shadow is a list of shadow ' +
  'objects, a gradient is a list of colour stops and a transition holds a duration and a cubic ' +
  'Bézier. The format has no field for a gradient angle, so it is kept in $extensions under ' +
  `${EXTENSION_KEY}. safeArea is not exported: its values are var() references to the insets ` +
  'that the device reports, which are neither a colour nor a length until a browser reads them.';

/** What the format calls the CSS easing keywords, as the four numbers of a cubic Bézier. */
const EASINGS: Readonly<Record<string, readonly [number, number, number, number]>> = {
  linear: [0, 0, 1, 1],
  ease: [0.25, 0.1, 0.25, 1],
  'ease-in': [0.42, 0, 1, 1],
  'ease-out': [0, 0, 0.58, 1],
  'ease-in-out': [0.42, 0, 0.58, 1],
};

/** A bare number in a tree means a length in pixels or a plain number, and only the group knows. */
type NumberMeaning = 'pixels' | 'plain';

const HEX = /^#([0-9a-f]{3,4}|[0-9a-f]{6}|[0-9a-f]{8})$/i;
const FUNCTIONAL =
  /^rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*(?:,\s*(\d*\.?\d+)\s*)?\)$/;
const DIMENSION = /^(-?\d+(?:\.\d+)?)(px|rem)$/;
const TRANSITION = /^(\d+(?:\.\d+)?)(ms|s)\s+([a-z-]+)$/;
const GRADIENT = /^linear-gradient\(\s*(-?\d+(?:\.\d+)?deg)\s*,\s*(.+)\)$/;
const STOP = /^(.*\S)\s+(-?\d+(?:\.\d+)?)%$/;

const unit = (channel: number): number => Math.round((channel / 255) * 10_000) / 10_000;

const toHex = (channels: readonly number[]): string =>
  `#${channels.map((c) => c.toString(16).padStart(2, '0')).join('')}`;

/**
 * Split on commas that are not inside brackets, so `rgba(0, 0, 0, 0.1)` stays one piece and a
 * list of shadows or of gradient stops comes apart.
 */
function splitTopLevel(text: string, separator: string): string[] {
  const parts: string[] = [];
  let depth = 0;
  let start = 0;
  for (let i = 0; i < text.length; i++) {
    const ch = text[i];
    if (ch === '(') depth++;
    else if (ch === ')') depth--;
    else if (ch === separator && depth === 0) {
      parts.push(text.slice(start, i).trim());
      start = i + 1;
    }
  }
  parts.push(text.slice(start).trim());
  return parts.filter((part) => part.length > 0);
}

/** Split on runs of whitespace that are not inside brackets. */
function splitWords(text: string): string[] {
  const words: string[] = [];
  let depth = 0;
  let current = '';
  for (const ch of text) {
    if (ch === '(') depth++;
    if (ch === ')') depth--;
    if (/\s/.test(ch) && depth === 0) {
      if (current) words.push(current);
      current = '';
    } else {
      current += ch;
    }
  }
  if (current) words.push(current);
  return words;
}

const isColour = (text: string): boolean => HEX.test(text) || FUNCTIONAL.test(text);

/** A colour as the format spells it: sRGB components from 0 to 1, with the alpha apart. */
function colourValue(text: string): Record<string, unknown> {
  let channels: number[];
  let alpha = 1;
  const hex = HEX.exec(text);
  const functional = FUNCTIONAL.exec(text);
  if (hex) {
    const digits = hex[1].length <= 4 ? [...hex[1]].map((d) => d + d).join('') : hex[1];
    channels = [0, 2, 4].map((at) => parseInt(digits.slice(at, at + 2), 16));
    if (digits.length === 8)
      alpha = Math.round((parseInt(digits.slice(6, 8), 16) / 255) * 1_000) / 1_000;
  } else if (functional) {
    channels = [functional[1], functional[2], functional[3]].map(Number);
    if (functional[4] !== undefined) alpha = Number(functional[4]);
  } else {
    throw new Error(`"${text}" is not a hex or rgb() colour`);
  }
  if (channels.some((c) => c > 255) || alpha < 0 || alpha > 1) {
    throw new Error(`"${text}" has a channel or an alpha out of range`);
  }
  return {
    colorSpace: 'srgb',
    components: channels.map(unit),
    ...(alpha === 1 ? {} : { alpha }),
    hex: toHex(channels),
  };
}

function dimensionValue(text: string): { value: number; unit: string } {
  const match = DIMENSION.exec(text);
  if (!match) throw new Error(`"${text}" is not a length in px or rem`);
  return { value: Number(match[1]), unit: match[2] };
}

/** `0px 4px 6px -1px rgba(...)` → offsetX, offsetY, blur and spread, as the format names them. */
function shadowValue(text: string): Record<string, unknown>[] {
  return splitTopLevel(text, ',').map((layer) => {
    const words = splitWords(layer);
    const colour = words.filter(isColour);
    const inset = words.includes('inset');
    const lengths = words.filter((word) => !isColour(word) && word !== 'inset');
    if (colour.length !== 1 || lengths.length < 2 || lengths.length > 4) {
      throw new Error(`"${layer}" is not a shadow of two to four lengths and one colour`);
    }
    // A bare `0` is a length in CSS; the format wants the unit written.
    const [offsetX, offsetY, blur = '0px', spread = '0px'] = lengths.map((length) =>
      length === '0' ? '0px' : length,
    );
    return {
      color: colourValue(colour[0]),
      offsetX: dimensionValue(offsetX),
      offsetY: dimensionValue(offsetY),
      blur: dimensionValue(blur),
      spread: dimensionValue(spread),
      ...(inset ? { inset: true } : {}),
    };
  });
}

function gradientToken(text: string): TokenLeaf {
  const match = GRADIENT.exec(text);
  if (!match) throw new Error(`"${text}" is not a linear-gradient with an angle`);
  const stops = splitTopLevel(match[2], ',').map((stop) => {
    const parts = STOP.exec(stop);
    if (!parts) throw new Error(`"${stop}" is not a colour stop with a percentage position`);
    return { color: colourValue(parts[1]), position: Number(parts[2]) / 100 };
  });
  return {
    $type: 'gradient',
    $value: stops,
    $extensions: { [EXTENSION_KEY]: { angle: match[1] } },
  };
}

function transitionToken(text: string): TokenLeaf {
  const match = TRANSITION.exec(text);
  const easing = match ? EASINGS[match[3]] : undefined;
  if (!match || !easing) throw new Error(`"${text}" is not a duration and a known easing keyword`);
  return {
    $type: 'transition',
    $value: {
      duration: { value: Number(match[1]), unit: match[2] },
      delay: { value: 0, unit: match[2] },
      timingFunction: [...easing],
    },
  };
}

/**
 * The token a single leaf of a source tree becomes, told apart by what the value is, not by which
 * group it sits in: a hex or `rgb()` text is a colour wherever the palette puts one.
 */
export function toToken(value: string | number, numbers?: NumberMeaning): TokenLeaf {
  if (typeof value === 'number') {
    if (numbers === 'pixels') return { $type: 'dimension', $value: { value, unit: 'px' } };
    if (numbers === 'plain') return { $type: 'number', $value: value };
    throw new Error(`the number ${value} sits in a group that does not say what a number means`);
  }
  if (isColour(value)) return { $type: 'color', $value: colourValue(value) };
  if (GRADIENT.test(value)) return gradientToken(value);
  if (DIMENSION.test(value)) return { $type: 'dimension', $value: dimensionValue(value) };
  if (TRANSITION.test(value)) return transitionToken(value);
  if (/^(inset\s+)?-?\d/.test(value)) return { $type: 'shadow', $value: shadowValue(value) };
  throw new Error(`no design-token type is known for "${value}"`);
}

interface SourceTree {
  readonly [key: string]: string | number | SourceTree;
}

/** Walk a source tree as `cssVariables.ts` does, keeping its shape and its order of keys. */
function walk(tree: SourceTree, path: readonly string[], numbers?: NumberMeaning): TokenTree {
  const out: TokenTree = {};
  for (const [key, value] of Object.entries(tree)) {
    try {
      out[key] =
        typeof value === 'object' ? walk(value, [...path, key], numbers) : toToken(value, numbers);
    } catch (error) {
      const where = [...path, key].join('.');
      throw new Error(`${where}: ${error instanceof Error ? error.message : String(error)}`, {
        cause: error,
      });
    }
  }
  return out;
}

/** Build the document. Pure: it reads only what the theme modules export. */
export function buildDesignTokens(): DesignTokensDocument {
  return {
    $description: DESCRIPTION,
    light: walk(lightPalette, ['light']),
    dark: walk(darkPalette, ['dark']),
    componentSizes: walk(componentSizes, ['componentSizes']),
    transitions: walk(transitions, ['transitions']),
    breakpoints: walk(breakpoints, ['breakpoints'], 'pixels'),
    zIndex: walk(zIndex, ['zIndex'], 'plain'),
  };
}

/**
 * Indent two spaces, as `JSON.stringify(value, null, 2)` does, except that an array of numbers or
 * strings stays on one line: a colour's three components would otherwise take five.
 */
function stringify(value: unknown, depth: number): string {
  if (typeof value !== 'object' || value === null) return JSON.stringify(value);
  const inner = '  '.repeat(depth + 1);
  const outer = '  '.repeat(depth);
  if (Array.isArray(value)) {
    if (value.every((item) => typeof item !== 'object')) {
      return `[${value.map((item) => JSON.stringify(item)).join(', ')}]`;
    }
    const items = value.map((item) => inner + stringify(item, depth + 1));
    return `[\n${items.join(',\n')}\n${outer}]`;
  }
  const entries = Object.entries(value).map(
    ([key, item]) => `${inner}${JSON.stringify(key)}: ${stringify(item, depth + 1)}`,
  );
  return `{\n${entries.join(',\n')}\n${outer}}`;
}

/** The exact text of `design-tokens/tokens.json`: two-space indentation, LF, a final newline. */
export function serialiseDesignTokens(document: DesignTokensDocument): string {
  return stringify(document, 0) + '\n';
}
