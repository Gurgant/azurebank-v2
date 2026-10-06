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

/*
  The key the browser keeps a claimed copy under, typed out here for the same reason as the tag's
  name: a helper that asked the product for it would follow the product to any key.
*/
const DEMO_COPY_KEY = 'azurebank.demoCopy';

/**
 * Leaves a claimed copy in the browser as the product would have: the key holds
 * `{ v: 1, email, password, pin, contacts, expiresAt }`.
 *
 * A raw write, and that is the point. The product's storage module
 * (`features/demo/demoCopyStorage.ts`) is not told and nobody who listens to it is called, so this
 * is what a reload finds, or what another tab left. `setup.ts` removes the key again after every
 * test. `demoMode.test.ts`, beside this file, compares what this leaves with what the product
 * leaves.
 */
export function rememberDemoCopy(copy: {
  email: string;
  password: string;
  pin: string;
  contacts: string[];
  expiresAt: string;
}): void {
  localStorage.setItem(
    DEMO_COPY_KEY,
    JSON.stringify({
      v: 1,
      email: copy.email,
      password: copy.password,
      pin: copy.pin,
      contacts: copy.contacts,
      expiresAt: copy.expiresAt,
    }),
  );
}
