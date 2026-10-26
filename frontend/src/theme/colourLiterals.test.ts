import { readdirSync, readFileSync } from 'node:fs';
import ts from 'typescript';
import { describe, expect, it } from 'vitest';

/**
 * Colour belongs in `src/theme` and nowhere else: a hex colour, or an `rgb()`, `rgba()`, `hsl()`
 * or `hsla()` call, written in a string or a template literal of any other `.ts` or `.tsx` file
 * fails here, with its file and line. Every hardcoded colour is a surface that cannot follow the
 * theme, which is what makes dark mode possible.
 *
 * It reads what ESLint reads, because it was ESLint's rule (`no-restricted-syntax`) until it moved
 * here, so that the lint is left with no rule of its own: every `.ts` and `.tsx` file under the
 * frontend, tests among them, but `node_modules`, `dist`, `src/api/schema.d.ts` (generated) and
 * `src/theme`, where colour is spelled out because that is its definition, not a use.
 *
 * What is matched is the rule's, on the same nodes. A string literal is matched on its value, a
 * template literal on the raw text of each part between `${}`; the TypeScript parser finds them, so
 * a comment is never read. A 6- or 8-digit hex is matched anywhere in the text (`1px solid
 * #ff0000` is the common one); a 3- or 4-digit one only as the whole text, because un-anchored it
 * would hit URL fragments and ids. A function name is matched in any case: CSS reads `RGBA(` as
 * `rgba(`.
 *
 * What it does not catch, stated so nobody trusts it further than it goes: a named CSS colour
 * (`white`, `tomato`), `color-mix()`, a short hex inside a longer string, and anything outside
 * TypeScript. Those are caught by reading.
 */

const LONG_HEX = /#(?:[0-9a-fA-F]{8}|[0-9a-fA-F]{6})(?![0-9a-fA-F])/;
const SHORT_HEX = /^#[0-9a-fA-F]{3,4}$/;
const COLOUR_FUNCTION = /(?:rgba?|hsla?)\(/i;

const isColour = (text: string) =>
  LONG_HEX.test(text) || SHORT_HEX.test(text) || COLOUR_FUNCTION.test(text);

/** Files where a colour is allowed, each for the whole file and with its reason. */
const EXEMPT: Record<string, string> = {
  'src/components/layout/AppErrorBoundary.tsx':
    'It renders when the theme is gone: by then React has unmounted ThemeProvider and the ' +
    'FluentProvider inside it, so a token would resolve to a CSS custom property nothing defines. ' +
    "Its colours are the brand's #0077b6 and Fluent's neutral pair, frozen on purpose; " +
    'AppErrorBoundary.test.tsx asserts at the source that it imports no Fluent and uses no tokens.',
  'screenshots/card.ts':
    'The social card is not the app: a standalone page photographed on its own, with no ' +
    "FluentProvider and no tokens. Its colours are the logo's two hexes and shades of them, " +
    'defined here as src/theme defines the theme.',
};

/** Every `.ts` and `.tsx` file ESLint reads, from the frontend root, with forward slashes. */
function lintedFiles(): string[] {
  return readdirSync('.', { withFileTypes: true })
    .filter((entry) => entry.name !== 'node_modules' && entry.name !== 'dist')
    .flatMap((entry) =>
      entry.isDirectory()
        ? readdirSync(entry.name, { recursive: true, encoding: 'utf8' }).map(
            (path) => `${entry.name}/${path.replaceAll('\\', '/')}`,
          )
        : [entry.name],
    )
    .filter((path) => /\.tsx?$/.test(path) && !path.split('/').includes('node_modules'))
    .filter((path) => !path.startsWith('src/theme/') && path !== 'src/api/schema.d.ts');
}

interface Colour {
  line: number;
  text: string;
}

/** The string and template literals in `code` that hold a colour; comments are never read. */
function coloursIn(name: string, code: string): Colour[] {
  const source = ts.createSourceFile(
    name,
    code,
    ts.ScriptTarget.Latest,
    false,
    name.endsWith('.tsx') ? ts.ScriptKind.TSX : ts.ScriptKind.TS,
  );
  const found: Colour[] = [];
  const visit = (node: ts.Node) => {
    let text: string | undefined;
    if (ts.isStringLiteral(node)) text = node.text;
    else if (
      ts.isNoSubstitutionTemplateLiteral(node) ||
      ts.isTemplateHead(node) ||
      ts.isTemplateMiddle(node) ||
      ts.isTemplateTail(node)
    )
      text = node.rawText ?? node.text;
    if (text !== undefined && isColour(text)) {
      const { line } = source.getLineAndCharacterOfPosition(node.getStart(source));
      found.push({ line: line + 1, text });
    }
    ts.forEachChild(node, visit);
  };
  visit(source);
  return found;
}

const FILES = lintedFiles();
const FOUND = Object.fromEntries(
  FILES.map((path) => [path, coloursIn(path, readFileSync(path, 'utf8'))] as const).filter(
    ([, colours]) => colours.length > 0,
  ),
);

describe('hand-written colours outside src/theme', () => {
  it('has none but in the files exempt by name', () => {
    const offenders = Object.entries(FOUND)
      .filter(([path]) => !(path in EXEMPT))
      .flatMap(([path, colours]) => colours.map(({ line, text }) => `${path}:${line} ${text}`));
    expect(
      offenders,
      'Hardcoded colour. Use a Fluent token (tokens.colorNeutralBackground1, ' +
        'tokens.colorNeutralForegroundOnBrand, …) for anything the theme should drive, or the ' +
        'local palette in src/theme/tokens.ts for what Fluent has no name for. See ' +
        'docs/brand-assets.md and frontend/CONVENTIONS.md.',
    ).toEqual([]);
  });

  it.each(Object.keys(EXEMPT))('still needs the exemption of %s', (path) => {
    // As ESLint reports a disable that no longer disables anything: an exemption past its reason
    // is a hole left open.
    expect(FILES).toContain(path);
    expect(FOUND[path]?.length ?? 0).toBeGreaterThan(0);
  });

  it('reads the files ESLint reads, and not src/theme', () => {
    // CONTROL: a check that read nothing would pass.
    expect(FILES.length).toBeGreaterThan(250);
    expect(FILES).toEqual(
      expect.arrayContaining([
        'src/pages/DashboardPage.tsx',
        'src/mocks/handlers.ts',
        'e2e/contrast.ts',
        'e2e-demo/demo.spec.ts',
        'screenshots/card.ts',
        'vite.config.ts',
      ]),
    );
    expect(FILES.filter((path) => path.startsWith('src/theme/'))).toEqual([]);
    expect(FILES).not.toContain('src/api/schema.d.ts');
  });

  it('sees a colour in a string, an attribute and every part of a template, and none in a comment', () => {
    const code = [
      "const a = '1px solid #ff0000';",
      "const b = '#fff';",
      'const c = `0 0 4px RGBA(0, 0, 0, 0.5)`;',
      'const d = `${a} hsl(200, 50%, 50%) ${b}`;',
      'const e = <div color="#0077b6" />;',
      '// #123456 and rgb(1, 2, 3) in a comment',
      '/* #abcdef */',
      "const f = 'a#fff', g = '#ffff0', h = 'color-mix(in srgb, red, blue)';",
    ].join('\n');
    expect(coloursIn('planted.tsx', code).map(({ line }) => line)).toEqual([1, 2, 3, 4, 5]);
  });
});
