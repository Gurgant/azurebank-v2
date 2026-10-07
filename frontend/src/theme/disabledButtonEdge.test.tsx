import { render, screen } from '@testing-library/react';
import { Button, type ButtonProps } from '@fluentui/react-components';
import { describe, expect, it } from 'vitest';
import { darkPalette } from './darkPalette';
import { azureBankDarkTheme, azureBankLightTheme } from './fluentTheme';
import { ThemeProvider } from './ThemeProvider';
import { lightPalette } from './tokens';

/*
  A disabled button keeps a shape.

  Measured in Chromium on 2026-10-06, before this rule: the disabled main button ("Review
  Transfer") was drawn with a fill of #141414 on the dark page's #141414, and of #f0f0f0 on the
  light page's #f3f4f6 (1.04 to 1), with an edge the component library sets to transparent. It
  read as loose grey words. So a disabled button whose appearance has a shape gets an edge in
  the colour of its own disabled label, set once, where the app hands the library its theme.

  WHAT THIS FILE CAN HOLD, AND WHAT IT CANNOT. jsdom draws nothing, and its `getComputedStyle`
  does not follow the classes the library writes: asked for the top edge of a disabled secondary
  button it answered `rgba(0, 0, 0, 0)`, where a browser answers the library's stroke. So the
  tests read the style sheets themselves: of the rules that name one of the button's classes and
  set the property, the last in the page, which is the one a browser applies among rules of one
  class each. That gives the theme's variable the edge is drawn with, and the theme objects give
  that variable's colour in light and in dark. The colours a browser painted, the contrast
  between them, and that the button still looks switched off were looked at in Chromium, in
  both themes, and are in no test.

  Two limits of that reading, both met while writing it. The sheets are taken in the order of
  their `style` elements in the page: `document.styleSheets` lists them in the order they were
  made, which is another order, and by it the library's base fill came after the fill of a main
  button. And jsdom does not split a `border` or `border-color` declaration into its four sides,
  so an edge the library draws that way (a secondary button that can be pressed) reads here as
  `not set`: no rule of one side over the library's own.
*/

/** Every rule of the page that is not inside a media query, in the order a browser applies them. */
function rules(): CSSStyleRule[] {
  return Array.from(document.querySelectorAll('style'))
    .flatMap((element) => Array.from(element.sheet?.cssRules ?? []))
    .filter((rule): rule is CSSStyleRule => 'selectorText' in rule);
}

/**
 * What the sheets give `element` for `property`, at rest or in `state` (`':hover'`): the value
 * of the last rule whose selector is one of the element's classes, with that state and nothing
 * else. `undefined` when no rule sets that one property.
 */
function declared(element: Element, property: string, state = ''): string | undefined {
  const selectors = new Set(Array.from(element.classList).map((name) => `.${name}${state}`));
  return rules()
    .filter(
      (rule) =>
        rule.style.getPropertyValue(property) !== '' &&
        rule.selectorText.split(',').some((selector) => selectors.has(selector.trim())),
    )
    .map((rule) => rule.style.getPropertyValue(property))
    .at(-1);
}

/** The four edges of `element`, at rest, under the pointer and pressed: one value if all agree. */
function edge(element: Element): string[] {
  const sides = ['top', 'right', 'bottom', 'left'];
  const states = ['', ':hover', ':hover:active'];
  return [
    ...new Set(
      states.flatMap((state) =>
        sides.map((side) => declared(element, `border-${side}-color`, state) ?? 'not set'),
      ),
    ),
  ];
}

function show(buttons: Record<string, ButtonProps>) {
  render(
    <ThemeProvider>
      {Object.entries(buttons).map(([name, props]) => (
        <Button key={name} {...props}>
          {name}
        </Button>
      ))}
    </ThemeProvider>,
  );
  return (name: string) => screen.getByRole('button', { name });
}

