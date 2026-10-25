/**
 * What a Fluent control is painted in a state that jsdom cannot enter: it matches no `:hover`,
 * `:active` or `:focus-visible`, so `getComputedStyle` only ever shows the rest. The answer is
 * read from the rules Griffel inserted instead, which is where a Fluent component and a class
 * passed to it both write their colours for each state.
 *
 * The rules read are those whose selector is one of the element's classes followed only by
 * pseudo-classes. A rule applies when every pseudo-class it names holds in the state, and the
 * one that wins is the browser's: the most pseudo-classes, then the last in the document. Rules
 * inside `@media` (forced colours) and selectors that reach into a descendant are not read.
 */
export type FluentState = 'rest' | 'hover' | 'pointer press' | 'key press';

export const FLUENT_STATES: readonly FluentState[] = [
  'rest',
  'hover',
  'pointer press',
  'key press',
];

// A key press is `:active` on the focused control with the pointer elsewhere.
const HOLDS: Record<FluentState, readonly string[]> = {
  rest: [],
  hover: ['hover'],
  'pointer press': ['hover', 'active'],
  'key press': ['active', 'focus-visible'],
};

export function paintedIn(
  element: Element,
  state: FluentState,
  property: 'background-color' | 'color',
): string | undefined {
  const classes = new Set(element.classList);
  let winner: { weight: number; value: string } | undefined;
  // The sheets in the order of their elements, which is the cascade's. jsdom lists
  // `document.styleSheets` in the order they were made, and Griffel can put a sheet made later in
  // front of one made earlier: its reset rules always go first, under every atomic class.
  for (const style of Array.from(document.querySelectorAll('style'))) {
    for (const rule of Array.from(style.sheet?.cssRules ?? [])) {
      if (!(rule instanceof CSSStyleRule)) continue;
      const value = rule.style.getPropertyValue(property);
      if (value === '') continue;
      for (const selector of rule.selectorText.split(',')) {
        const match = /^\.([\w-]+)((?::[a-z-]+)*)$/.exec(selector.trim());
        if (match === null || !classes.has(match[1])) continue;
        const pseudos = match[2].split(':').filter(Boolean);
        if (!pseudos.every((pseudo) => HOLDS[state].includes(pseudo))) continue;
        if (winner === undefined || pseudos.length >= winner.weight) {
          winner = { weight: pseudos.length, value };
        }
      }
    }
  }
  return winner?.value;
}
