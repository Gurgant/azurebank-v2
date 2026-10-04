import { readFileSync } from 'node:fs';
import { useEffect } from 'react';
import { act } from '@testing-library/react';
import { Route, Routes } from 'react-router-dom';
import { beforeEach, describe, expect, it } from 'vitest';
import { enableDemoMode } from '../../test/demoMode';
import { renderWithProviders } from '../../test/renderWithProviders';
import { ClosedInDemo } from './ClosedInDemo';

/*
  A page the demo closes: with the demo's tag on the page its address leads to the sign-in page and
  the page itself is never put on screen; without the tag it is the page it was.

  Three routes and three markers stand in for the app's, because the wrapper does the same whatever
  page it is given. That the app gives it the page of its /register route, and of no other, is
  asked of the app's source in the last test.
*/

/** How many times the closed page has been put on screen in this test. */
const put = { theRegisterPage: 0 };

beforeEach(() => {
  put.theRegisterPage = 0;
});

function RegisterMarker() {
  useEffect(() => {
    put.theRegisterPage += 1;
  }, []);
  return <p data-marker="">THE REGISTER PAGE</p>;
}

function ThreeRoutes() {
  return (
    <Routes>
      <Route path="/login" element={<p data-marker="">THE SIGN-IN PAGE</p>} />
      <Route
        path="/register"
        element={
          <ClosedInDemo>
            <RegisterMarker />
          </ClosedInDemo>
        }
      />
      <Route path="*" element={<p data-marker="">ANOTHER PAGE</p>} />
    </Routes>
  );
}

/** The markers on screen. */
function shown(): string[] {
  return Array.from(
    document.querySelectorAll('[data-marker]'),
    (marker) => marker.textContent ?? '',
  );
}

/**
 * One turn of the event loop, with whatever the router does in it drawn.
 *
 * A redirect is a navigation, and a router may finish one after the render that asked for it. Every
 * test here waits this same turn before it looks, the first one too, which sees its redirect: so
 * "still on /register" in the tests that expect no redirect is not a look taken too early.
 */
async function aTurn() {
  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 0));
  });
}

/**
 * Opens /register in a tab that was on another page before it. The address carries a query and a
 * fragment, so that a test can say what became of them.
 */
async function openRegister() {
  const { router } = renderWithProviders(<ThreeRoutes />, {
    routerEntries: ['/about', '/register?next=%2Fdashboard#frag'],
  });
  await aTurn();
  return router;
}

describe('a page the demo closes', () => {
  it('with the tag, /register leads to the sign-in page', async () => {
    enableDemoMode();

    const router = await openRegister();
    const landed = {
      shown: shown(),
      path: router.state.location.pathname,
      // Nothing goes along with the redirect: not the query and the fragment the closed address
      // carried, and no state. The sign-in page sends a visitor who signs in to the `from` of the
      // state it is handed (src/pages/LoginPage.test.tsx, "lands where the visitor was going"), so
      // a state that named this address would send them back to the closed address.
      search: router.state.location.search,
      hash: router.state.location.hash,
      carried: router.state.location.state,
      arrivedBy: router.state.historyAction,
      registerPagePut: put.theRegisterPage,
    };

    // Back from there: the closed address is not left in the history for Back to bounce off.
    await act(async () => {
      await router.navigate(-1);
    });
    await aTurn();

    expect({
      ...landed,
      backLeadsTo: { shown: shown(), path: router.state.location.pathname },
    }).toStrictEqual({
      shown: ['THE SIGN-IN PAGE'],
      path: '/login',
      search: '',
      hash: '',
      carried: null,
      arrivedBy: 'REPLACE',
      registerPagePut: 0,
      backLeadsTo: { shown: ['ANOTHER PAGE'], path: '/about' },
    });
  });

  it('without the tag, /register is the page it was', async () => {
    // CONTROL: green before this change
    const router = await openRegister();

    expect({
      demoTagsOnThePage: document.querySelectorAll('meta[name="azurebank-demo"]').length,
      shown: shown(),
      path: router.state.location.pathname,
      search: router.state.location.search,
      hash: router.state.location.hash,
      arrivedBy: router.state.historyAction,
      registerPagePut: put.theRegisterPage,
    }).toStrictEqual({
      demoTagsOnThePage: 0,
      shown: ['THE REGISTER PAGE'],
      path: '/register',
      search: '?next=%2Fdashboard',
      hash: '#frag',
      arrivedBy: 'POP',
      registerPagePut: 1,
    });
  });

  it('with a tag that does not say true, /register is the page it was', async () => {
    // CONTROL: green before this change
    // The tag is there and says the demo is off: its presence alone closes nothing.
    document.head.insertAdjacentHTML('beforeend', '<meta name="azurebank-demo" content="false">');

    const router = await openRegister();

    expect({
      demoTagsOnThePage: document.querySelectorAll('meta[name="azurebank-demo"]').length,
      shown: shown(),
      path: router.state.location.pathname,
      search: router.state.location.search,
      hash: router.state.location.hash,
      arrivedBy: router.state.historyAction,
      registerPagePut: put.theRegisterPage,
    }).toStrictEqual({
      demoTagsOnThePage: 1,
      shown: ['THE REGISTER PAGE'],
      path: '/register',
      search: '?next=%2Fdashboard',
      hash: '#frag',
      arrivedBy: 'POP',
      registerPagePut: 1,
    });
  });

  it('is wired into App: the /register route draws its page inside it, and no other route does', () => {
    // Against the source, as route-announcer.test.tsx does: App's router is module-scope and
    // unexported. Comments are stripped first, because App.tsx explains its routes in prose.
    const sourceOf = (file: string) =>
      readFileSync(file, 'utf8')
        .replace(/\/\*[\s\S]*?\*\//g, '')
        .replace(/^\s*\/\/.*$/gm, '');
    const app = sourceOf('src/App.tsx');
    // The index App names when it imports the wrapper: `./features/demo`, read from src/App.tsx.
    const index = sourceOf('src/features/demo/index.ts');
    const pathOf = (route: string) => /path="([^"]+)"/.exec(route)?.[1] ?? null;
    const routes = app.split('<Route').filter((route) => pathOf(route) !== null);
    const register = routes.filter((route) => pathOf(route) === '/register');

    expect({
      closed: routes.filter((route) => route.includes('<ClosedInDemo>')).map(pathOf),
      registerDraws: register.map(
        (route) =>
          /element=\{\s*([\s\S]*?)\s*\}\s*\/>/.exec(route)?.[1]?.replace(/\s+/g, '') ?? null,
      ),
      registerPagesInApp: app.split('<RegisterPage').length - 1,
      // `closed` sees a route only where its path is typed between double quotes. The count sees
      // the wrapper wherever App draws it.
      wrappersInApp: app.split('<ClosedInDemo').length - 1,
      // And the name App draws is the wrapper tested above, not another component of that name:
      // App takes it from the demo folder's index, and the index takes it from the file this test
      // imports it from.
      appTakesTheWrapperFrom: /import \{ ClosedInDemo \} from '([^']+)';/.exec(app)?.[1] ?? null,
      theIndexTakesItFrom: /export \{ ClosedInDemo \} from '([^']+)';/.exec(index)?.[1] ?? null,
    }).toStrictEqual({
      closed: ['/register'],
      registerDraws: ['<ClosedInDemo><RegisterPage/></ClosedInDemo>'],
      registerPagesInApp: 1,
      wrappersInApp: 1,
      appTakesTheWrapperFrom: './features/demo',
      theIndexTakesItFrom: './ClosedInDemo',
    });
  });
});
