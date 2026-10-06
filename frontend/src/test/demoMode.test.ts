import { describe, expect, it } from 'vitest';
import { writeDemoCopy } from '../features/demo/demoCopyStorage';
import { isDemoMode } from '../features/demo/demoMode';
import { enableDemoMode, rememberDemoCopy, resetDemoMode } from './demoMode';

/*
  The switch tests use to turn the demo on, and who turns it off.

  The first two tests are a pair and their order is the point: the first leaves the demo on, on
  purpose, and the second says what the next test finds. Nothing in this file removes the tag
  between them, so the second can only be green because `setup.ts` did.
*/
const DEMO_TAGS = 'meta[name="azurebank-demo"]';

function demoTagsOnThePage(): number {
  return document.querySelectorAll(DEMO_TAGS).length;
}

let theTestBeforeLeftTheTag = false;

describe('the switch for the demo', () => {
  it('turns the demo on for the test that asks', () => {
    enableDemoMode();
    // Written down for the next test, which has nothing to say unless this one ran before it and
    // ended with the tag on the page.
    theTestBeforeLeftTheTag = demoTagsOnThePage() === 1;

    expect(isDemoMode()).toBe(true);
    // Left on, on purpose.
  });

  it('…and the next test starts with it off', () => {
    expect({
      theTestBeforeLeftTheTag,
      demoTagsOnThePage: demoTagsOnThePage(),
      on: isDemoMode(),
    }).toStrictEqual({
      theTestBeforeLeftTheTag: true,
      demoTagsOnThePage: 0,
      on: false,
    });
  });

  it('turning it off removes every tag of that name, and no other tag', () => {
    document.head.insertAdjacentHTML(
      'beforeend',
      '<meta name="azurebank-demo-notes" content="true">',
    );
    const anotherTag = document.head.lastElementChild;
    const atFirst = demoTagsOnThePage();
    enableDemoMode();
    enableDemoMode();
    document.head.insertAdjacentHTML('beforeend', '<meta name="azurebank-demo" content="false">');
    const added = demoTagsOnThePage() - atFirst;

    resetDemoMode();

    const seen = {
      added,
      left: demoTagsOnThePage(),
      theOtherTagIsStillThere: anotherTag !== null && document.head.contains(anotherTag),
    };
    anotherTag?.remove();

    expect(seen).toStrictEqual({ added: 3, left: 0, theOtherTagIsStillThere: true });
  });
});

/*
  The other helper: a claimed copy left in the browser with no product code in between, for a test
  that starts where a reload would, or where another tab left things. It is only worth having
  while what it leaves is what the product would have left, so the two are compared here, as the
  strings the key holds. The key is typed out a third time: it is the product's and the helper's
  both, or this test is red.
*/
describe('the copy a test leaves in the browser', () => {
  it('is the one the product would have left, under the same key', () => {
    const copy = {
      email: 'demo-k7m2x9q4w8e1r5t3@azurebank.example',
      password: 'Xk7p-Rm3w-Hn8d-Tq5v',
      pin: '123456',
      contacts: ['jane_k7m2', 'mike_k7m2'],
      expiresAt: '2026-10-05T09:00:00.000Z',
    };

    rememberDemoCopy(copy);
    const leftByTheHelper = localStorage.getItem('azurebank.demoCopy');
    localStorage.clear();
    writeDemoCopy(copy);
    const leftByTheProduct = localStorage.getItem('azurebank.demoCopy');

    expect(leftByTheProduct).toContain('"password":"Xk7p-Rm3w-Hn8d-Tq5v"');
    expect(leftByTheHelper).toBe(leftByTheProduct);
  });
});
