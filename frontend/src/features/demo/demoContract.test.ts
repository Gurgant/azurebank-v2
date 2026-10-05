import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '../../mocks/server';
import { makeTestStore } from '../../test/renderWithProviders';
import { apiSlice } from '../api/apiSlice';
import { isDemoMode } from './demoMode';

/**
 * The demo's names, held against the backend's source.
 *
 * The app and the backend agree on four things by spelling them the same in two languages: the
 * tag the BFF puts in the page, the route a claim is sent to, the two refusals the app has words
 * of its own for, and the sentences the API gives those refusals, which this suite's fixtures
 * type out. No job of CI runs the demo in a browser, so if one side changed its spelling nothing
 * there would fail: every test here answers with the mock, which spells things as the app does.
 * These read the backend's files as text and fail on the name that moved. The manner is
 * src/api/timeoutChain.test.ts's, and so are the plain relative paths: Vitest runs from the
 * frontend root.
 *
 * What a visitor would have met without them, each read from the code and not run: the page
 * without its demo (the tag); a claim answered 404 (the route); the house's fallback sentence in
 * place of the one written for the refusal (the codes).
 *
 * Every test here was green when it was written: they hold what is, on both sides. Each was
 * watched failing on a name misspelled in what it expects, and again on a name misspelled in the
 * app's own file or in a fixture.
 */
const ERROR_CODES = '../backend/src/AzureBank.Shared/Constants/ErrorCodes.cs';
const REFUSALS = '../backend/src/AzureBank.Shared/Exceptions/DemoRefusalException.cs';
const BFF_AUTH_CONTROLLER = '../backend/src/AzureBank.Bff/Controllers/BffAuthController.cs';
const SPA_HOSTING = '../backend/src/AzureBank.Bff/Extensions/SpaHostingExtensions.cs';

const read = (path: string) => readFileSync(path, 'utf8');

/** `public const string Name = "value";` in a C# file, as name and value. */
function constantsOf(source: string): Record<string, string> {
  const found: Record<string, string> = {};
  for (const [, name, value] of source.matchAll(/const string (\w+) = "((?:[^"\\]|\\.)*)";/g)) {
    // A C# string's two escapes that these constants use: a quote and a backslash.
    found[name] = value.replace(/\\(["\\])/g, '$1');
  }
  return found;
}

describe("the demo's tag", () => {
  it('as the BFF writes it turns the demo on for the app', () => {
    // CONTROL: green before this change
    const tag = constantsOf(read(SPA_HOSTING)).DemoTag;
    // Typed out: what src/features/demo/demoMode.ts says it reads.
    expect(tag).toBe('<meta name="azurebank-demo" content="true">');

    // And asked of the app itself, on a page that carries the BFF's own text in its head.
    const before = isDemoMode();
    document.head.insertAdjacentHTML('beforeend', tag);
    try {
      expect({ before, withTheBffsTag: isDemoMode() }).toEqual({
        before: false,
        withTheBffsTag: true,
      });
    } finally {
      // What was put there is taken off here as well: the suite's teardown goes by the name.
      document.head.lastElementChild?.remove();
    }
  });
});

describe("the claim's route", () => {
  /** The route of the action `ClaimDemoCopy`: the controller's own, and the action's under it. */
  function claimRoute() {
    const controller = read(BFF_AUTH_CONTROLLER);
    const ofTheController = /\[Route\("([^"]+)"\)\]\s*public class BffAuthController\b/.exec(
      controller,
    )?.[1];
    const action = controller.indexOf(' ClaimDemoCopy(');
    const attribute = controller.lastIndexOf('[HttpPost("', action);
    const ofTheAction = /^\[HttpPost\("([^"]+)"\)\]/.exec(controller.slice(attribute))?.[1];
    // Attributes and comments lie between the two, and no other member: no brace.
    const between = controller.slice(attribute, action);
    return {
      route: `/${ofTheController}/${ofTheAction}`,
      theAttributeIsThisActions: action !== -1 && attribute !== -1 && !/[{}]/.test(between),
    };
  }

  it("is where the app's claim goes", async () => {
    // CONTROL: green before this change
    const { route, theAttributeIsThisActions } = claimRoute();
    let askedThere = 0;
    // Armed on the backend's route. A claim sent anywhere else meets the mock's own handler or
    // none, and this count stays 0. The answer is the one the route gives with the demo off.
    server.use(
      http.post(`*${route}`, () => {
        askedThere += 1;
        return new HttpResponse(null, { status: 404 });
      }),
    );

    await makeTestStore().dispatch(apiSlice.endpoints.claimDemoCopy.initiate());

    expect({ route, theAttributeIsThisActions, askedThere }).toEqual({
      route: '/bff/auth/demo/claim',
      theAttributeIsThisActions: true,
      askedThere: 1,
    });
  });
});

