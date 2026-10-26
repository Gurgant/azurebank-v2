/*
  The page's style rules, each one once, in the order of their style elements.

  A Griffel renderer remembers the rules it has written and writes each only once, but two
  renderers each write their own copy. The app's renderer is made when `ThemeProvider` is
  evaluated, and a file that empties the module registry (`not-found.test.tsx`,
  `fidelity.test.ts` and `mediaQueryOrder.test.tsx` do) has the next import make another.
  Without isolation the files of a worker share one page, so every file after that one finds the
  earlier renderer's copies there as well.
  Measured: after `not-found.test.tsx` in the same worker, the class that gives the shell its
  bottom padding matched three rules instead of one.

  The copies are the same text, because a Griffel class is named after its declaration, so keeping
  the first of each changes no value a rule gives and leaves a count of rules what it was.
*/
export function pageStyleRules(): CSSRule[] {
  const seen = new Set<string>();
  return Array.from(document.querySelectorAll('style'))
    .flatMap((element) => Array.from(element.sheet?.cssRules ?? []))
    .filter((rule) => {
      if (seen.has(rule.cssText)) return false;
      seen.add(rule.cssText);
      return true;
    });
}
