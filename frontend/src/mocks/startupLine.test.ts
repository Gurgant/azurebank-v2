import { describe, expect, it } from 'vitest';
import { enableDemoMode } from '../test/demoMode';
import { mockStartupLine } from './startupLine';

/**
 * What the console says once the mock is on (`npm run dev:mock`): how to get in.
 *
 * The mock has two ways in, and the demo's tag chooses between them (`accountForLogin` in
 * src/mocks/handlers.ts): without the tag it signs its own user in; with it, claimed copies only,
 * and its own user is answered 401 like an address nobody has
 * (src/mocks/demoClaimHandler.test.ts holds both). So the line that tells a developer how to sign
 * in has to follow the tag too: the pair it names without the tag is the pair the mock refuses
 * with it.
 *
 * The lines are typed out here, the dash of the first as U+2014.
 */
describe("the mock's line in the console", () => {
  it("without the tag it names the mock's own user, password and PIN", () => {
    expect(mockStartupLine()).toBe(
      '[MSW] Mock backend ON — sign in with demo@azurebank.dev / Password1! (PIN 123456)',
    );
  });

  it('with the tag it says how the demo is entered, and names no pair the mock would refuse', () => {
    enableDemoMode();

    const line = mockStartupLine();

    expect({
      line,
      namesTheMocksOwnUser: line.includes('demo@azurebank.dev'),
      namesItsPassword: line.includes('Password1!'),
    }).toEqual({
      line: '[MSW] Mock backend ON, as the public demo: press "Try the demo" (PIN 123456)',
      namesTheMocksOwnUser: false,
      namesItsPassword: false,
    });
  });
});
