import { afterEach, describe, expect, it } from 'vitest';
import { isDemoMode } from './demoMode';

/*
  Whether the page is the demo, read from the page itself.

  Each tag below is typed out as the markup a page would carry, and is not built from anything the
  product exports: a test that took the tag's name from `demoMode.ts` would pass whatever name the
  function looked for.
*/
const DEMO_TAGS = 'meta[name="azurebank-demo"]';

const put: Element[] = [];

/** Puts one piece of markup at the end of the page's head and hands back the element it made. */
function putInHead(markup: string): Element {
  document.head.insertAdjacentHTML('beforeend', markup);
  const element = document.head.lastElementChild;
  if (element === null) throw new Error(`Nothing was added to the head by: ${markup}`);
  put.push(element);
  return element;
}

/** What the function answers with this markup, and nothing else of this file's, on the page. */
function withOnly(markup: string) {
  const element = putInHead(markup);
  const seen = {
    demoTagsOnThePage: document.querySelectorAll(DEMO_TAGS).length,
    on: isDemoMode(),
  };
  element.remove();
  return seen;
}

describe('whether the page is the demo', () => {
  // The tests of this file share one page, so the file takes out of the head whatever its tests
  // put there.
  afterEach(() => {
    for (const element of put.splice(0)) element.remove();
  });

  it('is on when the page carries the tag', () => {
    putInHead('<meta name="azurebank-demo" content="true">');

    expect(isDemoMode()).toBe(true);
  });

  it('reads the page when it is asked, not when it was loaded', () => {
    const before = isDemoMode();
    const tag = putInHead('<meta name="azurebank-demo" content="true">');
    const withTheTag = isDemoMode();
    tag.remove();
    const afterItWasRemoved = isDemoMode();

    expect({ before, withTheTag, afterItWasRemoved }).toStrictEqual({
      before: false,
      withTheTag: true,
      afterItWasRemoved: false,
    });
  });

  it('is off without the tag', () => {
    // CONTROL: green before this change
    expect(document.querySelectorAll(DEMO_TAGS)).toHaveLength(0);
    expect(isDemoMode()).toBe(false);
  });

  it('is off for a tag that does not say true', () => {
    // CONTROL: green before this change
    // Each tag is on the page while the function is asked (`demoTagsOnThePage: 1`), so an "off"
    // here is an answer about that tag and not about an empty head.
    const off = { demoTagsOnThePage: 1, on: false };

    expect({
      saysFalse: withOnly('<meta name="azurebank-demo" content="false">'),
      noContent: withOnly('<meta name="azurebank-demo">'),
      emptyContent: withOnly('<meta name="azurebank-demo" content="">'),
      capitalised: withOnly('<meta name="azurebank-demo" content="True">'),
      one: withOnly('<meta name="azurebank-demo" content="1">'),
    }).toStrictEqual({
      saysFalse: off,
      noContent: off,
      emptyContent: off,
      capitalised: off,
      one: off,
    });
  });

  it('is off for a tag of another name that says true', () => {
    // CONTROL: green before this change
    const other = putInHead('<meta name="azurebank-demo-notes" content="true">');

    expect(document.head.contains(other)).toBe(true);
    expect(isDemoMode()).toBe(false);
  });
});