describe("the claim's two refusals the app words", () => {
  /** Every code of the demo's that a file of the app names in a string. */
  const demoCodesIn = (path: string) =>
    [...new Set([...read(path).matchAll(/'(DEMO_[A-Z_]+)'/g)].map(([, code]) => code))].sort();

  it('are codes the backend has, under the spelling the app asks for', () => {
    // CONTROL: green before this change
    const backendCodes = Object.values(constantsOf(read(ERROR_CODES)));

    expect({
      theSignInPage: demoCodesIn('src/pages/LoginPage.tsx'),
      theDialog: demoCodesIn('src/features/demo/StartOverDialog.tsx'),
    }).toEqual({
      theSignInPage: ['DEMO_DAILY_LIMIT', 'DEMO_POOL_EMPTY'],
      theDialog: ['DEMO_DAILY_LIMIT', 'DEMO_POOL_EMPTY'],
    });
    expect({
      DEMO_DAILY_LIMIT: backendCodes.includes('DEMO_DAILY_LIMIT'),
      DEMO_POOL_EMPTY: backendCodes.includes('DEMO_POOL_EMPTY'),
    }).toEqual({ DEMO_DAILY_LIMIT: true, DEMO_POOL_EMPTY: true });
  });

  it("are given the API's own sentences by every fixture that types one out", () => {
    // CONTROL: green before this change
    /*
      The API's sentence for each of the demo's refusals, worked out from its source: the
      exception's factories say which sentence goes with which code's constant
      (`new(PoolEmptyDetail, ErrorCodes.DemoPoolEmpty)`), and the two files give the constants.
    */
    const refusals = read(REFUSALS);
    const sentences = constantsOf(refusals);
    const codes = constantsOf(read(ERROR_CODES));
    const theApis: Record<string, string> = {};
    for (const [, sentence, code] of refusals.matchAll(/new\((\w+Detail), ErrorCodes\.(\w+)\)/g)) {
      theApis[codes[code]] = sentences[sentence];
    }
    // Typed out, so that a sentence reworded on the server is met here first.
    expect(theApis).toEqual({
      DEMO_POOL_EMPTY: 'All demo copies are in use right now. Please try again later.',
      DEMO_DAILY_LIMIT: 'This network has used its demo copies for today. Please try again later.',
      DEMO_COPY_LIMIT:
        'This demo copy has reached its limit of changes. Start over to get a fresh copy.',
    });

    // Every `errorCode: 'DEMO_...'` that is followed by a `detail`, in the mock and in the tests
    // that arm a refusal of their own: which code, and whether the sentence is the API's.
    const fixtures = (path: string) =>
      [...read(path).matchAll(/errorCode: '(DEMO_[A-Z_]+)',\s*detail:\s*'([^']*)'/g)].map(
        ([, code, detail]) => `${code}: ${detail === theApis[code] ? "the API's" : detail}`,
      );
    expect({
      theMock: fixtures('src/mocks/handlers.ts'),
      theMocksTest: fixtures('src/mocks/demoClaimHandler.test.ts'),
      theClaimsTest: fixtures('src/features/demo/claim.test.ts'),
      theDialogsTest: fixtures('src/features/demo/StartOverDialog.test.tsx'),
      theSignInPagesTest: fixtures('src/pages/LoginPage.test.tsx'),
    }).toEqual({
      theMock: ["DEMO_POOL_EMPTY: the API's"],
      theMocksTest: ["DEMO_POOL_EMPTY: the API's"],
      theClaimsTest: ["DEMO_POOL_EMPTY: the API's", "DEMO_POOL_EMPTY: the API's"],
      theDialogsTest: [
        "DEMO_POOL_EMPTY: the API's",
        "DEMO_DAILY_LIMIT: the API's",
        "DEMO_COPY_LIMIT: the API's",
      ],
      theSignInPagesTest: ["DEMO_POOL_EMPTY: the API's", "DEMO_DAILY_LIMIT: the API's"],
    });
  });
});