/** WCAG 2.1 relative luminance of `#rrggbb`. */
function luminance(hex: string): number {
  const channels = [1, 3, 5].map((i) => {
    const c = parseInt(hex.slice(i, i + 2), 16) / 255;
    return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * channels[0] + 0.7152 * channels[1] + 0.0722 * channels[2];
}

function contrast(a: string, b: string): number {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
}

const THEMES = [
  { name: 'light', theme: azureBankLightTheme, canvas: lightPalette.surfaces.canvas },
  { name: 'dark', theme: azureBankDarkTheme, canvas: darkPalette.surfaces.canvas },
] as const;

/** The variable of the theme the label of a disabled button is drawn with, as a sheet names it. */
const LABEL = 'var(--colorNeutralForegroundDisabled)';

describe('the edge of a disabled button', () => {
  it('the sheets are read as a browser reads them: the helpers see what the library sets', () => {
    // CONTROL: green before this change
    const button = show({
      'secondary on': {},
      'main on': { appearance: 'primary' },
    });

    // If `declared` found nothing, or the wrong rule, the tests below could not be believed.
    expect({
      // The library's base fill, which every button carries...
      secondaryFill: declared(button('secondary on'), 'background-color'),
      // ...and the fill that comes after it in the page for a main button, under the pointer too.
      mainFill: declared(button('main on'), 'background-color'),
      mainFillUnderThePointer: declared(button('main on'), 'background-color', ':hover'),
      mainEdge: edge(button('main on')),
    }).toStrictEqual({
      secondaryFill: 'var(--colorNeutralBackground1)',
      mainFill: 'var(--colorBrandBackground)',
      mainFillUnderThePointer: 'var(--colorBrandBackgroundHover)',
      mainEdge: ['transparent'],
    });
  });

  it.each(['primary', 'secondary', 'outline'] as const)(
    'a disabled %s button has an edge in the colour of its label, under the pointer too',
    (appearance) => {
      const button = show({ off: { appearance, disabled: true } });

      expect({
        edge: edge(button('off')),
        label: declared(button('off'), 'color'),
      }).toStrictEqual({ edge: [LABEL], label: LABEL });
    },
  );

  it('a button that can be focused while disabled has the same edge', () => {
    const button = show({ off: { appearance: 'primary', disabledFocusable: true } });

    expect(edge(button('off'))).toStrictEqual([LABEL]);
  });

  it("the fill and the label of a disabled button are the library's, as they were", () => {
    // CONTROL: green before this change
    const button = show({
      main: { appearance: 'primary', disabled: true },
      secondary: { disabled: true },
    });

    expect(
      ['main', 'secondary'].map((name) => ({
        fill: declared(button(name), 'background-color'),
        label: declared(button(name), 'color'),
        cursor: declared(button(name), 'cursor'),
      })),
    ).toStrictEqual([
      { fill: 'var(--colorNeutralBackgroundDisabled)', label: LABEL, cursor: 'not-allowed' },
      { fill: 'var(--colorNeutralBackgroundDisabled)', label: LABEL, cursor: 'not-allowed' },
    ]);
  });

  it('a disabled button with no shape gets none: subtle and transparent stay bare', () => {
    // CONTROL: green before this change
    const button = show({
      subtle: { appearance: 'subtle', disabled: true },
      transparent: { appearance: 'transparent', disabled: true },
    });

    expect({
      subtle: edge(button('subtle')),
      transparent: edge(button('transparent')),
    }).toStrictEqual({ subtle: ['transparent'], transparent: ['transparent'] });
  });

  it('a button that can be pressed is as it was', () => {
    // CONTROL: green before this change
    const button = show({ main: { appearance: 'primary' }, secondary: {} });

    expect({
      main: edge(button('main')),
      // The library's own stroke, which it sets with `border`: nothing of one side is over it.
      secondary: edge(button('secondary')),
    }).toStrictEqual({ main: ['transparent'], secondary: ['not set'] });
  });

  it.each(THEMES)(
    'in the $name theme the edge of a disabled main button shows on the page and on a card',
    ({ theme, canvas }) => {
      const button = show({ off: { appearance: 'primary', disabled: true } });
      // The theme's variable the edge is drawn with, read from the sheet and not typed here.
      const [drawnWith, ...others] = edge(button('off'));
      const token = /^var\(--(\w+)\)$/.exec(drawnWith)?.[1];
      const colours = theme as unknown as Record<string, string>;
      const colour = token === undefined ? undefined : colours[token];

      expect({
        drawnWith,
        others,
        isAColourOfTheTheme: /^#[0-9a-f]{6}$/i.test(colour ?? ''),
      }).toStrictEqual({
        drawnWith: LABEL,
        others: [],
        isAColourOfTheTheme: true,
      });
      // 1.5 to 1 is the aim taken for a shape that has to be seen and not operated: a disabled
      // control is outside WCAG 1.4.11's 3 to 1. Against the page, a card (the library's first
      // background) and the button's own fill.
      expect({
        page: contrast(colour!, canvas) >= 1.5,
        card: contrast(colour!, theme.colorNeutralBackground1) >= 1.5,
        fill: contrast(colour!, theme.colorNeutralBackgroundDisabled) >= 1.5,
      }).toStrictEqual({ page: true, card: true, fill: true });
    },
  );
});
