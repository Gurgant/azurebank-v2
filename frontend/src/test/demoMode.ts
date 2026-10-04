/*
  The demo, for the one test that asks for it.

  The product decides whether the page is the demo by reading a tag on the page
  (`features/demo/demoMode.ts`), so a test turns the demo on by putting that tag on the test page,
  and `setup.ts` takes it off again after every test: the tests of a file share one page, and a tag
  left behind would turn the demo on for every test after it.

  The tag's name is typed out here and not imported from the product. A helper that asked the
  product for the name would follow it anywhere; this one stops turning the demo on the day the
  product reads another tag, and the tests that need the demo say so.
*/
const DEMO_TAG_NAME = 'azurebank-demo';

/** Puts `<meta name="azurebank-demo" content="true">` at the end of the page's head. */
export function enableDemoMode(): void {
  const tag = document.createElement('meta');
  tag.setAttribute('name', DEMO_TAG_NAME);
  tag.setAttribute('content', 'true');
  document.head.append(tag);
}

/** Removes every `meta` of that name from the page, whatever it says and whoever put it there. */
export function resetDemoMode(): void {
  for (const tag of document.querySelectorAll(`meta[name="${DEMO_TAG_NAME}"]`)) tag.remove();
}
